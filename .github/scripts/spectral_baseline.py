"""Spec_FollowsTheStyleRules: the REST API's Spectral rules against an exact-match list (epic #783, CON-53).

Spectral writes every violation of .spectral.yaml in docs/api/openapi.json to a JSON report. This compares
them with .github/spectral-known.json exactly: a violation not listed fails, and so does a listed one Spectral
no longer reports, which has been fixed and must be deleted. The spec is generated, so the same code gives
the same report every time.

Usage:
  spectral_baseline.py --results spectral-results.json --known .github/spectral-known.json
"""
import argparse
import json
import sys


def keys(results: list[dict]) -> set[str]:
    """'rule-code path.to.node' for each violation: stable across runs, unlike its message or line."""
    return {f"{r['code']} {'.'.join(str(p) for p in r['path'])}" for r in results}


def compare(found: set[str], known: dict[str, str]) -> tuple[list[str], list[str]]:
    return sorted(found - set(known)), sorted(set(known) - found)


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--results", required=True)
    p.add_argument("--known", required=True)
    a = p.parse_args()

    with open(a.results, encoding="utf-8-sig") as f:
        results = json.load(f)
    with open(a.known, encoding="utf-8-sig") as f:
        known = json.load(f)

    # Spectral reports a ruleset or spec it couldn't read as a violation of its own rules, which no
    # list holds: those fail as new.
    added, fixed = compare(keys(results), known)
    print(f"{len(results)} Spectral violation(s), {len(known)} listed")
    for key in added:
        print(f"::error::not listed in {a.known}: \"{key}\"")
    for key in fixed:
        print(f"::error::listed in {a.known} but Spectral no longer reports it; delete it: \"{key}\"")
    if added:
        print("Fix the spec's source, or add each new violation to the list with the reason. Entries to add:")
        print(json.dumps({key: "" for key in added}, indent=2))
    return 1 if added or fixed else 0


if __name__ == "__main__":
    sys.exit(main())
