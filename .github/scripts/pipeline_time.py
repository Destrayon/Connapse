"""PrPipeline_WallTimeUnder6Min: the proof check for epic #783, problem P10 (#789), CON-223.

It fails a PR only when its tests add time against main's last few runs: the new tests, the tests
that got much slower or faster, and the tests that are gone, each read on a median runner. The
pipeline's wall time against its 6-minute budget is printed as a warning, with main's recent wall
times as its trend, and fails nothing.

Wall time depends on which runner GitHub hands out. The same code on two runners differed by up to 46 s
of wall in 18 pairs of runs on the same tree (2026-10-27 to 11-03), and eleven runs failed this check on
wall time with no slower tests: a slow build, a slow Codecov upload, GitHub taking 85 s to close a job.
Four patches (CON-87, CON-127, CON-141, CON-223's first try) each discounted one more of those, and the
next one still failed #1116's merge at 390 s. The summed test time moves with the runner too, by up to
176 s between those pairs, and by 44 s even read on a median runner. Counting only the tests that
changed, it moved by at most 7 s, while the epic that added links and memory read +30 s.

A median runner: each job's durations divided by its runner factor, the median ratio of its tests'
durations to main's (CON-87's method for the Small tier).

Usage (in build-and-test, after downloading this run's test-results-* artifacts; GH_TOKEN set):
  pipeline_time.py --results . --max-added-seconds 45 --max-wall-seconds 360 --current build-and-test
"""
import argparse
import glob
import json
import os
import re
import statistics
import subprocess
import sys
import tempfile
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from test_budgets import durations  # noqa: E402

# Which job (and so which runner) wrote each TRX file: its runner factor is that job's. A file not
# listed here is its own job.
JOB_OF = [
    (re.compile(r"^small-tests\.trx$"), "small"),
    (re.compile(r"^medium-tests\.trx$"), "medium"),
    (re.compile(r"^medium-tests-shard-(\d+)\.trx$"), r"integration (\1)"),
]

# The speed gates' results: each gate holds its own time limit, and main's artifacts carry these files
# in only some runs (none before 2026-10-31, then 17 of the next 35), so their tests would read as new
# whenever they came back. Left out.
SKIPPED = re.compile(r"^(memory-)?gate-tests\.trx$")

# A test whose median-runner duration stays inside this band around its usual one counts as unchanged:
# a test's own duration varies with the runner by more than the factor removes. Outside it, the whole
# difference counts. Picked from the replay: the band (2x + 2 s) keeps same-code reruns within 6 s.
BAND_RATIO = 2.0
BAND_SECONDS = 2.0

# The jobs whose test results the baseline needs: a main run where one of them didn't succeed has
# missing or partial results.
TEST_JOBS = ["small", "medium", "integration (1)", "integration (2)", "integration (3)"]

# How many of main's latest runs make the baseline: the median of five is steady against one slow one.
BASELINE_RUNS = 5

# A test must take this long on main to time its runner: shorter ones are mostly timer noise.
MIN_REFERENCE_SECONDS = 0.01

# Below this many tests timed against main, a job's runner factor says too little, and its durations
# are counted as they ran.
MIN_MATCHED = 50

# The slowest runner seen ran tests 1.44x main's median (small, 2026-11-02). A factor above this is read
# as this: past it, slower tests are the code's, not the runner's. So a PR that slows every test in a
# job alike, which a factor would otherwise absorb, still fails once it passes this.
MAX_FACTOR = 1.5


def job_of(path: str) -> str:
    name = os.path.basename(path)
    for pattern, job in JOB_OF:
        if pattern.match(name):
            return pattern.sub(job, name)
    return name


def results(root: str) -> dict[str, dict[str, float]]:
    """Each job's passed tests and their durations, from the TRX files under root."""
    by_job: dict[str, list[str]] = {}
    for path in glob.glob(os.path.join(root, "**", "*.trx"), recursive=True):
        if SKIPPED.match(os.path.basename(path)):
            continue
        by_job.setdefault(job_of(path), []).append(path)
    return {job: durations(sorted(paths)) for job, paths in by_job.items()}


