"""CON-174 runner, quick tests, judge and summary on top of CON-142's `run_agent.py` and `judge.py` and CON-146's setup
(all unchanged). See pass-bar.md.

  baseline  exactly CON-146's files_first: five tools, container_describe hidden
  folder    the same, plus container_describe served through `describe_proxy.py` (returning this run's container
            summary) and one set of instructions from prompts/describe-v*.md appended to the prompt

    python run.py pick-quick --dataset E:/tmp/con-32/cache/datasets/erb-50k/69916e31c68a   # writes quick-questions.json
    python run.py project                                 # projected spend before anything runs
    python run.py prompt --setup folder --instructions prompts/describe-v1.md               # offline: print a prompt
    python run.py quick --instructions prompts/describe-v1.md --out E:/tmp/con-174/runs <stack args>
                                                          # 10 quick questions, folder setup; counts describe-first
    python run.py run --setup baseline --out E:/tmp/con-174/runs <stack args> --budget 25
    python run.py run --setup folder --instructions prompts/describe-vN.md --out E:/tmp/con-174/runs <stack args> --budget 25
    python run.py judge --out E:/tmp/con-174/runs
    python run.py tiebreak --out E:/tmp/con-174/runs      # prints the already-right questions the folder setup missed
    python run.py run --setup folder --attempt 2 --only <those ids> ...   # then --attempt 3, then judge again
    python run.py summary --out E:/tmp/con-174/runs

<stack args>: --tool-server E:/tmp/con-146/proto/Connapse.AgentSearch.Prototype.exe --corpus E:/tmp/con-146/corpus
    --rg E:/tmp/con-142/bin/rg.exe --cwd E:/tmp/con-174/agent-cwd --pat-file E:/tmp/con-174/pat.txt
    --summary E:/tmp/con-174/container-summary.json
"""
import argparse
import hashlib
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

DESCRIBE = "container_describe"
CONTAINER = "eval-erb-50k"
run_agent.SETUPS["baseline"] = run_agent.SETUPS["both"]
run_agent.SETUPS["folder"] = run_agent.RAG_TOOLS + [f"mcp__connapse__{DESCRIBE}"] + run_agent.FILE_TOOLS
HIDDEN = {"baseline": list(run_agent.CONNAPSE_HIDDEN),
          "folder": [t for t in run_agent.CONNAPSE_HIDDEN if t != DESCRIBE]}
SETUPS = ["baseline", "folder"]
PROMPTS = [CON146 / "prompts" / "base.md", CON146 / "prompts" / "files_first.md"]
QUICK = HERE / "quick-questions.json"
QUICK_N, QUICK_NEEDED, QUICK_BUDGET = 10, 9, 5.0
# CON-146's files_first run, judged correct (pass-bar.md lists the same 31).
CON146_RIGHT = {"qst_0001", "qst_0024", "qst_0034", "qst_0042", "qst_0046", "qst_0055", "qst_0067", "qst_0078",
                "qst_0081", "qst_0083", "qst_0091", "qst_0108", "qst_0178", "qst_0191", "qst_0193", "qst_0196",
                "qst_0213", "qst_0214", "qst_0235", "qst_0240", "qst_0244", "qst_0245", "qst_0248", "qst_0251",
                "qst_0255", "qst_0268", "qst_0270", "qst_0273", "qst_0282", "qst_0289", "qst_0299"}
# CON-160's baseline on the same stack, the same day: the drift check, and the starting rate for the budget guard.
CON160_BASE = {"hard": 22, "control": 11, "cost": 0.1602}
JUDGE_PER_QUESTION = 0.025
TIEBREAK_ALLOWANCE = 6
load_jsonl = con146.load_jsonl
mcnemar_exact = con146.mcnemar_exact


def run_dir(out, setup, attempt=1):
    return Path(out) / (setup if attempt == 1 else f"{setup}-a{attempt}")


def described_first(rec):
    """True when the agent's first tool call was container_describe on the eval container."""
    first = rec["calls"][0] if rec["calls"] else None
    return bool(first and first["name"] == DESCRIBE and (first.get("input") or {}).get("containerId") == CONTAINER)


def mcp_config(setup, args, out):
    files = {"type": "stdio", "command": args.tool_server, "args": ["--root", args.corpus, "--rg", args.rg]}
    if setup == "baseline":
        connapse = {"type": "http", "url": args.connapse, "headers": {"X-Api-Key": "${CON142_PAT}"}}
    else:
        connapse = {"type": "stdio", "command": sys.executable,
                    "args": [str(HERE / "describe_proxy.py"), "--url", args.connapse, "--summary", args.summary],
                    "env": {"CON142_PAT": "${CON142_PAT}"}}
    path = out / f"mcp-{setup}.json"
    path.write_text(json.dumps({"mcpServers": {"connapse": connapse, "files": files}}), encoding="utf-8")
    return path


