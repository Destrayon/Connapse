"""The whole project's mutation score for a PR that re-tested only its own mutants (epic #783, CON-91).

A PR's Stryker run uses --since, so it tests only the mutants in the files the PR changes and those its
changed test files cover; every other mutant comes back Ignored, "Removed by since filter". This takes those mutants'
results from the last full run on the PR's base branch, matched by file, mutator, replacement and location
(an unchanged file has the same mutants), and holds the merged score to the break threshold in
stryker-config.json, so the PR is held to the same whole-project score a full run is. Stryker 5.0.0's own
--with-baseline can't do this: it never matches a disk baseline (stryker-net#3892), and even when it does it
leaves the unchanged mutants out of the score.

Usage:
  mutation_score.py --report mutation-report.json --baseline baseline/mutation-report.json \
      --config tests/Connapse.Core.Tests/stryker-config.json
"""
import argparse
import json
import sys

UNCHANGED = "Removed by since filter"
DETECTED = {"Killed", "Timeout"}
UNDETECTED = {"Survived", "NoCoverage"}


def mutants(report: dict) -> dict[tuple, dict]:
    """Each mutant by (path under the project, mutator, replacement, start, end): the same in every run of
    the same source, unlike its id. Paths are made relative because the report keys files by full path, and
    line endings are made LF because a Windows checkout's replacements carry CRLF."""
    root = report.get("projectRoot", "").replace("\\", "/").rstrip("/") + "/"
    found = {}
    for path, source in report["files"].items():
        path = path.replace("\\", "/")
        relative = path[len(root):] if path.startswith(root) else path
        for m in source["mutants"]:
            start, end = m["location"]["start"], m["location"]["end"]
            key = (relative, m["mutatorName"], (m.get("replacement") or "").replace("\r\n", "\n"),
                   start["line"], start["column"], end["line"], end["column"])
            found[key] = m
    return found


def merge(report: dict, baseline: dict) -> tuple[dict[str, int], int]:
    """Status counts with each unchanged mutant's status taken from the baseline, and how many unchanged
    mutants the baseline doesn't have (left out of the score, as Stryker leaves them out)."""
    previous = mutants(baseline)
    counts: dict[str, int] = {}
    unmatched = 0
    for key, m in mutants(report).items():
        status = m["status"]
        if status == "Ignored" and m.get("statusReason") == UNCHANGED:
            if key not in previous:
                unmatched += 1
                continue
            status = previous[key]["status"]
        counts[status] = counts.get(status, 0) + 1
    return counts, unmatched


def score(counts: dict[str, int]) -> float | None:
    """Stryker's formula: detected over detected plus undetected, as a percentage; None with nothing valid."""
    detected = sum(counts.get(s, 0) for s in DETECTED)
    valid = detected + sum(counts.get(s, 0) for s in UNDETECTED)
    return 100 * detected / valid if valid else None


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--report", required=True)
    p.add_argument("--baseline", required=True)
    p.add_argument("--config", required=True)
    a = p.parse_args()

    with open(a.report, encoding="utf-8-sig") as f:
        report = json.load(f)
    with open(a.baseline, encoding="utf-8-sig") as f:
        baseline = json.load(f)
    with open(a.config, encoding="utf-8-sig") as f:
        threshold = json.load(f)["stryker-config"]["thresholds"]["break"]

    counts, unmatched = merge(report, baseline)
    result = score(counts)
    tested = sum(1 for m in mutants(report).values() if m["status"] in DETECTED | UNDETECTED)
    print(f"{tested} mutant(s) tested in this PR, the rest from the base branch's last full run: "
          + ", ".join(f"{n} {s}" for s, n in sorted(counts.items())))
    if unmatched:
        print(f"::warning::{unmatched} unchanged mutant(s) aren't in the baseline and are left out of the score")
    if result is None:
        print("No mutants to score")
        return 0
    print(f"Mutation score {result:.2f}% (break threshold {threshold}%)")
    if result < threshold:
        print(f"::error::Mutation score {result:.2f}% is under the break threshold of {threshold}%")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
