"""Scores the CON-341 writer comparison against bars Q1 and Q2 in eval/pass-bars.md.

    python -I score_save_list.py <dataset-dir> <runs-dir>

<runs-dir> holds one folder per arm (`today`, `listed`), each with one folder per repetition (`rep1`, `rep2`) of
memory_op.py ops files (`<CODE>.jsonl`). Only the held-out projects are scored. Prints one JSON object.

Right answer: for each held-out `current` and `as_of` question, the facts true on the asked date (for `current`, the
project's last session date) that name any value the asked setting ever had, read with MemoryEval.Named (the longest
of overlapping names wins), together name exactly the expected value.
Wrongly replaced fact: a `correct` whose target names a value of setting S, and whose new value does not name S's
value on the date the correction takes effect (its validFrom, or its session's date).
"""
import json
import re
import sys
from pathlib import Path
from statistics import mean

HELDOUT = ["ASH", "HAL", "KST", "MRW", "QFT"]
FACT_OF = {"database": "database", "deploy region": "region", "tech lead": "lead", "CI system": "ci", "cache": "cache",
           "public API rate limit": "rate_limit", "alert channel": "alert", "current release": "release"}

root, runs = Path(sys.argv[1]), Path(sys.argv[2])
timeline = json.loads((root / "timeline.json").read_text(encoding="utf-8"))
questions = [json.loads(line) for line in (root / "questions.jsonl").read_text(encoding="utf-8").splitlines()]
questions = [q for q in questions if q["split"] == "heldout" and q["kind"] in ("current", "as_of") and q["code"] in HELDOUT]
sessions = [json.loads(line) for line in (root / "sessions.jsonl").read_text(encoding="utf-8").splitlines()]
session_date = {s["session"]: s["date"] for s in sessions}
last_date = {c: max(s["date"] for s in sessions if s["code"] == c) for c in HELDOUT}


def named(text: str, value: str) -> bool:
    return re.search(rf"(?<![\w.-]){re.escape(value)}(?![\w-])", text) is not None


def names(text: str, values: list[str]) -> set[str]:
    """The setting values a text names, dropping any that is only named inside a longer one it also names."""
    hit = {v for v in values if named(text, v)}
    return {v for v in hit if not any(v != w and v in w for w in hit)}


def facts(ops: list[dict]) -> dict[str, dict]:
    """memory_op.py's replay: every fact by id, with ends and corrections applied, plus who corrected it."""
    out: dict[str, dict] = {}
    for op in ops:
        fact = {k: op.get(k) for k in ("subject", "attribute", "value", "validFrom")}
        fact["session"] = op["session"]
        if op["op"] == "correct":
            old = out[op["target"]]
            fact["subject"] = fact["subject"] or old["subject"]
            fact["attribute"] = fact["attribute"] or old["attribute"]
            if op["kind"] == "changed":
                old["validTo"] = op["validFrom"]
            else:
                old["status"] = "corrected"
                fact["validFrom"] = fact["validFrom"] or old["validFrom"]
        out[op["id"]] = fact
    return out


def true_on(f: dict, date: str) -> bool:
    return (f.get("status") != "corrected" and (not f.get("validFrom") or f["validFrom"] <= date)
            and (not f.get("validTo") or f["validTo"] > date))


def value_on(code: str, setting: str, date: str) -> str | None:
    steps = [s for s in timeline[code][setting] if s["from"] <= date]
    return steps[-1]["value"] if steps else None


def score(code: str, ops: list[dict]) -> dict:
    all_facts = facts(ops)
    right = []
    for q in (q for q in questions if q["code"] == code):
        noun = next(k for k in FACT_OF if f"'s {k}" in q["question"])
        values = list(dict.fromkeys(s["value"] for s in timeline[code][FACT_OF[noun]]))
        date = last_date[code] if q["kind"] == "current" else re.search(r"\d{4}-\d{2}-\d{2}", q["question"]).group()
        said = set().union(*(names(f["value"], values) for f in all_facts.values() if true_on(f, date)))
        right.append(said == {q["answer"]["value"]})

    wrongly = 0
    for op in (o for o in ops if o["op"] == "correct"):
        target = all_facts[op["target"]]
        date = op.get("validFrom") or session_date[op["session"]]
        for setting, steps in timeline[code].items():
            if names(target["value"], list(dict.fromkeys(s["value"] for s in steps))):
                expected = value_on(code, setting, date)
                if expected is None or not named(op["value"], expected):
                    wrongly += 1
                break

    # Reported only: saves that would get a list, how many were followed by a correct of a listed fact, and
    # same-key duplicates left current at the end.
    key = lambda f: (f["subject"].strip().lower(), f["attribute"].strip().lower())
    listed_saves = followed = 0
    seen: dict[str, dict] = {}
    for i, op in enumerate(ops):
        if op["op"] == "remember":
            current = {fid for fid, f in facts(ops[:i]).items() if f.get("status") != "corrected" and not f.get("validTo")
                       and key(f) == key(op)}
            if current:
                listed_saves += 1
                if any(o["op"] == "correct" and o["target"] in current for o in ops[i + 1:i + 4]):
                    followed += 1
    end = [f for f in all_facts.values() if f.get("status") != "corrected" and not f.get("validTo")]
    duplicates = len(end) - len({key(f) for f in end})
    return {"right": sum(right), "questions": len(right), "wronglyReplaced": wrongly, "savesListed": listed_saves,
            "followedByCorrect": followed, "duplicatesLeft": duplicates, "ops": len(ops)}


result = {}
for arm in ("today", "listed"):
    reps = sorted(p for p in (runs / arm).iterdir() if p.is_dir()) if (runs / arm).exists() else []
    per_rep = []
    for rep in reps:
        per_code = {}
        for code in HELDOUT:
            path = rep / f"{code}.jsonl"
            ops = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line] if path.exists() else []
            per_code[code] = score(code, ops)
        right = sum(s["right"] for s in per_code.values())
        total = sum(s["questions"] for s in per_code.values())
        per_rep.append({"rep": rep.name, "rightRate": right / total, "right": right, "questions": total,
                        "wronglyReplaced": sum(s["wronglyReplaced"] for s in per_code.values()), "projects": per_code})
    result[arm] = {"rightRate": mean(r["rightRate"] for r in per_rep) if per_rep else None,
                   "wronglyReplaced": mean(r["wronglyReplaced"] for r in per_rep) if per_rep else None, "reps": per_rep}

if result["today"]["reps"] and result["listed"]["reps"]:
    result["Q1"] = result["listed"]["rightRate"] >= result["today"]["rightRate"] - 0.05
    result["Q2"] = result["listed"]["wronglyReplaced"] <= result["today"]["wronglyReplaced"] + 1
print(json.dumps(result, indent=2))