def system_prompt(setup, instructions=None):
    parts = list(PROMPTS)
    if setup == "folder":
        if not instructions:
            sys.exit("the folder setup needs --instructions")
        parts.append(Path(instructions))
    return "\n".join(p.read_text(encoding="utf-8") for p in parts)


def all_runs(out):
    return [r for p in Path(out).glob("*/runs.jsonl") for r in load_jsonl(p)] if out else []


def projected_spend(args, n_questions):
    """Spent so far on the full run (agent and judge) plus the rest of it at the per-question rate seen so far."""
    runs = [r for r in all_runs(args.out) if r.get("attempt", 1) != "quick"]
    judged_recs = [r for p in Path(args.out).glob("*/judged.jsonl") for r in load_jsonl(p)] if args.out else []
    agent = sum(r["cost_usd"] or 0 for r in runs)
    judge_cost = sum(r["judge"].get("judge_cost_usd") or 0 for r in judged_recs)
    # Start from CON-160's baseline rate, weighted as 12 questions, so one expensive early question can't stop the run.
    per_agent = (agent + 12 * CON160_BASE["cost"]) / (len(runs) + 12)
    main_done = sum(r.get("attempt", 1) == 1 for r in runs)
    left_runs = len(SETUPS) * n_questions - main_done + TIEBREAK_ALLOWANCE
    left_judged = len(SETUPS) * n_questions + TIEBREAK_ALLOWANCE - len(judged_recs)
    spent = agent + judge_cost
    return spent, spent + left_runs * per_agent + left_judged * JUDGE_PER_QUESTION


def quick_spent(out):
    return sum(r["cost_usd"] or 0 for r in all_runs(out) if r.get("attempt") == "quick")


def project(args, questions):
    spent, projected = projected_spend(args, len(questions))
    per_round = QUICK_N * CON160_BASE["cost"] * 1.1
    print(f"Full run: spent ${spent:.2f}; projected ${projected:.2f} for agent and judge "
          f"({len(SETUPS)} x {len(questions)} questions + {TIEBREAK_ALLOWANCE} tiebreak reruns).")
    print(f"Quick tests: about ${per_round:.2f} per candidate ({QUICK_N} questions, no judge); "
          f"capped at ${QUICK_BUDGET:.2f} in all. Container summary: about $0.03.")


