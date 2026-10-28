"""Splits a mutation run of Connapse.Application across parallel jobs, and merges their reports (CON-138).

A single Stryker job re-tests every mutant one after another on one runner, so its time grows with the mutants a
PR touches: PR runs went from about 4 to 7 minutes as epic #783 moved endpoints into Connapse.Application, and a
full run from 11 to 17. `plan` splits the project's source files into shards of about the same number of
mutants to test, each shard runs Stryker with --mutate on its own files, and `merge` puts their reports back
into one report of the whole project: each file from the shard that mutated it. The merged report is scored
exactly as one run's report would be, and saved as the baseline the same way.

A shard's estimate comes from the baseline: a file's mutants that a run tests (Killed, Survived or Timeout last
time), all of them for a full run or a changed file, else only those a changed test file covers. A file the
baseline doesn't have is estimated from its length. With one shard Stryker runs on the whole project with no
--mutate, exactly as before sharding.

Usage:
  mutation_shards.py plan --project-dir src/Connapse.Application --repo-root "$PWD" --mode partial|full \
      [--baseline mutation-report.json] [--changed changed-files.txt] [--max-shards 4] [--per-shard 150]
  mutation_shards.py merge --plan plan.json --reports DIR --out merged.json [--shard-prefix shard-]
"""
import argparse
import json
import math
import os
import sys

from mutation_score import DETECTED, UNDETECTED, normalize, test_files

TESTED = {"Killed", "Survived", "Timeout"}
# Stryker's status reason for a mutant outside the --mutate patterns.
MUTATE_FILTER = "mutate filter"


def source_files(project_dir: str) -> list[str]:
    """The project's .cs files, relative to its directory, outside obj/ and bin/."""
    found = []
    for root, dirs, files in os.walk(project_dir):
        dirs[:] = sorted(d for d in dirs if d not in ("obj", "bin"))
        for name in files:
            if name.endswith(".cs"):
                found.append(normalize(os.path.relpath(os.path.join(root, name), project_dir)))
    return sorted(found)


def relative_files(report: dict) -> dict[str, dict]:
    """The report's file entries keyed by path relative to the project root."""
    root = normalize(report.get("projectRoot", "")).rstrip("/") + "/"
    out = {}
    for path, source in report["files"].items():
        path = normalize(path)
        out[path[len(root):] if path.startswith(root) else path] = source
    return out


def weights(files: list[str], project_dir: str, project_rel: str, mode: str, baseline: dict | None,
            changed: set[str], repo_root: str) -> dict[str, float]:
    """Each file's estimated number of mutants to test."""
    known = relative_files(baseline) if baseline else {}
    changed_tests = set()
    if baseline and mode == "partial":
        changed_tests = {t for t, f in test_files(baseline, repo_root).items() if f in changed}
    out = {}
    for f in files:
        source = known.get(f)
        if source is None:
            with open(os.path.join(project_dir, f), encoding="utf-8", errors="replace") as fh:
                lines = sum(1 for _ in fh)
            # About one tested mutant every eight lines in this project; a new file is tested in full either way.
            out[f] = lines / 8
            continue
        tested = [m for m in source["mutants"] if m["status"] in TESTED]
        if mode == "full" or f"{project_rel}/{f}" in changed:
            out[f] = len(tested)
        else:
            out[f] = sum(1 for m in tested if changed_tests.intersection(m.get("coveredBy") or []))
    return out


def split(weight: dict[str, float], count: int) -> list[list[str]]:
    """Heaviest file first into the lightest shard, so shards end up close to even; ties keep path order."""
    shards: list[list[str]] = [[] for _ in range(count)]
    loads = [0.0] * count
    for f in sorted(weight, key=lambda f: (-weight[f], f)):
        i = loads.index(min(loads))
        shards[i].append(f)
        loads[i] += weight[f]
    return [sorted(s) for s in shards]


def plan(files: list[str], weight: dict[str, float], mode: str, max_shards: int, per_shard: int) -> dict:
    total = sum(weight.values())
    count = max_shards if mode == "full" else max(1, min(max_shards, math.ceil(total / per_shard)))
    count = min(count, max(1, len(files)))
    shards = split(weight, count)
    return {"mode": mode, "estimate": round(total),
            "shards": [{"index": i, "files": s, "estimate": round(sum(weight[f] for f in s)),
                        # One shard mutates the whole project with no --mutate, as a run did before sharding.
                        "mutate": [] if count == 1 else [f"**/{f}" for f in s]}
                       for i, s in enumerate(shards)]}


