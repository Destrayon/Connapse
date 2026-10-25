"""Check test-speed budgets from VSTest TRX results (epic #783, problem P10).

Each tier runs as its own `dotnet test` with its own TRX file name, because TRX doesn't record
xUnit traits. Per-test limits catch a slow test sitting in the wrong tier; the Small tier's wall
limit is the guarantee that keeps the fast gate fast. Per-test limits are loose on purpose: with
test assemblies running in parallel, the first test in a class can take seconds of warm-up that
says nothing about the test itself.

GitHub's runners differ in speed: the same Small tests take 14 to 31 s of wall, and every test's
duration moves with it (CON-87). With --reference, the wall limit is read on a median runner: the
run's wall divided by its runner factor, the median ratio of its tests' durations to the
reference's. A real slowdown still shows: a slower start, or new tests, raise the wall but not the
factor. What it can't see is one that slows every test alike, which reads as a slower runner.

Usage:
  test_budgets.py --tier Small --trx small-tests.trx --max-test-seconds 10 --wall-seconds 22 --max-wall-seconds 30 \
      --reference .github/test-timings/small-reference.json
  test_budgets.py --tier Large --trx large-tests.trx            # no time limits, but every project must report
  test_budgets.py --trx small-tests.trx --write-reference .github/test-timings/small-reference.json RUN_DIR...
      # each RUN_DIR a downloaded test-results-small artifact; several runs, so the reference is a median runner
"""
import argparse
import glob
import json
import pathlib
import re
import statistics
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def seconds(duration: str) -> float:
    h, m, s = duration.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def expected_projects(solution: str) -> list[str]:
    """Test projects in the solution: tests/ projects that reference Microsoft.NET.Test.Sdk."""
    text = pathlib.Path(solution).read_text(encoding="utf-8")
    projects = []
    for path in re.findall(r'Project Path="(tests/[^"]+\.csproj)"', text):
        csproj = pathlib.Path(path)
        if csproj.exists() and "Microsoft.NET.Test.Sdk" in csproj.read_text(encoding="utf-8"):
            projects.append(str(csproj.parent).replace("\\", "/"))
    return sorted(projects)


# A reference test must take this long on a median runner to be timed well enough to compare.
MIN_REFERENCE_SECONDS = 0.01

# Below this many reference tests in a run, the factor says too little: the reference is stale.
MIN_MATCHED = 100


def durations(paths: list[str]) -> dict[str, float]:
    """Each passed test's duration by its full name, theory cases apart, across these TRX files."""
    found: dict[str, float] = {}
    for path in paths:
        for r in ET.parse(path).getroot().iterfind(".//t:UnitTestResult", NS):
            if r.get("outcome") == "Passed" and r.get("duration") is not None:
                name = r.get("testName")
                found[name] = found.get(name, 0.0) + seconds(r.get("duration"))
    return found


def runner_factor(run: dict[str, float], reference: dict[str, float]) -> tuple[float, int]:
    """How much slower than the reference's median runner this run's runner was, and on how many tests."""
    ratios = [run[name] / reference[name] for name in reference if name in run and reference[name] > 0]
    return (statistics.median(ratios) if ratios else 0.0), len(ratios)


def write_reference(out: str, trx_name: str, run_dirs: list[str]) -> dict[str, float]:
    """Each test's median duration over the runs, for tests every run timed at 10 ms or more on the median."""
    runs = [durations(glob.glob(f"{d}/**/{trx_name}", recursive=True)) for d in run_dirs]
    common = set.intersection(*(set(r) for r in runs)) if runs else set()
    reference = {}
    for name in sorted(common):
        median = statistics.median(r[name] for r in runs)
        if median >= MIN_REFERENCE_SECONDS:
            reference[name] = round(median, 6)
    pathlib.Path(out).write_text(json.dumps(reference, indent=2) + "\n", encoding="utf-8")
    return reference


