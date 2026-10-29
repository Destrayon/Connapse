"""CON-146: picks the hard question set from EnterpriseRAG-Bench's erb-50k subsample.

Word overlap is the share of a question's content words (lowercased, stop words removed, English-stemmed; the
dev suite's tokenizer) that also appear in its gold documents. The 36 "semantic" questions with the lowest overlap
are the hard set; 12 "basic" questions, picked by a stable hash of their ID, are the easy control.

Each answer is checked against its gold documents: a fact counts as grounded when at least half of its content
words appear there. Questions with an ungrounded fact are listed for a manual read, not dropped silently.

    python select_questions.py --dataset E:/tmp/con-32/cache/datasets/erb-50k/69916e31c68a --out questions.json
"""
import argparse
import hashlib
import json
import os
import sys
from pathlib import Path

import pyarrow.parquet as pq

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from build_dev_suite import tokens  # noqa: E402

HARD, CONTROL = 36, 12


def overlap(text_terms, doc_terms):
    return round(len(text_terms & doc_terms) / len(text_terms), 3) if text_terms else 1.0


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", required=True)
    p.add_argument("--out", required=True)
    args = p.parse_args()

    questions = pq.read_table(os.path.join(args.dataset, "questions.parquet")).to_pylist()
    wanted = {d for q in questions for d in q["expected_doc_ids"]}
    docs = {}
    for batch in pq.ParquetFile(os.path.join(args.dataset, "documents.parquet")).iter_batches(
            columns=["doc_id", "source_type", "title", "content"]):
        for row in batch.to_pylist():
            if row["doc_id"] in wanted:
                docs[row["doc_id"]] = row

    scored = []
    for q in questions:
        gold = [docs[d] for d in q["expected_doc_ids"] if d in docs]
        if q["question_type"] not in ("semantic", "basic") or not gold:
            continue
        doc_terms = set()
        for d in gold:
            doc_terms |= set(tokens(d["title"] + "\n" + d["content"]))
        q_terms = set(tokens(q["question"]))
        facts = [overlap(set(tokens(f)), doc_terms) for f in q["answer_facts"]]
        scored.append({"q": q, "overlap": overlap(q_terms, doc_terms), "fact_overlap": facts,
                       "grounded": all(f >= 0.5 for f in facts), "sources": sorted({d["source_type"] for d in gold})})

    semantic = sorted((s for s in scored if s["q"]["question_type"] == "semantic"),
                      key=lambda s: (s["overlap"], s["q"]["question_id"]))[:HARD]
    basic = sorted((s for s in scored if s["q"]["question_type"] == "basic"),
                   key=lambda s: hashlib.sha256(s["q"]["question_id"].encode()).hexdigest())[:CONTROL]

    out = []
    for kind, picked in (("hard", semantic), ("control", basic)):
        for s in picked:
            q = s["q"]
            out.append({"id": q["question_id"], "kind": kind, "area": "+".join(s["sources"]),
                        "question": q["question"], "answer": q["gold_answer"], "must_include": q["answer_facts"],
                        "gold_doc_ids": q["expected_doc_ids"], "word_overlap": s["overlap"],
                        "fact_overlap": s["fact_overlap"], "facts_grounded": s["grounded"]})
    Path(args.out).write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")

    def mean(xs):
        return round(sum(xs) / len(xs), 3)
    all_sem = [s["overlap"] for s in scored if s["q"]["question_type"] == "semantic"]
    all_basic = [s["overlap"] for s in scored if s["q"]["question_type"] == "basic"]
    print(json.dumps({"hard": len(semantic), "control": len(basic),
                      "hard_overlap_mean": mean([s["overlap"] for s in semantic]),
                      "hard_overlap_max": max(s["overlap"] for s in semantic),
                      "control_overlap_mean": mean([s["overlap"] for s in basic]),
                      "all_semantic_overlap_mean": mean(all_sem), "all_basic_overlap_mean": mean(all_basic),
                      "ungrounded": [s["q"]["question_id"] for s in semantic + basic if not s["grounded"]]}, indent=1))


if __name__ == "__main__":
    main()
