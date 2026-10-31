"""The plain-notes baseline for the memory-question set (CON-38): BM25 over the notes, nothing else.

    python -I baseline.py <dataset-dir> <longmemeval_s_cleaned.json> <out.json>

A plain-notes memory is a project's folder of dated notes and a keyword search over it: no notion of
which note is newer, which fact superseded which, or what date a question asks about. For each
question, its project's notes are ranked by BM25 against the question text.

- hit@1, hit@5: an evidence note (one that holds the answer) is ranked first, or in the top five. For
  a changed question, every change in the window must have one of its notes there; a window with no
  changes has nothing to find and isn't scored.
- answer: what a reader of the ranked notes would answer, read mechanically from the first note that
  names a value of the asked fact. For current and as_of, the value the note names last (in "from A to
  B", B). For when, that note's date; for ticket, the first ticket id in the note naming the value.
  Scored against the timeline.

LongMemEval's knowledge-update and temporal-reasoning questions (longmemeval.json) are the public
check: each question's own haystack sessions are ranked the same way, and hit@k asks whether one of
its answer sessions is in the top k.
"""
import json
import math
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

TOKEN = re.compile(r"[a-z0-9]+")


class Bm25:
    def __init__(self, docs: list[str], k1: float = 1.2, b: float = 0.75):
        self.toks = [Counter(TOKEN.findall(d.lower())) for d in docs]
        self.lens = [sum(t.values()) for t in self.toks]
        self.avg = sum(self.lens) / len(self.lens)
        df = Counter(w for t in self.toks for w in t)
        n = len(docs)
        self.idf = {w: math.log(1 + (n - c + 0.5) / (c + 0.5)) for w, c in df.items()}
        self.k1, self.b = k1, b

    def rank(self, query: str) -> list[int]:
        q = set(TOKEN.findall(query.lower()))
        scores = []
        for i, (t, length) in enumerate(zip(self.toks, self.lens)):
            s = sum(self.idf[w] * t[w] * (self.k1 + 1) / (t[w] + self.k1 * (1 - self.b + self.b * length / self.avg)) for w in q if w in t)
            scores.append((-s, i))
        return [i for _, i in sorted(scores)]


def named(text: str, value: str) -> int:
    """Where the note names the value, or -1."""
    m = None
    for m in re.finditer(rf"(?<![\w.-]){re.escape(value)}(?![\w-])", text):
        pass
    return m.end() if m else -1


def mean(xs: list[float]) -> float:
    return round(sum(xs) / len(xs), 4) if xs else None


def main() -> int:
    root, lme_path, out = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
    notes = [json.loads(line) for line in (root / "notes.jsonl").read_text(encoding="utf-8").splitlines()]
    questions = [json.loads(line) for line in (root / "questions.jsonl").read_text(encoding="utf-8").splitlines()]
    timeline = json.loads((root / "timeline.json").read_text(encoding="utf-8"))
    by_project = {c: [n for n in notes if n["code"] == c] for c in {n["code"] for n in notes}}
    indexes = {c: Bm25([n["text"] for n in ns]) for c, ns in by_project.items()}
    fact_of = {"database": "database", "deploy region": "region", "tech lead": "lead", "CI system": "ci", "cache": "cache",
               "public API rate limit": "rate_limit", "alert channel": "alert", "current release": "release"}

    groups = defaultdict(lambda: defaultdict(list))
    for q in questions:
        if q["evidence"] == ["(none)"]:
            continue  # a window with no changes has no evidence note to find
        ranked = [by_project[q["code"]][i] for i in indexes[q["code"]].rank(q["question"])]
        ids = [n["note"] for n in ranked]
        if q["kind"] == "changed":
            m = {f"hit@{k}": float(all(set(c["evidence"]) & set(ids[:k]) for c in q["answer"]["changes"])) for k in (1, 5)}
        else:
            evidence = set(q["evidence"])
            m = {"hit@1": float(ids[0] in evidence), "hit@5": float(bool(evidence & set(ids[:5])))}
        noun = next((k for k in fact_of if f"'s {k}" in q["question"]), None)
        if noun and q["kind"] in ("current", "as_of", "when", "ticket"):
            values = {s["value"] for s in timeline[q["code"]][fact_of[noun]]}
            target = q["answer"].get("value") or q["question"].rsplit(" to ", 1)[-1].rstrip("?")
            if q["kind"] in ("current", "as_of"):
                first = next((n for n in ranked if any(named(n["text"], v) >= 0 for v in values)), None)
                said = max(values, key=lambda v: named(first["text"], v)) if first else None
                m["answer"] = float(said == q["answer"]["value"])
            else:
                first = next((n for n in ranked if named(n["text"], target) >= 0), None)
                if q["kind"] == "when":
                    m["answer"] = float(first is not None and first["date"] == q["answer"]["date"])
                else:
                    t = re.search(r"\b[A-Z]{3}-\d+\b", first["text"]) if first else None
                    m["answer"] = float(t is not None and t.group() == q["answer"]["ticket"])
        for g in (q["split"], f"{q['split']}/{q['kind']}"):
            for k, v in m.items():
                groups[g][k].append(v)

    lme = json.loads((root / "longmemeval.json").read_text(encoding="utf-8"))
    chosen = {q["question_id"]: q for q in lme["questions"]}
    data = [d for d in json.loads(lme_path.read_text(encoding="utf-8")) if d["question_id"] in chosen]
    for d in data:
        sessions = [" ".join(t["content"] for t in s) for s in d["haystack_sessions"]]
        ranked = [d["haystack_session_ids"][i] for i in Bm25(sessions).rank(d["question"])]
        answers = set(d["answer_session_ids"])
        split = chosen[d["question_id"]]["split"]
        for g in (f"longmemeval/{split}", f"longmemeval/{split}/{d['question_type']}"):
            groups[g]["hit@1"].append(float(ranked[0] in answers))
            groups[g]["hit@5"].append(float(bool(answers & set(ranked[:5]))))

    result = {g: {"questions": len(m["hit@1"]), **{k: mean(v) for k, v in m.items()}} for g, m in sorted(groups.items())}
    out.write_text(json.dumps({"system": "BM25 over plain notes (k1 1.2, b 0.75)", "groups": result}, indent=2) + "\n", encoding="utf-8")
    for g, m in result.items():
        print(f"{g:40} n={m['questions']:3}  hit@1 {m['hit@1']:.3f}  hit@5 {m['hit@5']:.3f}" + (f"  answer {m['answer']:.3f}" if m.get("answer") is not None else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
