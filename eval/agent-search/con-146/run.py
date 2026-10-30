"""CON-146 runner, judge and summary on top of CON-142's `run_agent.py` and `judge.py` (both unchanged).

Four setups; each is CON-142's rag or both tool set with its own prompt files from `con-146/prompts/`:
  rag          Connapse tools only (the reference for what RAG finds)
  files_first  all five tools, no advice on which to use (the agent as it behaves today; the baseline)
  rag_first    all five tools plus routing advice: search_knowledge first for questions in their own words
  rag_always   all five tools plus: call search_knowledge before any file tool on every question

    python run.py run --setup files_first --out E:/tmp/con-146/runs --tool-server E:/tmp/con-146/proto/Connapse.AgentSearch.Prototype.exe \
        --corpus E:/tmp/con-146/corpus --rg E:/tmp/con-142/bin/rg.exe --cwd E:/tmp/con-146/agent-cwd --pat-file E:/tmp/con-146/pat.txt
    python run.py judge --out E:/tmp/con-146/runs
    python run.py summary --out E:/tmp/con-146/runs
"""
import argparse
import json
import os
import sys
from collections import Counter
from math import comb
from pathlib import Path

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE.parent))
import judge as con142_judge  # noqa: E402
import run_agent  # noqa: E402

SETUPS = {
    "rag": ("rag", ["base.md", "rag.md"]),
    "files_first": ("both", ["base.md", "files_first.md"]),
    "rag_first": ("both", ["base.md", "files_first.md", "rag_first.md"]),
    "rag_always": ("both", ["base.md", "files_first.md", "rag_always.md"]),
}
FIXES = ["rag_first", "rag_always"]
SEARCH_CALLS = {"search_knowledge", "grep_files", "find_files"}
# CON-142's judge prompt says "about a codebase"; these questions are about a company's records.
con142_judge.PROMPT = con142_judge.PROMPT.replace("a question about a codebase", "a question about a company's records")


def system_prompt(setup):
    return "\n".join((HERE / "prompts" / name).read_text(encoding="utf-8") for name in SETUPS[setup][1])


def load_jsonl(path):
    return [json.loads(l) for l in path.read_text(encoding="utf-8").splitlines() if l.strip()] if path.exists() else []


def run(args, questions):
    tools, _ = SETUPS[args.setup]
    if "CON142_PAT" not in os.environ:
        os.environ["CON142_PAT"] = Path(args.pat_file).read_text(encoding="utf-8").strip()
    out = Path(args.out) / args.setup
    out.mkdir(parents=True, exist_ok=True)
    Path(args.cwd).mkdir(parents=True, exist_ok=True)
    mcp_path = run_agent.mcp_config(tools, args, out)
    prompt = system_prompt(args.setup)
    summary = out / "runs.jsonl"
    done = {r["id"] for r in load_jsonl(summary)}
    todo = [q for q in questions if q["id"] not in done and (not args.only or q["id"] in args.only)]
    for i, q in enumerate(todo, 1):
        rec = run_agent.run_one(q, tools, args, out, mcp_path, prompt)
        rec["setup"] = args.setup
        with summary.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
        print(f"[{args.setup} {i}/{len(todo)}] {q['id']} calls={rec['tool_calls']} cost={rec['cost_usd']} "
              f"turns={rec['num_turns']} {rec['wall_s']}s", flush=True)
        if rec["unexpected_tools"]:
            sys.exit(f"agent saw tools outside its setup: {rec['unexpected_tools']}")


def judge(args, questions):
    by_id = {q["id"]: q for q in questions}
    for setup in SETUPS:
        runs = load_jsonl(Path(args.out) / setup / "runs.jsonl")
        judged_path = Path(args.out) / setup / "judged.jsonl"
        done = {r["id"] for r in load_jsonl(judged_path)}
        for rec in runs:
            if rec["id"] in done:
                continue
            rec["judge"] = con142_judge.grade(by_id[rec["id"]], rec, args.model)
            with judged_path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(rec) + "\n")
            print(f"{setup} {rec['id']} correct={rec['judge']['correct']}", flush=True)


def mcnemar_exact(b, c):
    """Two-sided exact McNemar p-value from the discordant counts b and c."""
    n = b + c
    if n == 0:
        return 1.0
    tail = sum(comb(n, k) for k in range(0, min(b, c) + 1)) / 2 ** n
    return min(1.0, 2 * tail)


