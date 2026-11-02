"""Splits a mutation run of Connapse.Application across parallel jobs, and merges their reports (CON-138).

A single Stryker job re-tests every mutant one after another on one runner, so its time grows with the mutants a
PR touches: PR runs went from about 4 to 7 minutes as epic #783 moved endpoints into Connapse.Application, and a
full run from 11 to 17. `plan` splits the project's source files into shards of about the same number of
mutants to test, each shard runs Stryker with --mutate on its own files, and `merge` puts their reports back
into one report of the whole project: each file from the shard that mutated it. The merged report is scored
exactly as one run's report would be, and saved as the baseline the same way.

A shard's estimate comes from the baseline: a file's mutants that a run tests (Killed, Survived or Timeout last
time), all of them for a full run or a changed file, else only those a changed test file covers, with a timeout
counted as TIMEOUT_COST mutants. A file the
baseline doesn't have is estimated from its length. With one shard Stryker runs on the whole project with no
--mutate, exactly as before sharding.

A file heavier than half a shard is cut into parts across shards, since one file's mutants otherwise held up
its shard alone (172 of a 372-mutant run in 40729669517). A part is a --mutate span of characters, and Stryker
mutates only what a span wholly contains, so cuts fall only on blank lines between a type's members, where no
mutated node can cross. `merge` takes each of the file's mutants from the one shard whose part kept it, and
fails if no shard did.

Usage:
  mutation_shards.py plan --project-dir src/Connapse.Application --repo-root "$PWD" --mode partial|full \
      [--baseline mutation-report.json] [--changed changed-files.txt] [--max-shards 4] [--per-shard 100]
  mutation_shards.py merge --plan plan.json --reports DIR --out merged.json [--shard-prefix shard-]
"""
import argparse
import json
import math
import os
import re
import sys

from mutation_score import DETECTED, UNDETECTED, normalize, test_files

TESTED = {"Killed", "Survived", "Timeout"}
# A timeout holds its test session for the whole timeout: about 23 s against 0.2 s for any other mutant in the
# first sharded full run (40668977895), where the shard with 12 of the 17 timeouts took 6.4 min to test and the
# ones with none 1.6 to 2.5.
TIMEOUT_COST = 100
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


def costs(files: list[str], project_dir: str, project_rel: str, mode: str, baseline: dict | None,
          changed: set[str], repo_root: str) -> dict[str, list[tuple[int, float]]]:
    """Each file's estimated mutants to test, as (line, cost) pairs."""
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
            out[f] = [(n, 1 / 8) for n in range(1, lines + 1)]
            continue
        tested = [m for m in source["mutants"] if m["status"] in TESTED]
        if not (mode == "full" or f"{project_rel}/{f}" in changed):
            tested = [m for m in tested if changed_tests.intersection(m.get("coveredBy") or [])]
        out[f] = [(m["location"]["start"]["line"], TIMEOUT_COST if m["status"] == "Timeout" else 1) for m in tested]
    return out


def weights(files: list[str], project_dir: str, project_rel: str, mode: str, baseline: dict | None,
            changed: set[str], repo_root: str) -> dict[str, float]:
    """Each file's estimated number of mutants to test."""
    return {f: sum(c for _, c in cs)
            for f, cs in costs(files, project_dir, project_rel, mode, baseline, changed, repo_root).items()}


# A string literal's opening: optional $ (interpolated) and @ (verbatim) prefixes, then one or more quotes.
STRING_START = re.compile(r'(\$*)(@?)(\$*)("+)')
TYPE_KEYWORD = re.compile(r"\b(namespace|class|struct|record|interface)\b")


def _skip_string(text: str, i: int) -> int:
    """The index just past the string or char literal starting at i."""
    if text[i] == "'":
        j = i + 1
        if text[j] == "\\":
            j += 2
        while text[j] != "'":
            j += 1
        return j + 1
    m = STRING_START.match(text, i)
    interpolated = bool(m.group(1) or m.group(3))
    verbatim = bool(m.group(2))
    quotes = len(m.group(4))
    j = m.end()
    if quotes >= 3:
        return text.index('"' * quotes, j) + quotes
    if quotes == 2:  # ""
        return j
    while True:
        c = text[j]
        if c == "\\" and not verbatim:
            j += 2
        elif c == '"':
            if verbatim and text.startswith('""', j):
                j += 2
            else:
                return j + 1
        elif c == "{" and interpolated:
            if text.startswith("{{", j):
                j += 2
            else:
                j = _skip_code(text, j + 1, "}")
        else:
            j += 1