def reference(runs: list[dict[str, dict[str, float]]]) -> dict[str, float]:
    """Each test's median duration over the runs it ran in, newest run first, for the tests main runs now:
    those in the newest run, or in at least half of them. A test that only the newest one ran came with
    a merge since, and a PR made after it carries it too. By test, not by job: the integration shards
    trade test classes as their timings change."""
    seen: dict[str, list[float]] = {}
    for run in runs:
        for tests in run.values():
            for name, seconds in tests.items():
                seen.setdefault(name, []).append(seconds)
    newest = {name for tests in runs[0].values() for name in tests} if runs else set()
    return {name: statistics.median(s) for name, s in seen.items() if name in newest or 2 * len(s) >= len(runs)}


def factor(tests: dict[str, float], ref: dict[str, float]) -> tuple[float, int]:
    """How much slower than main's median this job's runner ran, and over how many tests: 1 when too few
    tests ran on main to tell, and at most MAX_FACTOR."""
    ratios = [tests[n] / ref[n] for n in tests if ref.get(n, 0) >= MIN_REFERENCE_SECONDS]
    if len(ratios) < MIN_MATCHED:
        return 1.0, len(ratios)
    return min(statistics.median(ratios), MAX_FACTOR), len(ratios)


def normalized(run: dict[str, dict[str, float]], ref: dict[str, float]) -> dict[str, dict[str, float]]:
    """Each test's duration on a median runner: divided by its job's runner factor."""
    out = {}
    for job, tests in run.items():
        f, _ = factor(tests, ref)
        out[job] = {n: s / f for n, s in tests.items()}
    return out


def total(run: dict[str, dict[str, float]]) -> float:
    return sum(sum(tests.values()) for tests in run.values())


def changed(seconds: float, usual: float) -> bool:
    return seconds > BAND_RATIO * usual + BAND_SECONDS or seconds < (usual - BAND_SECONDS) / BAND_RATIO


def added(run: dict[str, dict[str, float]], baseline: list[dict[str, dict[str, float]]]
          ) -> tuple[float, float, dict[str, float], dict[str, float]]:
    """The test time this run's changes add on a median runner: every new test's duration, plus the
    difference of every test that ran well outside its usual time (see BAND_RATIO), less every test
    that's gone. Also the baseline's usual total, each counted test's share, and each job's factor.

    Only changed tests count: summing every test, unchanged ones too, carried each runner's leftover
    noise into the result, and the integration jobs run on two kinds of CPU whose waits don't scale alike,
    so the sum read 30 s high on a typical run and moved by up to 47 s between reruns of the same code."""
    ref = reference(baseline)
    usual = statistics.median(total(normalized(b, ref)) for b in baseline)
    mine = normalized(run, ref)
    share: dict[str, float] = {}
    for job, tests in mine.items():
        # A job slowed past MAX_FACTOR is slowed by the code, all its tests alike, each one maybe
        # inside the band: every test's difference counts.
        whole = factor(run[job], ref)[0] >= MAX_FACTOR
        share.update({n: s - ref.get(n, 0.0) for n, s in tests.items() if n not in ref or whole or changed(s, ref[n])})
    flat = {n for tests in mine.values() for n in tests}
    share.update({n: -s for n, s in ref.items() if n not in flat})
    return sum(share.values()), usual, share, {job: factor(t, ref)[0] for job, t in run.items()}


# GitHub, through the gh CLI the runners ship with. Mocked in the tests.

def gh_json(*args: str):
    return json.loads(subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout)


def jobs(run_id: str | int) -> list[dict]:
    """The latest attempt of every job in a run."""
    repo = os.environ["GITHUB_REPOSITORY"]
    return gh_json("api", f"repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100")["jobs"]


def main_runs() -> list[dict]:
    """main's latest push runs of this workflow, newest first."""
    repo, workflow = os.environ["GITHUB_REPOSITORY"], os.environ.get("GITHUB_WORKFLOW_REF", "")
    name = re.sub(r"@.*$", "", workflow).split("/")[-1] or "ci.yml"
    return gh_json("api", f"repos/{repo}/actions/workflows/{name}/runs?branch=main&event=push&per_page=30")["workflow_runs"]


def download(run_id: str | int, into: str) -> None:
    subprocess.run(["gh", "run", "download", str(run_id), "--pattern", "test-results-*", "--dir", into,
                    "--repo", os.environ["GITHUB_REPOSITORY"]], check=True, capture_output=True)


