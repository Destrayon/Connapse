"""CON-160 runner, judge and summary on top of CON-142's `run_agent.py` and `judge.py` and CON-146's setup (all unchanged).

Two setups, both with all five of CON-146's tools and its `files_first` prompt (see pass-bar.md):
  baseline   exactly CON-146's files_first, rerun the same day
  summaries  the same, with search_knowledge served through `summary_proxy.py` (a `Document summary:` line per hit),
             container_describe allowed, and `prompts/summaries.md` appended to the prompt

    python run.py run --setup baseline --out E:/tmp/con-160/runs --tool-server E:/tmp/con-146/proto/Connapse.AgentSearch.Prototype.exe \
        --corpus E:/tmp/con-146/corpus --rg E:/tmp/con-142/bin/rg.exe --cwd E:/tmp/con-160/agent-cwd --pat-file E:/tmp/con-146/pat.txt \
        --summary-cache E:/tmp/con-160/doc-summaries.jsonl
    python run.py judge --out E:/tmp/con-160/runs
    python run.py tiebreak --out E:/tmp/con-160/runs      # prints the already-right questions summaries missed
    python run.py run --setup summaries --attempt 2 --only <those ids> ...   # then --attempt 3, then judge again
    python run.py summary --out E:/tmp/con-160/runs --summary-cache E:/tmp/con-160/doc-summaries.jsonl
"""
import argparse
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).parent
CON146 = HERE.parent / "con-146"
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(CON146))
import run as con146  # noqa: E402  (also applies CON-146's one-word judge prompt change)
import run_agent  # noqa: E402

DESCRIBE = "mcp__connapse__container_describe"
run_agent.SETUPS["summaries"] = run_agent.RAG_TOOLS + [DESCRIBE] + run_agent.FILE_TOOLS
SETUPS = {
    "baseline": ("both", [CON146 / "prompts" / "base.md", CON146 / "prompts" / "files_first.md"]),
    "summaries": ("summaries", [CON146 / "prompts" / "base.md", CON146 / "prompts" / "files_first.md",
                                HERE / "prompts" / "summaries.md"]),
}
# CON-146's files_first run, judged correct (pass-bar.md lists the same 31).
CON146_RIGHT = {"qst_0001", "qst_0024", "qst_0034", "qst_0042", "qst_0046", "qst_0055", "qst_0067", "qst_0078",
                "qst_0081", "qst_0083", "qst_0091", "qst_0108", "qst_0178", "qst_0191", "qst_0193", "qst_0196",
                "qst_0213", "qst_0214", "qst_0235", "qst_0240", "qst_0244", "qst_0245", "qst_0248", "qst_0251",
                "qst_0255", "qst_0268", "qst_0270", "qst_0273", "qst_0282", "qst_0289", "qst_0299"}
CON146_BASE = {"hard": 19, "control": 12, "cost": 0.204}
load_jsonl = con146.load_jsonl
mcnemar_exact = con146.mcnemar_exact


def run_dir(out, setup, attempt=1):
    return Path(out) / (setup if attempt == 1 else f"{setup}-a{attempt}")


def mcp_config(setup, args, out):
    files = {"type": "stdio", "command": args.tool_server, "args": ["--root", args.corpus, "--rg", args.rg]}
    if setup == "baseline":
        connapse = {"type": "http", "url": args.connapse, "headers": {"X-Api-Key": "${CON142_PAT}"}}
    else:
        connapse = {"type": "stdio", "command": sys.executable,
                    "args": [str(HERE / "summary_proxy.py"), "--url", args.connapse, "--corpus", args.corpus,
                             "--cache", args.summary_cache, "--model", args.summary_model],
                    "env": {"CON142_PAT": "${CON142_PAT}"}}
    path = out / f"mcp-{setup}.json"
    path.write_text(json.dumps({"mcpServers": {"connapse": connapse, "files": files}}), encoding="utf-8")
    return path


def system_prompt(setup):
    return "\n".join(p.read_text(encoding="utf-8") for p in SETUPS[setup][1])


def projected_spend(args, n_questions):
    """Spent so far (agent, judge, Haiku summaries) plus the rest of the plan at the per-question rates seen so far."""
    runs = [r for p in Path(args.out).glob("*/runs.jsonl") for r in load_jsonl(p)]
    judged_recs = [r for p in Path(args.out).glob("*/judged.jsonl") for r in load_jsonl(p)]
    cache = load_jsonl(Path(args.summary_cache)) if args.summary_cache and Path(args.summary_cache).exists() else []
    agent = sum(r["cost_usd"] or 0 for r in runs)
    judge_cost = sum(r["judge"].get("judge_cost_usd") or 0 for r in judged_recs)
    haiku = sum(r["cost_usd"] or 0 for r in cache)
    per_agent = agent / len(runs) if runs else CON146_BASE["cost"]
    summ_done = sum(r["setup"] == "summaries" for r in runs)
    per_haiku = haiku / summ_done if summ_done else 0.07
    main_done = sum(r.get("attempt", 1) == 1 for r in runs)
    left = 2 * n_questions - main_done + 6  # both setups, plus an allowance of 6 tiebreak reruns
    left_summ = n_questions - sum(r["setup"] == "summaries" and r.get("attempt", 1) == 1 for r in runs) + 6
    spent = agent + judge_cost + haiku
    return spent, spent + left * (per_agent + 0.025) + left_summ * per_haiku


