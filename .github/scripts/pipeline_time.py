"""PrPipeline_WallTimeUnder6Min: the proof check for epic #783, problem P10 (#789), CON-223.

It fails a PR only when its tests add time against main's five runs up to its base commit: the new
tests, the tests that got much slower or faster, and the tests that are gone, each read on a median
runner; or when the other tests, each inside its usual time, add a lot together. The pipeline's wall
time against its 6-minute budget is printed as a warning, with main's recent wall times as its trend,
and fails nothing.

What it can't see: a change that slows most of one job's tests alike, by less than MAX_FACTOR, reads
as a slower runner. The wall-time warning and its trend are where that shows.

Wall time depends on which runner GitHub hands out. The same code on two runners differed by up to 46 s
of wall in 18 pairs of runs on the same tree (2026-10-27 to 11-03), and eleven runs failed this check on
wall time with no slower tests: a slow build, a slow Codecov upload, GitHub taking 85 s to close a job.
Four patches (CON-87, CON-127, CON-141, CON-223's first try) each discounted one more of those, and the
next one still failed #1116's merge at 390 s. The summed test time moves with the runner too, by up to
176 s between those pairs, and by 44 s even read on a median runner. Counting only the tests that
changed, it moved by at most 7 s, while the epic that added links and memory read +30 s.

A median runner: each job's durations divided by its runner factor, the median ratio of its tests'
durations to main's (CON-87's method for the Small tier).

A PR into an epic or release branch is measured against that branch's own runs, which carry the tests
its earlier PRs added: measured against main, each tables-epic PR passed alone, and the next one failed
for the 48 s they had added together (CON-333). Until the branch has five runs, main's runs from before
its oldest one fill the baseline; with none, the baseline is main's, as for a PR into main.

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
import time
from datetime import datetime, timezone
from urllib.parse import quote

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from test_budgets import durations  # noqa: E402

# Which job (and so which runner) wrote each TRX file: its runner factor is that job's. A file not
# listed here is its own job.
JOB_OF = [
    (re.compile(r"^small-tests\.trx$"), "small"),
    (re.compile(r"^gate-tests\.trx$"), "small gates"),
    (re.compile(r"^medium-tests\.trx$"), "medium"),
    (re.compile(r"^medium-tests-shard-(\d+)\.trx$"), r"integration (\1)"),
    (re.compile(r"^memory-gate-tests\.trx$"), "integration (1) gates"),
]

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
    # Results of a file main's newest run didn't carry can't be compared: main's artifacts carried the
    # speed gates' files in only some runs before 2026-11-02, and their tests read as new each time.
    mine = {job: tests for job, tests in normalized(run, ref).items() if not baseline or job in baseline[0]}
    share: dict[str, float] = {}
    for job, tests in mine.items():
        # A job slowed past MAX_FACTOR is slowed by the code, all its tests alike, each one maybe
        # inside the band: every test's difference counts. Otherwise a test counts when it is outside
        # the band both as it ran and on a median runner: a test that mostly waits takes as long on a
        # fast CPU, and dividing it by that runner's factor alone would put it outside.
        whole = factor(run[job], ref)[0] >= MAX_FACTOR
        share.update({n: s - ref.get(n, 0.0) for n, s in tests.items()
                      if n not in ref or whole or (changed(s, ref[n]) and changed(run[job][n], ref[n]))})
    flat = {n for tests in mine.values() for n in tests}
    # A test is gone only if its file came: a missing file is a step that didn't run, not dropped tests.
    absent = {n for job, tests in (baseline[0] if baseline else {}).items() if job not in run for n in tests}
    share.update({n: -s for n, s in ref.items() if n not in flat and n not in absent})
    return sum(share.values()), usual, share, {job: factor(t, ref)[0] for job, t in run.items()}


def unbanded(run: dict[str, dict[str, float]], ref: dict[str, float]) -> float:
    """The median-runner time the tests added() doesn't count add up to against their usual time."""
    t = 0.0
    for job, tests in normalized(run, ref).items():
        if factor(run[job], ref)[0] >= MAX_FACTOR:
            continue
        t += sum(s - ref[n] for n, s in tests.items() if n in ref and not (changed(s, ref[n]) and changed(run[job][n], ref[n])))
    return t


