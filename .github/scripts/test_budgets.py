"""Check test-speed budgets from VSTest TRX results (epic #783, problem P10).

Each tier runs as its own `dotnet test` with its own TRX file name, because TRX doesn't record
xUnit traits. Per-test limits catch a slow test sitting in the wrong tier; the Small tier's wall
limit is the guarantee that keeps the fast gate fast. Per-test limits are loose on purpose: with
test assemblies running in parallel, the first test in a class can take seconds of warm-up that
says nothing about the test itself.

Usage:
  test_budgets.py --tier Small --trx small-tests.trx --max-test-seconds 3 --wall-seconds 22 --max-wall-seconds 30
  test_budgets.py --tier Large --trx large-tests.trx            # no time limits, but every project must report
"""
import argparse
import glob
import pathlib
import re
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


def results(trx_name: str, projects: list[str]):
    """Every test result in the tier, plus the projects that wrote no TRX for it.

    VSTest writes a TRX for every test project it runs, even one with no tests matching the
    tier's filter, so a missing file means that project never ran: a crash, a build problem or a
    filter typo, and its tests went unmeasured.
    """
    found, missing = [], []
    for project in projects:
        paths = sorted(glob.glob(f"{project}/TestResults/{trx_name}"))
        if not paths:
            missing.append(project)
        for path in paths:
            for r in ET.parse(path).getroot().iterfind(".//t:UnitTestResult", NS):
                if r.get("outcome") == "NotExecuted" or r.get("duration") is None:
                    continue
                found.append((re.sub(r"\(.*$", "", r.get("testName")), seconds(r.get("duration"))))
    return found, missing


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--tier", required=True)
    p.add_argument("--trx", required=True, help="TRX file name each test project wrote, e.g. small-tests.trx")
    p.add_argument("--max-test-seconds", type=float)
    p.add_argument("--wall-seconds", type=float, default=-1, help="measured wall time of the tier's dotnet test step")
    p.add_argument("--max-wall-seconds", type=float)
    p.add_argument("--solution", default="Connapse.slnx", help="where the expected test projects are listed")
    a = p.parse_args()

    projects = expected_projects(a.solution)
    found, missing = results(a.trx, projects)
    tests = sorted(found, key=lambda t: t[1], reverse=True)
    print(f"{a.tier}: {len(tests)} tests, {sum(t for _, t in tests):.1f} s summed")
    for name, t in tests[:20]:
        print(f"  {t:7.2f} s  {name}")

    failures = []
    for project in missing:
        failures.append(f"{project} wrote no {a.trx}; its {a.tier} tests did not run")
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
        elif a.wall_seconds > a.max_wall_seconds:
            failures.append(f"{a.tier} tier took {a.wall_seconds:g} s wall, over its {a.max_wall_seconds:g} s budget")
        else:
            print(f"{a.tier} wall: {a.wall_seconds:g} s of {a.max_wall_seconds:g} s")

    for f in failures:
        print(f"::error::{f}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