def baseline_runs(exclude: str) -> list[tuple[str, dict[str, dict[str, float]], float | None]]:
    """main's last BASELINE_RUNS runs whose test jobs all succeeded: each one's id, test results and
    pipeline wall time."""
    found = []
    for r in main_runs():
        if str(r["id"]) == exclude or r.get("status") != "completed":
            continue
        js = jobs(r["id"])
        if any(next((j["conclusion"] for j in js if j["name"] == t), None) != "success" for t in TEST_JOBS):
            continue
        with tempfile.TemporaryDirectory() as d:
            try:
                download(r["id"], d)
            except subprocess.CalledProcessError:
                continue  # its artifacts have expired
            run = results(d)
        if not all(t in run for t in TEST_JOBS):
            continue
        found.append((str(r["id"]), run, wall(js)))
        if len(found) == BASELINE_RUNS:
            break
    return found


def at(s: str) -> datetime:
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def wall(js: list[dict], current: str = "build-and-test", now: datetime | None = None) -> float | None:
    """The pipeline's wall time without the wait for runners: its longest job, start to finish, plus the
    job that runs last, to `now` if it is still running."""
    last = next((j for j in js if j["name"] == current), None)
    done = [j for j in js if j["name"] != current and j.get("started_at") and j.get("completed_at")]
    if not last or not last.get("started_at") or not done:
        return None
    end = now or (at(last["completed_at"]) if last.get("completed_at") else None)
    if end is None:
        return None
    longest = max((at(j["completed_at"]) - at(j["started_at"])).total_seconds() for j in done)
    return longest + (end - at(last["started_at"])).total_seconds()


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--results", required=True, help="where this run's test-results-* artifacts were downloaded")
    p.add_argument("--max-added-seconds", type=float, required=True,
                   help="the test time on a median runner a PR may add over main's")
    p.add_argument("--max-wall-seconds", type=float, required=True, help="the wall time over which to warn")
    p.add_argument("--current", required=True, help="the name of the job running this script")
    a = p.parse_args(argv)

    run = results(a.results)
    missing = [t for t in TEST_JOBS if t not in run]
    if missing:
        print(f"::error::no test results from {', '.join(missing)}; the test time can't be measured")
        return 1
    baseline = baseline_runs(exclude=os.environ.get("GITHUB_RUN_ID", ""))
    if len(baseline) < BASELINE_RUNS:
        print(f"::error::only {len(baseline)} of main's recent runs have every test job's results, and the baseline"
              f" needs {BASELINE_RUNS}; the test time can't be compared")
        return 1

    more, usual, share, factors = added(run, [b for _, b, _ in baseline])
    print(f"Test time on a median runner: {total(normalized(run, reference([b for _, b, _ in baseline]))):.0f} s;"
          f" main's usual {usual:.0f} s (median of runs {', '.join(i for i, _, _ in baseline)})")
    for job in sorted(factors):
        print(f"  {job}: runner factor {factors[job]:.2f}")
    print(f"  {len(share)} tests are new, gone, or outside {BAND_RATIO:g}x + {BAND_SECONDS:g} s of their usual time;"
          f" the ones that add or save the most:")
    for name, s in sorted(share.items(), key=lambda kv: -abs(kv[1]))[:10]:
        print(f"    {s:+7.1f} s  {name}")

    now = utcnow()
    mine = wall(jobs(os.environ["GITHUB_RUN_ID"]), a.current, now)
    trend = ", ".join(f"{w:.0f}" for _, _, w in reversed(baseline) if w is not None)
    if mine is not None:
        line = f"pipeline wall time {mine:.0f} s of {a.max_wall_seconds:g} s; main's last runs, oldest first: {trend} s"
        print(f"::warning::{line}, over the budget on this runner" if mine > a.max_wall_seconds else line)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(f"### Pipeline time\n\n| | This run | main's usual |\n|---|---|---|\n"
                    f"| Test time the changed tests add on a median runner (s) | {more:+.0f} | limit {a.max_added_seconds:g} |\n"
                    f"| Wall time (s) | {'?' if mine is None else f'{mine:.0f}'} | {trend} |\n")

    if more > a.max_added_seconds:
        print(f"::error::PrPipeline_WallTimeUnder6Min: the changed tests add {more:.0f} s over main's on a median"
              f" runner, over the {a.max_added_seconds:g} s allowed; the tests above add the most")
        return 1
    print(f"PrPipeline_WallTimeUnder6Min: the tests add {more:+.0f} s of the {a.max_added_seconds:g} s allowed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