def drift(run: dict[str, dict[str, float]], baseline: list[dict[str, dict[str, float]]]) -> float:
    """How much the tests inside the band add together, less what main's own runs add the same way: a
    slowdown spread over many tests, each too little to count alone. It read up to +51 s with no change
    in 47 replayed runs and moved by up to 55 s between reruns of the same code, so its limit is wider."""
    ref = reference(baseline)
    return unbanded(run, ref) - statistics.median(unbanded(b, ref) for b in baseline)


# GitHub, through the gh CLI the runners ship with. Mocked in the tests.

# A GitHub hiccup: a 5xx, a rate limit or a dropped connection, worth another try (CON-334). A 404 or an
# expired artifact is not, and fails at once.
TRANSIENT = re.compile(r"HTTP (5\d\d|429)|rate limit|timed? ?out|connection (reset|refused)|unexpected EOF"
                       r"|TLS handshake|no such host|network is unreachable", re.I)
RETRY_DELAYS = (2, 4, 8)  # seconds before each retry: four tries in all


def gh(*args: str) -> str:
    """The gh CLI's output, retried after a transient GitHub error so a brief outage doesn't fail the check."""
    for delay in (*RETRY_DELAYS, None):
        try:
            return subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout
        except subprocess.CalledProcessError as e:
            err = f"{e.stderr or ''}{e.stdout or ''}".strip()
            if delay is None or not TRANSIENT.search(err):
                raise
            print(f"::notice::gh {args[0]} failed ({err.splitlines()[-1]}); retrying in {delay} s")
            time.sleep(delay)
    raise AssertionError("unreachable")


def gh_json(*args: str):
    return json.loads(gh(*args))


def jobs(run_id: str | int) -> list[dict]:
    """The latest attempt of every job in a run."""
    repo = os.environ["GITHUB_REPOSITORY"]
    return gh_json("api", f"repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100")["jobs"]


def branch_runs(branch: str = "main") -> list[dict]:
    """The branch's latest push runs of this workflow, newest first."""
    repo, workflow = os.environ["GITHUB_REPOSITORY"], os.environ.get("GITHUB_WORKFLOW_REF", "")
    name = re.sub(r"@.*$", "", workflow).split("/")[-1] or "ci.yml"
    return gh_json("api", f"repos/{repo}/actions/workflows/{name}/runs?branch={quote(branch, safe='')}"
                          f"&event=push&per_page=30")["workflow_runs"]


def base_branch() -> str:
    """The branch whose runs make the baseline: a PR's base branch, or the branch a push went to."""
    return os.environ.get("GITHUB_BASE_REF") or os.environ.get("GITHUB_REF_NAME") or "main"


def download(run_id: str | int, into: str) -> None:
    gh("run", "download", str(run_id), "--pattern", "test-results-*", "--dir", into,
       "--repo", os.environ["GITHUB_REPOSITORY"])


