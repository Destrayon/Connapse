"""The memory writer's tool: records one memory operation, or shows the writer's memory (CON-111).

    python -I memory_op.py <project-code> remember '<json>'
    python -I memory_op.py <project-code> correct '<json>'
    python -I memory_op.py <project-code> show

A writer agent reads its project's sessions one at a time (next_session.py) and, after each, records what it
learned with remember or correct, the way an agent calls memory_remember and memory_correct. Operations are
appended to memory-ops/<CODE>.jsonl in order, each with the session it was written in, and replayed later
through Connapse's memory service. `show` prints the facts as memory_recall would return them now, with ids,
so the writer can find the fact to correct. Nothing here reads the sessions or the answer key.
"""
import json
import re
import sys
from pathlib import Path

DATE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
code, verb = sys.argv[1], sys.argv[2]
path = Path(__file__).resolve().parent / f"../../datasets/memory-questions/memory-ops/{code}.jsonl"
ops = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()] if path.exists() else []


def facts() -> dict[str, dict]:
    """Every fact the operations made, by id, with its end date or correction applied."""
    out: dict[str, dict] = {}
    for op in ops:
        fact = {k: op.get(k) for k in ("subject", "attribute", "value", "validFrom", "reason")}
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


if verb == "show":
    current = {i: f for i, f in facts().items() if f.get("status") != "corrected" and not f.get("validTo")}
    for i, f in current.items():
        print(json.dumps({"id": i, **{k: v for k, v in f.items() if v is not None}}, ensure_ascii=False))
    print(f"({len(current)} current facts)")
    sys.exit(0)

if verb not in ("remember", "correct"):
    sys.exit(f"unknown verb {verb!r}: use remember, correct or show")
op = json.loads(sys.argv[3])
errors = []
if not isinstance(op.get("session"), str) or not op["session"].startswith(code + "-"):
    errors.append("session must be the id of the session you just read")
for key in ("value",) + (("subject", "attribute") if verb == "remember" else ()):
    if not isinstance(op.get(key), str) or not op[key].strip():
        errors.append(f"{key} is required")
if op.get("validFrom") is not None and not DATE.match(str(op["validFrom"])):
    errors.append("validFrom must be YYYY-MM-DD")
if verb == "correct":
    known = facts()
    if op.get("target") not in known:
        errors.append("target must be the id of a fact (see show)")
    elif known[op["target"]].get("status") == "corrected" or known[op["target"]].get("validTo"):
        errors.append("target already ended or was corrected; correct its replacement")
    if op.get("kind") not in ("changed", "wrong"):
        errors.append('kind must be "changed" or "wrong"')
    elif op["kind"] == "changed" and not op.get("validFrom"):
        errors.append("a change needs validFrom, the date the new value took effect")
if errors:
    sys.exit("refused: " + "; ".join(errors))

op = {"session": op["session"], "op": verb, "id": f"{code}-m{len(ops) + 1}", **{k: v for k, v in op.items() if k not in ("session", "op", "id")}}
path.parent.mkdir(parents=True, exist_ok=True)
with path.open("a", encoding="utf-8", newline="\n") as f:
    f.write(json.dumps(op, ensure_ascii=False) + "\n")
print(f"recorded {op['id']}")
