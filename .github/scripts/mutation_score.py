"""The whole project's mutation score for a PR that re-tested only some of its mutants (epic #783, CON-91).

A PR's Stryker run uses --since the commit of the base branch's last full run, so it tests only the mutants in
the files changed since then and those the changed test files cover; every other mutant comes back Ignored,
"Removed by since filter". This takes those mutants' results from that full run, matched by file, mutator,
replacement and location (an unchanged file has the same mutants), and holds the merged score to the break
threshold in stryker-config.json, so a PR is held to the same whole-project score a full run is. Stryker
5.0.0's own --with-baseline can't do this: it never matches a disk baseline (stryker-net#3892), and even when
it does it leaves the unchanged mutants out of the score.

A result the full run can't vouch for counts as survived, so the merged score is a floor under the true one:
a mutant it doesn't have; a kill whose killing tests are all in changed test files, when Stryker didn't
re-test the mutant (the tests were removed or no longer reach it, and Stryker never re-tests a static mutant
for a test change); or a timeout with a covering test in a changed file. A survivor can't get worse from a
test change, so its old result stands. It exits 3, asking for a full run, when that floor is under the
threshold, and when a change's effect can't be bounded: a changed or deleted test-project file holding no
tests (a shared helper the killing tests may use), or a build file.

Usage:
  mutation_score.py --report mutation-report.json --baseline baseline/mutation-report.json \
      --config tests/Connapse.Core.Tests/stryker-config.json --changed changed-files.txt --repo-root "$PWD"

changed-files.txt is `git diff --name-status --no-renames <baseline commit>`: a status letter, a tab, a path.
Without --report it only checks for changes that need a full run, so a full run doesn't wait on a partial one.
"""
import argparse
import json
import sys

UNCHANGED = "Removed by since filter"
DETECTED = {"Killed", "Timeout"}
UNDETECTED = {"Survived", "NoCoverage"}
FULL_RUN = 3
# Changes that can alter every mutant's result without touching a file Stryker diffs as source or test.
BUILD_FILES = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json",
               "dotnet-tools.json", "nuget.config")


def normalize(path: str) -> str:
    return path.replace("\\", "/")


def mutants(report: dict) -> dict[tuple, dict]:
    """Each mutant by (path under the project, mutator, replacement, start, end): the same in every run of
    the same source, unlike its id. Paths are made relative because the report keys files by full path, and
    line endings are made LF because a Windows checkout's replacements carry CRLF."""
    root = normalize(report.get("projectRoot", "")).rstrip("/") + "/"
    found = {}
    for path, source in report["files"].items():
        path = normalize(path)
        relative = path[len(root):] if path.startswith(root) else path
        for m in source["mutants"]:
            start, end = m["location"]["start"], m["location"]["end"]
            key = (relative, m["mutatorName"], (m.get("replacement") or "").replace("\r\n", "\n"),
                   start["line"], start["column"], end["line"], end["column"])
            found[key] = m
    return found


def test_files(report: dict, repo_root: str) -> dict[str, str]:
    """Each test id to its file, relative to the repository root."""
    root = normalize(repo_root).rstrip("/") + "/"
    files = {}
    for path, source in (report.get("testFiles") or {}).items():
        path = normalize(path)
        relative = path[len(root):] if path.startswith(root) else path
        for test in source.get("tests", []):
            files[test["id"]] = relative
    return files


def build_changes(changed: dict[str, str], project_dir: str, test_dir: str, files_with_tests: set[str]) -> list[str]:
    """Changed files that can change results without Stryker re-testing what they affect: build files anywhere,
    any non-.cs file in the mutated project (its .csproj, say), and a changed or deleted .cs file in the test
    project that held no tests. Non-.cs changes in the test project Stryker answers by re-testing everything,
    and a new test-project file can only add kills."""
    return sorted(p for p, status in changed.items()
                  if p.rsplit("/", 1)[-1] in BUILD_FILES
                  or (p.startswith(project_dir + "/") and not p.endswith(".cs"))
                  or (p.startswith(test_dir + "/") and p.endswith(".cs") and status != "A" and p not in files_with_tests))


def vouched(old: dict, tests: dict[str, str], changed: set[str]) -> bool:
    """Whether a baseline result still holds when its mutant wasn't re-tested and these test files changed."""
    killers = old.get("killedBy") or []
    if old["status"] == "Killed" and killers:
        return any(tests.get(t) not in changed for t in killers)
    if old["status"] in DETECTED:
        return not any(tests.get(t) in changed for t in killers + (old.get("coveredBy") or []))
    return True


