"""Picks LongMemEval's knowledge-update and temporal-reasoning questions as the public check (CON-38).

    python -I select_longmemeval.py <longmemeval_oracle.json> <out.json>

LongMemEval (Wu et al., 2024; MIT) asks about facts spread across chat sessions. Its knowledge-update
questions ask for a value that changed, and its temporal-reasoning questions ask about dates and order:
the two kinds closest to this set's own. The cleaned release is pinned by commit and SHA-256 below; the
oracle file lists the questions, and the _s file adds each question's haystack of unrelated sessions.
Each question's half is fixed by a hash of the seed and its id.
"""
import hashlib
import json
import sys
from pathlib import Path

SEED = 38
REVISION = "98d7416c24c778c2fee6e6f3006e7a073259d48f"
BASE = f"https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/resolve/{REVISION}"
FILES = {"longmemeval_oracle.json": "821a2034d219ab45846873dd14c14f12cfe7776e73527a483f9dac095d38620c",
         "longmemeval_s_cleaned.json": "d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442"}
KINDS = ("knowledge-update", "temporal-reasoning")

oracle, out = Path(sys.argv[1]), Path(sys.argv[2])
if hashlib.sha256(oracle.read_bytes()).hexdigest() != FILES["longmemeval_oracle.json"]:
    sys.exit(f"{oracle} is not the pinned longmemeval_oracle.json")
picked = [{"question_id": q["question_id"], "question_type": q["question_type"],
           "split": "heldout" if hashlib.sha256(f"{SEED}:{q['question_id']}".encode()).digest()[0] % 2 else "tuning"}
          for q in json.loads(oracle.read_text(encoding="utf-8")) if q["question_type"] in KINDS]
out.write_text(json.dumps({"source": "xiaowu0162/longmemeval-cleaned", "revision": REVISION, "license": "MIT",
                           "files": [{"name": n, "url": f"{BASE}/{n}", "sha256": s} for n, s in FILES.items()],
                           "questions": picked}, indent=1) + "\n", encoding="utf-8", newline="\n")
print(len(picked), "questions:", {k: sum(1 for q in picked if q["question_type"] == k) for k in KINDS},
      {s: sum(1 for q in picked if q["split"] == s) for s in ("tuning", "heldout")})
