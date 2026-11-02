"""CON-142 judge and summary.

    python judge.py --out E:/tmp/con-142/runs judge      # grades every runs-<setup>.jsonl line not yet judged
    python judge.py --out E:/tmp/con-142/runs summary    # prints the tables used in results.md

The judge sees the question, the reference answer, the required facts and the candidate answer, never
the setup name. An answer is correct when every required fact is present (in any wording) and
nothing in it contradicts the reference.
"""
import argparse
import json
import os

import subprocess
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).parent
BELOW_NORMAL = 0x00004000
SETUPS = ["rag", "files", "both"]
RAG_CALLS = {"search_knowledge", "get_document"}
FILE_CALLS = {"grep_files", "find_files", "read_file"}

PROMPT = """You grade an answer to a question about a codebase against a reference answer.

Question: {question}

Reference answer: {reference}

Required facts (each must be present in the candidate, in any wording):
{facts}

Candidate answer:
<<<
{candidate}
>>>

Grade meaning, not wording: a fact is present when the candidate conveys the same information, even in
different words or more detail. Extra correct detail is fine. A contradiction is a statement that conflicts
with the reference, not an omission.

First write one line per required fact: the fact number, the candidate's words that convey it (or "none"),
and PRESENT or MISSING. Then write one line saying whether anything contradicts the reference. Last, on its
own line, the verdict as JSON that agrees with those lines:
{{"facts": [true or false for each required fact, in order], "contradiction": true or false, "reason": "one sentence"}}"""


def grade(q, rec, model):
    facts = "\n".join(f"{i + 1}. {f}" for i, f in enumerate(q["must_include"]))
    prompt = PROMPT.format(question=q["question"], reference=q["answer"], facts=facts,
                           candidate=rec["answer"] or "(no answer)")
    for attempt in range(2):
        proc = subprocess.run(["claude", "-p", prompt, "--model", model, "--tools", "", "--strict-mcp-config",
                               "--setting-sources", "", "--no-session-persistence", "--output-format", "json"],
                              capture_output=True, text=True, encoding="utf-8", timeout=300,
                              creationflags=BELOW_NORMAL if os.name == "nt" else 0)
        outer = json.loads(proc.stdout)
        text = outer.get("result", "")
        try:
            verdict, _ = json.JSONDecoder().raw_decode(text[text.rindex('{"facts"'):])
            break
        except ValueError:
            if attempt == 1:
                raise
    present = [bool(x) for x in verdict["facts"]][:len(q["must_include"])]
    correct = len(present) == len(q["must_include"]) and all(present) and not verdict["contradiction"]
    return {"facts": present, "contradiction": verdict["contradiction"], "reason": verdict.get("reason", ""),
            "correct": correct, "judge_cost_usd": outer.get("total_cost_usd")}


def judge(out, questions, model):
    for setup in SETUPS:
        runs = out / f"runs-{setup}.jsonl"
        if not runs.exists():
            continue
        judged_path = out / f"judged-{setup}.jsonl"
        done = set()
        if judged_path.exists():
            done = {json.loads(l)["id"] for l in judged_path.read_text(encoding="utf-8").splitlines() if l.strip()}
        for line in runs.read_text(encoding="utf-8").splitlines():
            rec = json.loads(line)
            if rec["id"] in done:
                continue
            rec["judge"] = grade(questions[rec["id"]], rec, model)
            with judged_path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(rec) + "\n")
            print(f"{setup} {rec['id']} correct={rec['judge']['correct']}", flush=True)


def load(out, setup):
    path = out / f"judged-{setup}.jsonl"
    return [json.loads(l) for l in path.read_text(encoding="utf-8").splitlines() if l.strip()] if path.exists() else []


def summary(out):
    rows = {s: load(out, s) for s in SETUPS}
    print("| Setup | Correct | Exact | Concept | Code | Docs | Cost per question | Tool calls per question | Turns |")
    print("|---|---|---|---|---|---|---|---|---|")
    for s, recs in rows.items():
        if not recs:
            continue
        def pct(f):
            sel = [r for r in recs if f(r)]
            if not sel:
                return "n/a"
            return f"{100 * sum(r['judge']['correct'] for r in sel) / len(sel):.0f}% ({sum(r['judge']['correct'] for r in sel)}/{len(sel)})"
        n = len(recs)
        cost = sum(r["cost_usd"] or 0 for r in recs) / n
        calls = sum(r["tool_calls"] for r in recs) / n
        turns = sum(r["num_turns"] or 0 for r in recs) / n
        print(f"| {s} | {pct(lambda r: True)} | {pct(lambda r: r['kind'] == 'exact')} | "
              f"{pct(lambda r: r['kind'] == 'concept')} | {pct(lambda r: r['area'] == 'code')} | "
              f"{pct(lambda r: r['area'] == 'docs')} | ${cost:.4f} | {calls:.1f} | {turns:.1f} |")

    both = rows["both"]
    if both:
        print("\nTool choice in 'both' (first search call, and any use):")
        by_kind = defaultdict(lambda: defaultdict(int))
        for r in both:
            names = [c["name"] for c in r["calls"]]
            first = next((n for n in names if n in {"search_knowledge", "grep_files", "find_files"}), "none")
            k = r["kind"]
            by_kind[k]["n"] += 1
            by_kind[k]["first_rag"] += first == "search_knowledge"
            by_kind[k]["first_file"] += first in {"grep_files", "find_files"}
            by_kind[k]["used_rag"] += any(n in RAG_CALLS for n in names)
            by_kind[k]["used_files"] += any(n in FILE_CALLS for n in names)
        for k, c in by_kind.items():
            print(f"- {k}: n={c['n']} first=RAG {c['first_rag']}, first=file search {c['first_file']}; "
                  f"used RAG {c['used_rag']}, used file tools {c['used_files']}")

    print("\nPer-question correctness (rag / files / both):")
    by_id = defaultdict(dict)
    for s, recs in rows.items():
        for r in recs:
            by_id[r["id"]][s] = "Y" if r["judge"]["correct"] else "-"
    for qid in sorted(by_id):
        print(qid, " ".join(by_id[qid].get(s, "?") for s in SETUPS))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    p.add_argument("--questions", default=str(HERE / "questions.json"))
    p.add_argument("--model", default="sonnet")
    p.add_argument("action", choices=["judge", "summary"])
    args = p.parse_args()
    out = Path(args.out)
    questions = {q["id"]: q for q in json.loads(Path(args.questions).read_text(encoding="utf-8"))}
    if args.action == "judge":
        judge(out, questions, args.model)
    else:
        summary(out)


if __name__ == "__main__":
    main()