def base_commit() -> str | None:
    """The base branch's commit this run's code is built on: the first parent of the commit checked out, the
    merge of a PR into its base or a push's commit. Read from the commit itself, which a shallow checkout
    holds though not its parents."""
    try:
        raw = subprocess.run(["git", "cat-file", "-p", "HEAD"], check=True, capture_output=True, text=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return None
    parent = re.search(r"^parent ([0-9a-f]{40})$", raw, re.M)
    return parent.group(1) if parent else None


def baseline_runs(exclude: str, base: str | None, branch: str = "main"
                  ) -> list[tuple[str, dict[str, dict[str, float]], float | None]]:
    """The branch's last BASELINE_RUNS runs whose test jobs all succeeded, from the run of `base` back: each
    one's id, test results and pipeline wall time. Not newer ones: a test merged since would read as
    one this run dropped, and its time as saved.

    A branch other than main with fewer runs of its own is filled with main's from before its oldest one,
    which don't carry what main got after the branch last took main in. A test's usual time is the median
    of the runs it ran in, so the tests only the branch carries are timed by the branch's runs alone."""
    runs = branch_runs(branch)
    start = next((i for i, r in enumerate(runs) if r.get("head_sha") == base), None)
    if start is None:
        print(f"::notice::{branch}'s recent runs don't include the base commit {base}; the baseline is {branch}'s latest runs")
        start = 0
    found = usable(runs[start:], exclude, BASELINE_RUNS)
    if branch != "main" and len(found) < BASELINE_RUNS:
        mains = branch_runs("main")
        if found:
            oldest = found[-1][3]
            mains = [r for r in mains if (r.get("created_at") or "") < oldest]
        else:
            # None of its own: main's, as for a PR into main, from the base commit's run if main ran it.
            mains = mains[next((i for i, r in enumerate(mains) if r.get("head_sha") == base), 0):]
        print(f"::notice::{branch} has {len(found)} usable runs of its own; main's runs fill the other"
              f" {BASELINE_RUNS - len(found)} of the baseline's {BASELINE_RUNS}")
        found += usable(mains, exclude, BASELINE_RUNS - len(found))
    return [(i, run, w) for i, run, w, _ in found]


def usable(runs: list[dict], exclude: str, want: int
           ) -> list[tuple[str, dict[str, dict[str, float]], float | None, str]]:
    """The first `want` of these runs whose test jobs all succeeded: each one's id, test results, pipeline
    wall time and creation time."""
    found = []
    for r in runs:
        if len(found) == want:
            break
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
        found.append((str(r["id"]), run, wall(js), r.get("created_at") or ""))
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
    p.add_argument("--max-drift-seconds", type=float, required=True,
                   help="the time the tests inside the band may add together, over main's own")
    p.add_argument("--max-wall-seconds", type=float, required=True, help="the wall time over which to warn")
    p.add_argument("--current", required=True, help="the name of the job running this script")
    a = p.parse_args(argv)

    run = results(a.results)
    missing = [t for t in TEST_JOBS if t not in run]
    if missing:
        print(f"::error::no test results from {', '.join(missing)}; the test time can't be measured")
        return 1
    branch = base_branch()
    baseline = baseline_runs(exclude=os.environ.get("GITHUB_RUN_ID", ""), base=base_commit(), branch=branch)
    if len(baseline) < BASELINE_RUNS:
        print(f"::error::only {len(baseline)} of {branch}'s recent runs have every test job's results, and the baseline"
              f" needs {BASELINE_RUNS}; the test time can't be compared")
        return 1

    more, usual, share, factors = added(run, [b for _, b, _ in baseline])
    spread = drift(run, [b for _, b, _ in baseline])
    print(f"Test time on a median runner: {total(normalized(run, reference([b for _, b, _ in baseline]))):.0f} s;"
          f" {branch}'s usual {usual:.0f} s (median of runs {', '.join(i for i, _, _ in baseline)})")
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
        line = f"pipeline wall time {mine:.0f} s of {a.max_wall_seconds:g} s; {branch}'s last runs, oldest first: {trend} s"
        print(f"::warning::{line}, over the budget on this runner" if mine > a.max_wall_seconds else line)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as f:
            f.write(f"### Pipeline time\n\n| | This run | {branch}'s usual |\n|---|---|---|\n"
                    f"| Test time the changed tests add on a median runner (s) | {more:+.0f} | limit {a.max_added_seconds:g} |\n"
                    f"| Test time the other tests add together (s) | {spread:+.0f} | limit {a.max_drift_seconds:g} |\n"
                    f"| Wall time (s) | {'?' if mine is None else f'{mine:.0f}'} | {trend} |\n")

    rc = 0
    if more > a.max_added_seconds:
        print(f"::error::PrPipeline_WallTimeUnder6Min: the changed tests add {more:.0f} s over {branch}'s on a median"
              f" runner, over the {a.max_added_seconds:g} s allowed; the tests above add the most")
        rc = 1
    else:
        print(f"PrPipeline_WallTimeUnder6Min: the changed tests add {more:+.0f} s of the {a.max_added_seconds:g} s allowed")
    if spread > a.max_drift_seconds:
        print(f"::error::PrPipeline_WallTimeUnder6Min: the tests each inside their usual time add {spread:.0f} s together"
              f" over {branch}'s on a median runner, over the {a.max_drift_seconds:g} s allowed: a slowdown spread over many tests")
        rc = 1
    else:
        print(f"PrPipeline_WallTimeUnder6Min: the other tests add {spread:+.0f} s together, of the {a.max_drift_seconds:g} s allowed")
    return rc


if __name__ == "__main__":
    sys.exit(main())