def pick_quick(args, questions):
    """10 semantic questions not in the scored set, by a stable hash of their ID."""
    import pyarrow.parquet as pq
    scored = {q["id"] for q in questions}
    rows = pq.read_table(Path(args.dataset) / "questions.parquet").to_pylist()
    pool = [r for r in rows if r["question_type"] == "semantic" and r["question_id"] not in scored]
    pool.sort(key=lambda r: hashlib.sha256(f"con-174:{r['question_id']}".encode()).hexdigest())
    quick = [{"id": r["question_id"], "kind": "quick", "area": ",".join(r["source_types"]), "question": r["question"]}
             for r in pool[:QUICK_N]]
    QUICK.write_text(json.dumps(quick, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(quick)} quick questions to {QUICK.name}: {' '.join(q['id'] for q in quick)}")


def ask(args, questions, setup, out, attempt, budget_check):
    if "CON142_PAT" not in os.environ:
        os.environ["CON142_PAT"] = Path(args.pat_file).read_text(encoding="utf-8").strip()
    out.mkdir(parents=True, exist_ok=True)
    Path(args.cwd).mkdir(parents=True, exist_ok=True)
    run_agent.CONNAPSE_HIDDEN = HIDDEN[setup]
    prompt = system_prompt(setup, args.instructions)
    (out / "system-prompt.md").write_text(prompt, encoding="utf-8")
    mcp_path = mcp_config(setup, args, out)
    summary = out / "runs.jsonl"
    done = {r["id"] for r in load_jsonl(summary)}
    todo = [q for q in questions if q["id"] not in done and (not args.only or q["id"] in args.only)]
    for i, q in enumerate(todo, 1):
        budget_check(q)
        rec = run_agent.run_one(q, setup, args, out, mcp_path, prompt)
        rec["setup"], rec["attempt"] = setup, attempt
        rec["instructions"] = Path(args.instructions).name if setup == "folder" else None
        if rec["unexpected_tools"]:
            sys.exit(f"agent saw tools outside its setup: {rec['unexpected_tools']}")
        if rec["subtype"] is None:
            sys.exit(f"{q['id']}: claude exited {rec['exit_code']} without a result; not recorded, rerun to retry it")
        with summary.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
        print(f"[{out.name} {i}/{len(todo)}] {q['id']} calls={rec['tool_calls']} cost={rec['cost_usd']} "
              f"turns={rec['num_turns']} described_first={described_first(rec)} {rec['wall_s']}s", flush=True)


def run(args, questions):
    if args.setup == "folder" and args.attempt == 1:
        _check_instructions_consistent(args)

    def budget_check(q):
        if args.budget:
            spent, projected = projected_spend(args, len(questions))
            if projected > args.budget:
                sys.exit(f"budget stop before {q['id']}: spent ${spent:.2f}, projected ${projected:.2f} > ${args.budget}")

    ask(args, questions, args.setup, run_dir(args.out, args.setup, args.attempt), args.attempt, budget_check)


def _check_instructions_consistent(args):
    out = Path(args.out) / f"quick-{Path(args.instructions).stem}"
    recs = load_jsonl(out / "runs.jsonl") if (out / "runs.jsonl").exists() else []
    hits = sum(described_first(r) for r in recs)
    if len(recs) < QUICK_N or hits < QUICK_NEEDED:
        sys.exit(f"{Path(args.instructions).name} has not passed the quick test ({hits}/{len(recs)} described first; "
                 f"needs {QUICK_NEEDED}/{QUICK_N})")


def quick(args, questions):
    quick_qs = json.loads(QUICK.read_text(encoding="utf-8"))
    out = Path(args.out) / f"quick-{Path(args.instructions).stem}"

    def budget_check(q):
        spent = quick_spent(args.out)
        if spent + CON160_BASE["cost"] * 1.5 > QUICK_BUDGET:
            sys.exit(f"quick-test budget stop before {q['id']}: ${spent:.2f} spent of ${QUICK_BUDGET:.2f}")

    ask(args, quick_qs, "folder", out, "quick", budget_check)
    recs = load_jsonl(out / "runs.jsonl")
    hits = sum(described_first(r) for r in recs)
    verdict = "consistent" if hits >= QUICK_NEEDED and len(recs) >= QUICK_N else "not consistent"
    print(f"{Path(args.instructions).name}: described first in {hits}/{len(recs)} ({verdict}; bar {QUICK_NEEDED}/{QUICK_N}); "
          f"this candidate ${sum(r['cost_usd'] or 0 for r in recs):.2f}, quick tests ${quick_spent(args.out):.2f} in all")
    for r in recs:
        print(f"  {r['id']} first={r['calls'][0]['name'] if r['calls'] else '-'} "
              f"describe_calls={sum(c['name'] == DESCRIBE for c in r['calls'])}")


def prompt_only(args, questions):
    sys.stdout.reconfigure(encoding="utf-8")
    print(system_prompt(args.setup, args.instructions))


def scored_dirs(out):
    return sorted(p for p in Path(out).iterdir() if (p / "runs.jsonl").exists() and not p.name.startswith("quick-"))


def judge(args, questions):
    by_id = {q["id"]: q for q in questions}
    for d in scored_dirs(args.out):
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
    folder = judged(out, "folder")
    return [i for i in already_right(out) if i in folder and not folder[i]["judge"]["correct"]]


def tiebreak(args, questions):
    ids = missed_already_right(args.out)
    print(" ".join(ids) if ids else "none: the folder setup got every already-right question right")


def summary(args, questions):
    kinds = {q["id"]: q["kind"] for q in questions}
    hard_ids = [q["id"] for q in questions if q["kind"] == "hard"]
    rows = {s: judged(args.out, s) for s in SETUPS}
    report = {}
    print("| Setup | Hard (36) | Control (12) | Cost per question | Tool calls | Turns | search_knowledge calls | "
          "Described first |")
    print("|---|---|---|---|---|---|---|---|")
    for s, recs in rows.items():
        rs = list(recs.values())
        if not rs:
            continue
        hard = sum(r["judge"]["correct"] for r in rs if kinds[r["id"]] == "hard")
        control = sum(r["judge"]["correct"] for r in rs if kinds[r["id"]] == "control")
        cost = sum(r["cost_usd"] or 0 for r in rs) / len(rs)
        report[s] = {"hard": hard, "control": control, "cost": cost, "described": sum(map(described_first, rs))}
        searches = sum(c["name"] == "search_knowledge" for r in rs for c in r["calls"])
        print(f"| {s} | {hard} ({100 * hard / 36:.0f}%) | {control} | ${cost:.4f} | "
              f"{sum(r['tool_calls'] for r in rs) / len(rs):.1f} | {sum(r['num_turns'] or 0 for r in rs) / len(rs):.1f} | "
              f"{searches} | {report[s]['described']}/{len(rs)} |")
    print(f"| CON-160 baseline | {CON160_BASE['hard']} | {CON160_BASE['control']} | ${CON160_BASE['cost']:.4f} | | | | |")

    def correct(s, qid, attempt=1):
        recs = rows[s] if attempt == 1 else judged(args.out, s, attempt)
        return recs.get(qid, {}).get("judge", {}).get("correct", False)

    incomplete = [s for s in SETUPS if set(rows[s]) != set(kinds) or any(r.get("cost_usd") is None for r in rows[s].values())]
    if incomplete:
        print(f"\nNo verdict: {incomplete} lack a judged answer with a cost for every question.")
    else:
        base, folder = report["baseline"], report["folder"]
        gain = folder["hard"] - base["hard"]
        cost = folder["cost"] / base["cost"] - 1
        right = already_right(args.out)
        dropped, unsettled = [], []
        for i in missed_already_right(args.out):
            reruns = [judged(args.out, "folder", a).get(i) for a in (2, 3)]
            if any(r is None for r in reruns):
                unsettled.append(i)
            elif sum(not r["judge"]["correct"] for r in reruns) >= 1:  # wrong in at least 2 of 3 attempts
                dropped.append(i)
        bars = [gain >= 4, cost <= 0.20, not dropped and not unsettled]
        b = sum(correct("folder", i) and not correct("baseline", i) for i in hard_ids)
        c = sum(correct("baseline", i) and not correct("folder", i) for i in hard_ids)
        print(f"\n1. More hard right: {gain:+d} ({100 * gain / 36:+.1f} points; bar >= +4) {'PASS' if bars[0] else 'FAIL'}")
        print(f"2. Cost: {100 * cost:+.1f}% per question (bar <= +20%) {'PASS' if bars[1] else 'FAIL'}")
        print(f"3. No drop: {len(right)} already right; dropped {dropped}"
              + (f"; reruns missing for {unsettled}" if unsettled else "") + f" {'PASS' if bars[2] else 'FAIL'}")
        print(f"Hard discordant: {b} won / {c} lost, exact McNemar p={mcnemar_exact(b, c):.3f}")
        held = folder["described"] >= 43
        verdict = "yes" if all(bars) else ("not as built" if bars[0] else "no")
        print(f"Verdict: {verdict}" + (" (pending tiebreak reruns)" if unsettled else "")
              + ("" if held else f" (instructions did not hold: {folder['described']}/48 described first)"))

    print("\nPer-question correctness (baseline / folder / CON-146 files_first):")
    for q in questions:
        print(q["id"], q["kind"], " ".join("Y" if correct(s, q["id"]) else ("-" if q["id"] in rows[s] else "?")
                                           for s in SETUPS), "Y" if q["id"] in CON146_RIGHT else "-")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["pick-quick", "project", "prompt", "quick", "run", "judge", "tiebreak", "summary"])
    p.add_argument("--setup", choices=SETUPS)
    p.add_argument("--attempt", type=int, default=1, choices=[1, 2, 3])
    p.add_argument("--instructions", help="folder setup: one of prompts/describe-v*.md")
    p.add_argument("--questions", default=str(CON146 / "questions.json"))
    p.add_argument("--dataset", help="pick-quick: the erb-50k dataset folder")
    p.add_argument("--out")
    p.add_argument("--connapse", default="http://localhost:5174/mcp")
    p.add_argument("--tool-server")
    p.add_argument("--corpus")
    p.add_argument("--rg")
    p.add_argument("--cwd", help="empty folder the agent runs in")
    p.add_argument("--pat-file")
    p.add_argument("--summary", help="container-summary.json from container_summary.py")
    p.add_argument("--claude", default="claude")
    p.add_argument("--model", default="sonnet")
    p.add_argument("--max-turns", type=int, default=25)
    p.add_argument("--timeout", type=int, default=1800)
    p.add_argument("--only", nargs="*")
    p.add_argument("--budget", type=float, help="stop before a question if projected total spend passes this (USD)")
    args = p.parse_args()
    questions = json.loads(Path(args.questions).read_text(encoding="utf-8"))
    {"pick-quick": pick_quick, "project": project, "prompt": prompt_only, "quick": quick, "run": run, "judge": judge,
     "tiebreak": tiebreak, "summary": summary}[args.action](args, questions)


if __name__ == "__main__":
    main()