def run(args, questions):
    tools, _ = SETUPS[args.setup]
    if "CON142_PAT" not in os.environ:
        os.environ["CON142_PAT"] = Path(args.pat_file).read_text(encoding="utf-8").strip()
    # A search whose hits are new to the summary cache waits on Haiku; let it run past Claude Code's MCP default.
    os.environ["MCP_TOOL_TIMEOUT"] = "600000"
    run_agent.CONNAPSE_HIDDEN = [t for t in run_agent.CONNAPSE_HIDDEN
                                 if args.setup == "baseline" or t != "container_describe"]
    out = run_dir(args.out, args.setup, args.attempt)
    out.mkdir(parents=True, exist_ok=True)
    Path(args.cwd).mkdir(parents=True, exist_ok=True)
    mcp_path = mcp_config(args.setup, args, out)
    prompt = system_prompt(args.setup)
    summary = out / "runs.jsonl"
    done = {r["id"] for r in load_jsonl(summary)}
    todo = [q for q in questions if q["id"] not in done and (not args.only or q["id"] in args.only)]
    for i, q in enumerate(todo, 1):
        if args.budget:
            spent, projected = projected_spend(args, len(questions))
            if projected > args.budget:
                sys.exit(f"budget stop before {q['id']}: spent ${spent:.2f}, projected ${projected:.2f} > ${args.budget}")
        rec = run_agent.run_one(q, tools, args, out, mcp_path, prompt)
        rec["setup"], rec["attempt"] = args.setup, args.attempt
        if rec["unexpected_tools"]:
            sys.exit(f"agent saw tools outside its setup: {rec['unexpected_tools']}")
        if rec["subtype"] is None:
            sys.exit(f"{q['id']}: claude exited {rec['exit_code']} without a result; not recorded, rerun to retry it")
        with summary.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
        print(f"[{out.name} {i}/{len(todo)}] {q['id']} calls={rec['tool_calls']} cost={rec['cost_usd']} "
              f"turns={rec['num_turns']} {rec['wall_s']}s", flush=True)


def judge(args, questions):
    by_id = {q["id"]: q for q in questions}
    for d in sorted(p for p in Path(args.out).iterdir() if (p / "runs.jsonl").exists()):
        judged_path = d / "judged.jsonl"
        done = {r["id"] for r in load_jsonl(judged_path)}
        for rec in load_jsonl(d / "runs.jsonl"):
            if rec["id"] in done:
                continue
            rec["judge"] = con146.con142_judge.grade(by_id[rec["id"]], rec, args.model)
            with judged_path.open("a", encoding="utf-8") as f:
                f.write(json.dumps(rec) + "\n")
            print(f"{d.name} {rec['id']} correct={rec['judge']['correct']}", flush=True)


def judged(out, setup, attempt=1):
    return {r["id"]: r for r in load_jsonl(run_dir(out, setup, attempt) / "judged.jsonl")}


def already_right(out):
    base = judged(out, "baseline")
    return sorted(i for i in CON146_RIGHT if base.get(i, {}).get("judge", {}).get("correct"))


def missed_already_right(out):
    summ = judged(out, "summaries")
    return [i for i in already_right(out) if i in summ and not summ[i]["judge"]["correct"]]


def tiebreak(args, questions):
    ids = missed_already_right(args.out)
    print(" ".join(ids) if ids else "none: summaries got every already-right question right")


