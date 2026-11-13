"""The memory writer's tool: records one memory operation, or shows the writer's memory (CON-111).

    python -I memory_op.py <project-code> remember '<json>'
    python -I memory_op.py <project-code> correct '<json>'
    python -I memory_op.py <project-code> show

A writer agent reads its project's sessions one at a time (next_session.py) and, after each, records what it
learned with remember or correct, the way an agent calls memory_remember and memory_correct. Operations are
appended to memory-ops/<CODE>.jsonl in order, each with the session it was written in, and replayed later
through Connapse's memory service. `show` prints the facts as memory_recall would return them now, with ids,
so the writer can find the fact to correct. Nothing here reads the sessions or the answer key.

Two options for the CON-341 comparison, given before the project code: `--ops-dir <dir>` writes and reads
`<dir>/<CODE>.jsonl` instead, and `--list-existing` makes `remember` also list the facts already current with the same
subject and attribute, as memory_remember's answer does.
"""
import json
import re
import sys
from pathlib import Path

DATE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
args = sys.argv[1:]
ops_dir = Path(__file__).resolve().parent / "../../datasets/memory-questions/memory-ops"
list_existing = False
while args and args[0].startswith("--"):
    if args[0] == "--ops-dir":
        ops_dir, args = Path(args[1]), args[2:]
    elif args[0] == "--list-existing":
        list_existing, args = True, args[1:]
    else:
        sys.exit(f"unknown option {args[0]!r}")
sys.argv = [sys.argv[0], *args]
code, verb = sys.argv[1], sys.argv[2]
path = ops_dir / f"{code}.jsonl"
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


def current() -> dict[str, dict]:
    return {i: f for i, f in facts().items() if f.get("status") != "corrected" and not f.get("validTo")}


if verb == "show":
    current = current()
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

same = []
if list_existing and verb == "remember":
    key = (op["subject"].strip().lower(), op["attribute"].strip().lower())
    same = [{"id": i, "value": f["value"], **({"validFrom": f["validFrom"]} if f.get("validFrom") else {})}
            for i, f in current().items() if (f["subject"].strip().lower(), f["attribute"].strip().lower()) == key]
op = {"session": op["session"], "op": verb, "id": f"{code}-m{len(ops) + 1}", **{k: v for k, v in op.items() if k not in ("session", "op", "id")}}
path.parent.mkdir(parents=True, exist_ok=True)
with path.open("a", encoding="utf-8", newline="\n") as f:
    f.write(json.dumps(op, ensure_ascii=False) + "\n")
print(f"recorded {op['id']}")
if same:
    print("Also current with the same subject and attribute (if you meant to replace one, correct it):")
    for s in same:
        print(json.dumps(s, ensure_ascii=False))
