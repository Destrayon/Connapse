"""IntegrationSuite_EveryResponse_ConformsToCommittedSpec: the proof check for epic #783, problem P3 (CON-8).

Every integration shard's test host checks each /api response against docs/api/openapi.json and writes
what it saw to spec-conformance-*.json. This joins the shards' records and compares the violations with
tests/Connapse.Integration.Tests/Spec/known-spec-violations.json exactly: a violation not listed fails, and
so does a listed one whose operation and status a shard saw conforming, which has been fixed and must be
deleted from the list. A listed one the run didn't exercise at all is only warned about: which statuses a
run reaches can vary (a conflict that depends on timing), so its absence proves nothing.

Usage (in the job that has every shard's results):
  spec_conformance.py --reports "**/spec-conformance-*.json" --known tests/Connapse.Integration.Tests/Spec/known-spec-violations.json
"""
import argparse
import glob
import json
import sys


def merge(paths: list[str]) -> tuple[set[str], dict[str, str]]:
    """Every operation and status the shards saw, and every violation with one example of its detail."""
    seen: set[str] = set()
    violations: dict[str, str] = {}
    for path in paths:
        with open(path, encoding="utf-8-sig") as f:
            report = json.load(f)
        seen.update(report["seen"])
        for key, detail in report["violations"].items():
            violations.setdefault(key, detail)
    return seen, violations


def operation_status(key: str) -> str:
    """'GET /api/x 404: reason' -> 'GET /api/x 404', the pair a shard records as seen."""
    return key.split(": ", 1)[0]


def compare(seen: set[str], violations: dict[str, str], known: dict[str, str]) -> tuple[list[str], list[str], list[str]]:
    """The violations the list lacks; the listed ones a shard saw conforming; and the listed ones not exercised."""
    added = sorted(set(violations) - set(known))
    gone = set(known) - set(violations)
    closed = sorted(k for k in gone if operation_status(k) in seen)
    unexercised = sorted(k for k in gone if operation_status(k) not in seen)
    return added, closed, unexercised


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--reports", required=True, help="glob of the shards' spec-conformance-*.json files")
    p.add_argument("--known", required=True, help="the exact-match list of today's violations")
    p.add_argument("--expect-reports", type=int, required=True, help="how many shards must have reported")
    a = p.parse_args()

    paths = sorted(glob.glob(a.reports, recursive=True))
    # Every shard must report: a missing one would leave its violations unchecked and its entries
    # reading as merely unexercised.
    if len(paths) != a.expect_reports:
        print(f"::error::{len(paths)} shard report(s), expected {a.expect_reports}; a shard that didn't report wasn't checked")
        return 1

    seen, violations = merge(paths)
    with open(a.known, encoding="utf-8-sig") as f:
        known = json.load(f)
    added, closed, unexercised = compare(seen, violations, known)

    print(f"{len(paths)} shard report(s), {len(seen)} operation and status pairs seen, {len(violations)} violation(s), {len(known)} listed")
    for key in added:
        print(f"::error::not listed in {a.known}: \"{key}\"  ({violations[key]})")
    for key in closed:
        print(f"::error::listed in {a.known} but its response now conforms; delete it: \"{key}\"")
    for key in unexercised:
        print(f"::warning::listed in {a.known} but not exercised by this run: \"{key}\"")
    if added:
        print("Fix the endpoint or its spec, or add each new violation to the list with the reason. Entries to add:")
        print(json.dumps({key: "" for key in added}, indent=2))
    return 1 if added or closed else 0


if __name__ == "__main__":
    sys.exit(main())