def merge(report: dict, baseline: dict, changed: set[str], repo_root: str) -> tuple[dict[str, int], int, int]:
    """Status counts with each unchanged mutant's status taken from the baseline; the unchanged mutants the
    baseline doesn't have; and those it has but can't vouch for."""
    previous = mutants(baseline)
    tests = test_files(baseline, repo_root)
    counts: dict[str, int] = {}
    unmatched = suspect = 0
    for key, m in mutants(report).items():
        status = m["status"]
        if status == "Ignored" and m.get("statusReason") == UNCHANGED:
            old = previous.get(key)
            if old is None:
                unmatched += 1
                continue
            if not vouched(old, tests, changed):
                suspect += 1
                continue
            status = old["status"]
        counts[status] = counts.get(status, 0) + 1
    return counts, unmatched, suspect


def full_counts(report: dict) -> dict[str, int]:
    """Status counts of a report that tested every mutant."""
    counts: dict[str, int] = {}
    for source in report["files"].values():
        for m in source["mutants"]:
            counts[m["status"]] = counts.get(m["status"], 0) + 1
    return counts


def score(counts: dict[str, int]) -> float | None:
    """Stryker's formula: detected over detected plus undetected, as a percentage; None with nothing valid."""
    detected = sum(counts.get(s, 0) for s in DETECTED)
    valid = detected + sum(counts.get(s, 0) for s in UNDETECTED)
    return 100 * detected / valid if valid else None


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--report", help="omitted, only checks for changes that need a full run, before Stryker runs")
    p.add_argument("--baseline")
    p.add_argument("--config", required=True)
    p.add_argument("--changed", help="file listing the paths changed since the baseline's commit")
    p.add_argument("--repo-root")
    p.add_argument("--project-dir", default="src/Connapse.Application")
    p.add_argument("--test-dir", default="tests/Connapse.Core.Tests")
    p.add_argument("--full", action="store_true",
                   help="score --report alone, a full run merged from its shards (CON-138); no baseline or changes")
    a = p.parse_args()

    with open(a.config, encoding="utf-8-sig") as f:
        threshold = json.load(f)["stryker-config"]["thresholds"]["break"]
    if a.full:
        with open(a.report, encoding="utf-8-sig") as f:
            report = json.load(f)
        counts = full_counts(report)
        print("Every mutant tested: " + ", ".join(f"{n} {s}" for s, n in sorted(counts.items())))
        return verdict(counts, 0, threshold)
    with open(a.baseline, encoding="utf-8-sig") as f:
        baseline = json.load(f)
    with open(a.changed, encoding="utf-8") as f:
        rows = [line.rstrip("\r\n").split("\t", 1) for line in f if line.strip()]
    changed = {normalize(path): status[0] for status, path in rows}

    build = build_changes(changed, a.project_dir, a.test_dir, set(test_files(baseline, a.repo_root).values()))
    if build:
        print(f"Changed files whose effect Stryker doesn't re-test ({', '.join(build)}); every mutant needs a full run")
        return FULL_RUN
    if a.report is None:
        return 0
    with open(a.report, encoding="utf-8-sig") as f:
        report = json.load(f)
    counts, unmatched, suspect = merge(report, baseline, set(changed), a.repo_root)
    tested = sum(1 for m in mutants(report).values() if m["status"] in DETECTED | UNDETECTED)
    print(f"{tested} mutant(s) tested in this PR, the rest from the base branch's last full run: "
          + ", ".join(f"{n} {s}" for s, n in sorted(counts.items())))
    if unmatched or suspect:
        print(f"{unmatched} mutant(s) not in the full run and {suspect} whose tests changed without a re-test "
              "count as survived")
    return verdict(counts, unmatched + suspect, threshold)


def verdict(counts: dict[str, int], unknown: int, threshold: float) -> int:
    """0 to pass, 1 to fail, FULL_RUN when only a full run can tell. Unknown results count as survived, so the
    score is a floor under the true one: a floor at the threshold passes, and one under it fails only when
    nothing is unknown."""
    result = score({**counts, "Survived": counts.get("Survived", 0) + unknown})
    if result is None:
        print("No mutants to score; every mutant needs a full run")
        return FULL_RUN
    print(f"Mutation score {'at least ' if unknown else ''}{result:.2f}% (break threshold {threshold}%)")
    if result >= threshold:
        return 0
    if unknown:
        print("Under the threshold only with the unknown results counted as survived; every mutant needs a full run")
        return FULL_RUN
    print(f"::error::Mutation score {result:.2f}% is under the break threshold of {threshold}%")
    return 1


if __name__ == "__main__":
    sys.exit(main())