def results(trx_name: str, projects: list[str]):
    """Every test result in the tier, plus the projects that wrote no TRX for it.

    VSTest writes a TRX for every test project it runs, even one with no tests matching the
    tier's filter, so a missing file means that project never ran: a crash, a build problem or a
    filter typo, and its tests went unmeasured.
    """
    found, missing, skipped, untimed = [], [], [], []
    for project in projects:
        paths = sorted(glob.glob(f"{project}/TestResults/{trx_name}"))
        if not paths:
            missing.append(project)
        for path in paths:
            for r in ET.parse(path).getroot().iterfind(".//t:UnitTestResult", NS):
                name = re.sub(r"\(.*$", "", r.get("testName"))
                if r.get("outcome") == "NotExecuted":
                    skipped.append(name)  # [Fact(Skip = ...)]: never ran, so it has no duration by design
                elif r.get("duration") is None:
                    untimed.append(name)
                else:
                    found.append((name, seconds(r.get("duration"))))
    return found, missing, skipped, untimed


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--tier")
    p.add_argument("--trx", required=True, help="TRX file name each test project wrote, e.g. small-tests.trx")
    p.add_argument("--max-test-seconds", type=float)
    p.add_argument("--wall-seconds", type=float, default=-1, help="measured wall time of the tier's dotnet test step")
    p.add_argument("--max-wall-seconds", type=float)
    p.add_argument("--solution", default="Connapse.slnx", help="where the expected test projects are listed")
    p.add_argument("--reference", help="per-test durations on a median runner; the wall limit is read on that runner")
    p.add_argument("--write-reference", help="write a reference from the run directories given, and check nothing")
    p.add_argument("runs", nargs="*", help="with --write-reference: downloaded test-results artifacts, one per run")
    a = p.parse_args(argv)

    if a.write_reference:
        reference = write_reference(a.write_reference, a.trx, a.runs)
        print(f"Wrote {len(reference)} tests from {len(a.runs)} runs to {a.write_reference}")
        return 0 if len(reference) >= MIN_MATCHED else 1
    if not a.tier:
        p.error("--tier is required")

    projects = expected_projects(a.solution)
    found, missing, skipped, untimed = results(a.trx, projects)
    tests = sorted(found, key=lambda t: t[1], reverse=True)
    print(f"{a.tier}: {len(tests)} tests, {sum(t for _, t in tests):.1f} s summed, {len(skipped)} skipped")
    for name, t in tests[:20]:
        print(f"  {t:7.2f} s  {name}")

    failures = []
    for project in missing:
        failures.append(f"{project} wrote no {a.trx}; its {a.tier} tests did not run")
    for name in untimed:
        failures.append(f"{name} ran but its result has no duration, so its speed can't be checked")
    if not tests:
        failures.append(f"{a.tier} ran no tests at all; a tier that measures nothing proves nothing")
    if a.max_test_seconds is not None:
        over = [(n, t) for n, t in tests if t > a.max_test_seconds]
        for n, t in over:
            failures.append(f"{n} took {t:.2f} s, over the {a.tier} limit of {a.max_test_seconds:g} s; "
                            f"give its class a larger Size trait or make it faster")
    if a.max_wall_seconds is not None:
        if a.wall_seconds is None or a.wall_seconds < 0:
            failures.append(f"{a.tier} has a wall limit but no --wall-seconds was measured")
        else:
            wall, on = a.wall_seconds, "wall"
            if a.reference:
                reference = json.loads(pathlib.Path(a.reference).read_text(encoding="utf-8"))
                run = durations([path for project in projects for path in glob.glob(f"{project}/TestResults/{a.trx}")])
                factor, matched = runner_factor(run, reference)
                if matched < MIN_MATCHED:
                    failures.append(f"only {matched} of the {len(reference)} tests in {a.reference} ran, too few to tell "
                                    f"the runner's speed; refresh it with --write-reference from recent runs' artifacts")
                    wall = None
                else:
                    print(f"{a.tier} runner factor {factor:.2f} over {matched} reference tests: "
                          f"{a.wall_seconds:g} s wall is {a.wall_seconds / factor:.1f} s on a median runner")
                    wall, on = a.wall_seconds / factor, "on a median runner"
            if wall is not None and wall > a.max_wall_seconds:
                failures.append(f"{a.tier} tier took {wall:.1f} s {on}, over its {a.max_wall_seconds:g} s budget")
            elif wall is not None:
                print(f"{a.tier} wall: {wall:.1f} s {on}, of {a.max_wall_seconds:g} s")

    for f in failures:
        print(f"::error::{f}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