def _skip_comment(text: str, i: int) -> int:
    """The index just past the comment at i: a line comment stops before its line's end."""
    if text.startswith("//", i):
        end = text.find("\n", i)
        return len(text) if end < 0 else end
    return text.index("*/", i + 2) + 2


def _skip_code(text: str, i: int, close: str) -> int:
    """The index just past the `close` that ends the code starting at i, such as an interpolation hole."""
    depth = 0
    while True:
        c = text[i]
        if text.startswith(("//", "/*"), i):
            i = _skip_comment(text, i)
        elif c in "\"'" or (c in "$@" and STRING_START.match(text, i)):
            i = _skip_string(text, i)
        elif c == "{":
            depth += 1
            i += 1
        elif c == "}" and depth:
            depth -= 1
            i += 1
        elif c == close:
            return i + 1
        else:
            i += 1


def cut_points(text: str) -> list[tuple[int, int]]:
    """Where a C# file can be cut so that no node Stryker mutates crosses the cut: the start of each blank line
    between a namespace's or a type's members, after a finished one. Each is (offset in UTF-16 code units, as Roslyn
    counts them, line number)."""
    stack: list[bool] = []  # one per open brace: whether it opens a namespace's or a type's body
    header: list[str] = []  # the code since the last ; { or }, to tell a type's brace from any other
    last = ""  # the last character of code outside comments and literals
    line = 1
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if text.startswith(("//", "/*"), i):
            j = _skip_comment(text, i)
            line += text.count("\n", i, j)
            i = j
            continue
        if c in "\"'" or (c in "$@" and STRING_START.match(text, i)):
            j = _skip_string(text, i)
            line += text.count("\n", i, j)
            last = '"'
            i = j
            continue
        if c == "#" and text[text.rfind("\n", 0, i) + 1:i].strip() == "":
            end = text.find("\n", i)
            i = n if end < 0 else end
            continue
        if c == "\n":
            line += 1
            end = text.find("\n", i + 1)
            if ((not stack or stack[-1]) and last in ";{}" and text[i + 1:n if end < 0 else end].strip() == ""
                    and end >= 0):
                out.append((len(text[:i + 1].encode("utf-16-le")) // 2, line))
        elif c == "{":
            # A where clause can name `class` or `struct` as a constraint; the type's own keyword comes before it.
            stack.append(bool(TYPE_KEYWORD.search(re.split(r"\bwhere\b", "".join(header))[0])))
            header = []
        elif c == "}":
            if stack:
                stack.pop()
            header = []
        elif c == ";":
            header = []
        else:
            header.append(c)
        if not c.isspace():
            last = c
        i += 1
    return out


def parts(text: str, line_costs: list[tuple[int, float]], count: int) -> list[tuple[int, int, float]]:
    """The file cut into up to `count` parts of about the same cost, as (start, end, cost); the last part ends at
    the largest span Stryker accepts, so it takes the file's end however its characters are counted."""
    cuts = cut_points(text)
    total = sum(c for _, c in line_costs)
    if count < 2 or not cuts or total <= 0:
        return [(0, 2**31 - 1, total)]

    def before(line: int) -> float:
        return sum(c for l, c in line_costs if l < line)

    chosen: list[tuple[int, int]] = []
    for k in range(1, count):
        goal = total * k / count
        best = min(cuts, key=lambda cut: abs(before(cut[1]) - goal))
        if (not chosen or best[0] > chosen[-1][0]) and 0 < before(best[1]) < total:
            chosen.append(best)
    bounds = [(0, 1)] + chosen + [(2**31 - 1, 2**31 - 1)]
    out = []
    for (start, first), (end, after) in zip(bounds, bounds[1:]):
        out.append((start, end, sum(c for l, c in line_costs if first <= l < after)))
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


def plan(files: list[str], weight: dict[str, float], mode: str, max_shards: int, per_shard: int,
         divide=None) -> dict:
    """`divide(file, count)` cuts a file into parts as `parts` does; without it no file is cut."""
    total = sum(weight.values())
    count = max_shards if mode == "full" else max(1, min(max_shards, math.ceil(total / per_shard)))
    count = min(count, max(1, len(files)))
    # A unit is a whole file, or a part of one written as its --mutate span suffix: File.cs{start..end}.
    units = dict(weight)
    cut: dict[str, str] = {}
    if count > 1 and divide:
        cap = total / count / 4
        for f in files:
            if weight[f] > cap:
                pieces = divide(f, math.ceil(weight[f] / cap))
                if len(pieces) > 1:
                    del units[f]
                    for start, end, w in pieces:
                        units[f"{f}{{{start}..{end}}}"] = w
                        cut[f"{f}{{{start}..{end}}}"] = f
    shards = split(units, count)
    return {"mode": mode, "estimate": round(total),
            "shards": [{"index": i, "files": [u for u in s if u not in cut],
                        "parts": [{"file": cut[u], "span": u[len(cut[u]):]} for u in s if u in cut],
                        "estimate": round(sum(units[u] for u in s)),
                        # One shard mutates the whole project with no --mutate, as a run did before sharding.
                        "mutate": [] if count == 1 else [f"**/{u}" for u in s]}
                       for i, s in enumerate(shards)]}


def _mutant_key(m: dict) -> str:
    return json.dumps([m.get("id"), m.get("mutatorName"), m.get("replacement"), m.get("location")], sort_keys=True)


def merge_parts(rel: str, entries: dict[int, dict]) -> dict:
    """One file's entry from the shards that each mutated a part of it: every mutant from the one shard whose span
    kept it. Fails if the shards' mutants differ or a mutant was filtered out of every part, which would drop it
    from the score; a compile error keeps its status in every shard, so it may come from several."""
    shards = sorted(entries)
    keyed = {i: {_mutant_key(m): m for m in entries[i]["mutants"]} for i in shards}
    first = entries[shards[0]]
    if any(keyed[i].keys() != keyed[shards[0]].keys() for i in shards):
        raise ValueError(f"shards {shards} mutated {rel} differently")
    mutants = []
    for m in first["mutants"]:
        k = _mutant_key(m)
        kept = [keyed[i][k] for i in shards if MUTATE_FILTER not in (keyed[i][k].get("statusReason") or "")]
        if not kept:
            raise ValueError(f"no shard of {shards} kept the mutant at line {m['location']['start']['line']} "
                             f"of {rel}")
        if len(kept) > 1 and any(x["status"] != "CompileError" for x in kept):
            raise ValueError(f"several shards of {shards} tested the mutant at line "
                             f"{m['location']['start']['line']} of {rel}")
        mutants.append(kept[0])
    return {**first, "mutants": mutants}


def merge(plan_: dict, reports: list[dict]) -> tuple[dict, list[str]]:
    """One report of the whole project: each file's entry from the shard that owns it. Fails when an owned file
    came back filtered out, or a file one shard owns is missing from its report but present in another's, since
    either would drop that file's mutants from the score. A file another shard also tested only warns."""
    if len(reports) != len(plan_["shards"]):
        raise ValueError(f"{len(reports)} report(s) for {len(plan_['shards'])} shard(s)")
    owner = {f: s["index"] for s in plan_["shards"] for f in s["files"]}
    part_owners: dict[str, set[int]] = {}
    for s in plan_["shards"]:
        for p in s.get("parts") or []:
            part_owners.setdefault(p["file"], set()).add(s["index"])
    part_entries: dict[str, dict[int, dict]] = {}
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
            if rel in part_owners:
                if i in part_owners[rel]:
                    part_entries.setdefault(rel, {})[i] = source
            elif single or owner.get(rel) == i:
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
    for rel, shards in sorted(part_owners.items()):
        missing = sorted(shards - part_entries.get(rel, {}).keys())
        if missing:
            raise ValueError(f"{rel} is missing from shard {missing[0]}'s report, which owns a part of it")
        files[rel] = merge_parts(rel, part_entries[rel])
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
    # A shard's setup, build and first test runs take about 3 minutes however few mutants it has, and its
    # mutants about 0.45 s each, so a shard of 100 saves about 20 s on one of 150 for 3 more runner minutes.
    pl.add_argument("--per-shard", type=int, default=100)
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
        line_costs = costs(files, project_dir, normalize(a.project_dir).strip("/"), a.mode, baseline, changed,
                           a.repo_root)
        weight = {f: sum(c for _, c in cs) for f, cs in line_costs.items()}

        def divide(f: str, count: int) -> list[tuple[int, int, float]]:
            # newline="" keeps CRLF as two characters and utf-8-sig drops a BOM, as Roslyn reads the file.
            with open(os.path.join(project_dir, f), encoding="utf-8-sig", newline="") as fh:
                return parts(fh.read(), line_costs[f], count)

        print(json.dumps(plan(files, weight, a.mode, a.max_shards, a.per_shard, divide)))
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