def merge(plan_: dict, reports: list[dict]) -> tuple[dict, list[str]]:
    """One report of the whole project: each file's entry from the shard that owns it. Fails when an owned file
    came back filtered out, or a file one shard owns is missing from its report but present in another's, since
    either would drop that file's mutants from the score. A file another shard also tested only warns."""
    if len(reports) != len(plan_["shards"]):
        raise ValueError(f"{len(reports)} report(s) for {len(plan_['shards'])} shard(s)")
    owner = {f: s["index"] for s in plan_["shards"] for f in s["files"]}
    single = len(plan_["shards"]) == 1
    merged = {k: v for k, v in reports[0].items() if k not in ("files", "testFiles")}
    root = normalize(merged.get("projectRoot", "")).rstrip("/")
    files: dict[str, dict] = {}
    seen_elsewhere: set[str] = set()
    tests: dict[str, dict] = {}
    warnings = []
    for i, report in enumerate(reports):
        for path, source in (report.get("testFiles") or {}).items():
            tests.setdefault(normalize(path), source)
        for rel, source in relative_files(report).items():
            if single or owner.get(rel) == i:
                files[rel] = source
            elif owner.get(rel) is None:
                raise ValueError(f"{rel} is in shard {i}'s report but in no shard's plan")
            else:
                seen_elsewhere.add(rel)
                if any(m["status"] in DETECTED | UNDETECTED for m in source["mutants"]):
                    warnings.append(f"{rel} was also tested by shard {i}; shard {owner[rel]}'s results count")
    for rel in sorted(seen_elsewhere - files.keys()):
        raise ValueError(f"{rel} is missing from shard {owner[rel]}'s report, which owns it")
    for rel, source in files.items():
        # A shard mutates every file and filters out the ones not its own, so one filtered mutant in a file it
        # owns means its pattern missed that file (its compile errors keep their status either way).
        if any(MUTATE_FILTER in (m.get("statusReason") or "") for m in source["mutants"]):
            raise ValueError(f"shard {owner.get(rel, 0)} filtered out {rel}, which it owns")
    merged["files"] = {f"{root}/{rel}": files[rel] for rel in sorted(files)}
    merged["testFiles"] = tests
    return merged, warnings


def main() -> int:
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="command", required=True)
    pl = sub.add_parser("plan")
    pl.add_argument("--project-dir", required=True)
    pl.add_argument("--repo-root", required=True)
    pl.add_argument("--mode", choices=("partial", "full"), required=True)
    pl.add_argument("--baseline")
    pl.add_argument("--changed")
    pl.add_argument("--max-shards", type=int, default=4)
    pl.add_argument("--per-shard", type=int, default=150)
    mg = sub.add_parser("merge")
    mg.add_argument("--plan", required=True)
    mg.add_argument("--reports", required=True, help="directory holding <prefix><index>/mutation-report.json")
    mg.add_argument("--shard-prefix", default="shard-")
    mg.add_argument("--out", required=True)
    a = p.parse_args()

    if a.command == "plan":
        baseline = None
        if a.baseline and os.path.isfile(a.baseline):
            with open(a.baseline, encoding="utf-8-sig") as f:
                baseline = json.load(f)
        changed: set[str] = set()
        if a.changed and os.path.isfile(a.changed):
            with open(a.changed, encoding="utf-8") as f:
                changed = {normalize(line.rstrip("\r\n").split("\t", 1)[1]) for line in f if "\t" in line}
        project_dir = os.path.join(a.repo_root, a.project_dir)
        files = source_files(project_dir)
        weight = weights(files, project_dir, normalize(a.project_dir).strip("/"), a.mode, baseline, changed,
                         a.repo_root)
        print(json.dumps(plan(files, weight, a.mode, a.max_shards, a.per_shard)))
        return 0

    with open(a.plan, encoding="utf-8") as f:
        plan_ = json.load(f)
    reports = []
    for s in plan_["shards"]:
        path = os.path.join(a.reports, f"{a.shard_prefix}{s['index']}", "mutation-report.json")
        if not os.path.isfile(path):
            print(f"::error::shard {s['index']} has no report at {path}")
            return 1
        with open(path, encoding="utf-8-sig") as f:
            reports.append(json.load(f))
    try:
        merged, warnings = merge(plan_, reports)
    except ValueError as e:
        print(f"::error::{e}")
        return 1
    for w in warnings:
        print(f"::warning::{w}")
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(merged, f)
    return 0


if __name__ == "__main__":
    sys.exit(main())