def summary(args, questions):
    kinds = {q["id"]: q["kind"] for q in questions}
    all_ids = set(kinds)
    hard_ids = [q["id"] for q in questions if q["kind"] == "hard"]
    rows = {s: judged(args.out, s) for s in SETUPS}
    report = {}
    print("| Setup | Hard (36) | Control (12) | Cost per question | Tool calls | Turns | Used search_knowledge | "
          "Called container_describe |")
    print("|---|---|---|---|---|---|---|---|")
    for s, recs in rows.items():
        rs = list(recs.values())
        if not rs:
            continue
        hard = sum(r["judge"]["correct"] for r in rs if kinds[r["id"]] == "hard")
        control = sum(r["judge"]["correct"] for r in rs if kinds[r["id"]] == "control")
        cost = sum(r["cost_usd"] or 0 for r in rs) / len(rs)
        used_rag = sum(any(c["name"] == "search_knowledge" for c in r["calls"]) for r in rs)
        described = sum(any(c["name"] == "container_describe" for c in r["calls"]) for r in rs)
        report[s] = {"n": len(rs), "hard": hard, "control": control, "cost": cost}
        print(f"| {s} | {hard} ({100 * hard / 36:.0f}%) | {control} | ${cost:.4f} | "
              f"{sum(r['tool_calls'] for r in rs) / len(rs):.1f} | {sum(r['num_turns'] or 0 for r in rs) / len(rs):.1f} | "
              f"{used_rag}/{len(rs)} | {described}/{len(rs)} |")
    print(f"| CON-146 files_first | {CON146_BASE['hard']} | {CON146_BASE['control']} | ${CON146_BASE['cost']:.4f} | | | | |")

    def correct(s, qid, attempt=1):
        recs = rows[s] if attempt == 1 else judged(args.out, s, attempt)
        return recs.get(qid, {}).get("judge", {}).get("correct", False)

    incomplete = [s for s in SETUPS if set(rows[s]) != all_ids or any(r.get("cost_usd") is None for r in rows[s].values())]
    if incomplete:
        print(f"\nNo verdict: {incomplete} lack a judged answer with a cost for every question.")
    else:
        base, summ = report["baseline"], report["summaries"]
        gain = summ["hard"] - base["hard"]
        cost = summ["cost"] / base["cost"] - 1
        right = already_right(args.out)
        dropped, unsettled = [], []
        for i in missed_already_right(args.out):
            reruns = [judged(args.out, "summaries", a).get(i) for a in (2, 3)]
            if any(r is None for r in reruns):
                unsettled.append(i)
            elif sum(not r["judge"]["correct"] for r in reruns) >= 1:  # wrong in at least 2 of 3 attempts
                dropped.append(i)
        bars = [gain >= 4, cost <= 0.20, not dropped and not unsettled]
        b = sum(correct("summaries", i) and not correct("baseline", i) for i in hard_ids)
        c = sum(correct("baseline", i) and not correct("summaries", i) for i in hard_ids)
        print(f"\n1. Finds more: hard {gain:+d} ({100 * gain / 36:+.1f} points; bar >= +4) {'PASS' if bars[0] else 'FAIL'}")
        print(f"2. Cost: {100 * cost:+.1f}% per question (bar <= +20%) {'PASS' if bars[1] else 'FAIL'}")
        print(f"3. No drop: {len(right)} already right; dropped {dropped}"
              + (f"; reruns missing for {unsettled}" if unsettled else "") + f" {'PASS' if bars[2] else 'FAIL'}")
        print(f"Hard discordant: {b} won / {c} lost, exact McNemar p={mcnemar_exact(b, c):.3f}")
        verdict = "yes" if all(bars) else ("not as built" if bars[0] else "no")
        print(f"Verdict: {verdict}" + (" (pending tiebreak reruns)" if unsettled else ""))

    cache = Path(args.summary_cache) if args.summary_cache else None
    if cache and cache.exists():
        recs = load_jsonl(cache)
        spent = sum(r["cost_usd"] or 0 for r in recs)
        print(f"\nDocument summaries written: {len(recs)}, Haiku ${spent:.2f} (${spent / max(len(recs), 1):.4f} each; "
              f"all 50,000 records would be about ${50_000 * spent / max(len(recs), 1):,.0f}); "
              f"{sum(r['truncated'] for r in recs)} truncated")

    print("\nPer-question correctness (baseline / summaries / CON-146 files_first):")
    for q in questions:
        print(q["id"], q["kind"], " ".join("Y" if correct(s, q["id"]) else ("-" if q["id"] in rows[s] else "?")
                                           for s in SETUPS), "Y" if q["id"] in CON146_RIGHT else "-")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["run", "judge", "tiebreak", "summary"])
    p.add_argument("--setup", choices=SETUPS)
    p.add_argument("--attempt", type=int, default=1, choices=[1, 2, 3])
    p.add_argument("--questions", default=str(CON146 / "questions.json"))
    p.add_argument("--out", required=True)
    p.add_argument("--connapse", default="http://localhost:5160/mcp")
    p.add_argument("--tool-server")
    p.add_argument("--corpus")
    p.add_argument("--rg")
    p.add_argument("--cwd", help="empty folder the agent runs in")
    p.add_argument("--pat-file")
    p.add_argument("--summary-cache")
    p.add_argument("--summary-model", default="haiku")
    p.add_argument("--claude", default="claude")
    p.add_argument("--model", default="sonnet")
    p.add_argument("--max-turns", type=int, default=25)
    p.add_argument("--timeout", type=int, default=1800)
    p.add_argument("--only", nargs="*")
    p.add_argument("--budget", type=float, help="stop before a question if projected total spend passes this (USD)")
    args = p.parse_args()
    questions = json.loads(Path(args.questions).read_text(encoding="utf-8"))
    {"run": run, "judge": judge, "tiebreak": tiebreak, "summary": summary}[args.action](args, questions)


if __name__ == "__main__":
    main()