def summary(args, questions):
    kinds = {q["id"]: q["kind"] for q in questions}
    rows = {s: {r["id"]: r for r in load_jsonl(Path(args.out) / s / "judged.jsonl")} for s in SETUPS}
    report = {}
    print("| Setup | Hard (36) | Control (12) | Cost per question | Tool calls | Turns | First search is RAG | Used RAG |")
    print("|---|---|---|---|---|---|---|---|")
    for s, recs in rows.items():
        if not recs:
            continue
        rs = list(recs.values())
        hard = sum(r["judge"]["correct"] for r in rs if kinds[r["id"]] == "hard")
        control = sum(r["judge"]["correct"] for r in rs if kinds[r["id"]] == "control")
        firsts = Counter(next((c["name"] for c in r["calls"] if c["name"] in SEARCH_CALLS), "none") for r in rs)
        used_rag = sum(any(c["name"] in ("search_knowledge", "get_document") for c in r["calls"]) for r in rs)
        cost = sum(r["cost_usd"] or 0 for r in rs) / len(rs)
        report[s] = {"n": len(rs), "hard": hard, "control": control, "cost": cost}
        print(f"| {s} | {hard} ({100 * hard / 36:.0f}%) | {control} | ${cost:.4f} | "
              f"{sum(r['tool_calls'] for r in rs) / len(rs):.1f} | {sum(r['num_turns'] or 0 for r in rs) / len(rs):.1f} | "
              f"{firsts['search_knowledge']}/{len(rs)} | {used_rag}/{len(rs)} |")

    def correct(s, qid):
        return rows[s].get(qid, {}).get("judge", {}).get("correct", False)

    hard_ids = [q["id"] for q in questions if q["kind"] == "hard"]
    if rows["rag"] and rows["files_first"]:
        found = [i for i in hard_ids if correct("rag", i)]
        missed = [i for i in found if not correct("files_first", i)]
        print(f"\nMiss rate: files_first got {len(missed)} of the {len(found)} hard questions rag answered wrong "
              f"({100 * len(missed) / max(len(found), 1):.0f}%): {missed}")
        gained = [i for i in hard_ids if correct("files_first", i) and not correct("rag", i)]
        print(f"files_first answered {len(gained)} hard questions rag missed: {gained}")

    base = report.get("files_first")
    for fix in FIXES:
        if fix not in report or not base:
            continue
        r = report[fix]
        acc = r["hard"] - base["hard"]
        cost = r["cost"] / base["cost"] - 1
        harm = base["control"] - r["control"]
        b = sum(correct(fix, i) and not correct("files_first", i) for i in hard_ids)
        c = sum(correct("files_first", i) and not correct(fix, i) for i in hard_ids)
        print(f"\n{fix} vs files_first: hard {acc:+d} ({100 * acc / 36:+.1f} points; bar >= +4) "
              f"{'PASS' if acc >= 4 else 'FAIL'}; cost {100 * cost:+.1f}% (bar <= +20%) {'PASS' if cost <= 0.20 else 'FAIL'}; "
              f"control {-harm:+d} (bar >= -1) {'PASS' if harm <= 1 else 'FAIL'}; "
              f"hard discordant {b} won / {c} lost, exact McNemar p={mcnemar_exact(b, c):.3f}")

    print("\nPer-question correctness (" + " / ".join(SETUPS) + "):")
    for q in questions:
        print(q["id"], q["kind"], " ".join("Y" if correct(s, q["id"]) else ("-" if q["id"] in rows[s] else "?") for s in SETUPS))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["run", "judge", "summary"])
    p.add_argument("--setup", choices=SETUPS)
    p.add_argument("--questions", default=str(HERE / "questions.json"))
    p.add_argument("--out", required=True)
    p.add_argument("--connapse", default="http://localhost:5146/mcp")
    p.add_argument("--tool-server")
    p.add_argument("--corpus")
    p.add_argument("--rg")
    p.add_argument("--cwd", help="empty folder the agent runs in")
    p.add_argument("--pat-file")
    p.add_argument("--claude", default="claude")
    p.add_argument("--model", default="sonnet")
    p.add_argument("--max-turns", type=int, default=25)
    p.add_argument("--timeout", type=int, default=900)
    p.add_argument("--only", nargs="*")
    args = p.parse_args()
    questions = json.loads(Path(args.questions).read_text(encoding="utf-8"))
    {"run": run, "judge": judge, "summary": summary}[args.action](args, questions)


if __name__ == "__main__":
    main()
