"""Checks the writer agents' memory operations for look-ahead (CON-111).

    python -I check_memory_ops.py <dataset-dir>

Each operation was written in a session. Its sessions must come in order, and every value and ticket it names must
already appear in that project's sessions up to and including that one: a writer that names something later
sessions introduce saw the future. Fails (exit 1) on any breach, like build_questions.py does for notes.
"""
import json
import re
import sys
from pathlib import Path

root = Path(sys.argv[1])
sessions = [json.loads(line) for line in (root / "sessions.jsonl").read_text(encoding="utf-8").splitlines()]
by_id = {s["session"]: s for s in sessions}
problems, counts = [], {}
for path in sorted((root / "memory-ops").glob("*.jsonl")):
    code = path.stem
    ops = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]
    counts[code] = len(ops)
    last = ""
    for op in ops:
        session = by_id.get(op["session"])
        if session is None or session["code"] != code:
            problems.append(f"{op['id']}: {op['session']} is not one of {code}'s sessions")
            continue
        if session["date"] < last:
            problems.append(f"{op['id']}: written in {op['session']}, after a later session")
        last = max(last, session["date"])
        seen = "\n".join(s["log"] for s in sessions if s["code"] == code and s["date"] <= session["date"])
        if op["value"] not in seen:
            problems.append(f"{op['id']}: value {op['value']!r} does not appear in {code}'s sessions up to {op['session']}")
        for ticket in re.findall(r"\b[A-Z]{3}-\d+\b", op.get("reason") or ""):
            if ticket not in seen:
                problems.append(f"{op['id']}: ticket {ticket} does not appear in {code}'s sessions up to {op['session']}")

print(json.dumps(counts))
for p in problems:
    print(p)
sys.exit(1 if problems else 0)
