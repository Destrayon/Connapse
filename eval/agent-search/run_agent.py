"""CON-142 runner: asks every question once per setup through `claude -p` and records tools, cost and answer.

One question at a time, at below-normal priority (children inherit it). Raw stream-json goes to
<out>/raw/<setup>/<id>.jsonl; one summary line per question to <out>/runs-<setup>.jsonl.
Already-finished questions are skipped, so the run can be resumed.

    python run_agent.py --setup both --questions questions.json --out E:/tmp/con-142/runs \
        --connapse http://localhost:5142/mcp --tool-server E:/tmp/con-142/proto/Connapse.AgentSearch.Prototype.exe \
        --corpus E:/tmp/con-142/corpus --rg E:/tmp/con-142/bin/rg.exe [--only docs-exact-01]
The Connapse PAT comes from the CON142_PAT environment variable so it never lands in a file in the repo.
"""
import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).parent
BELOW_NORMAL = 0x00004000
RAG_TOOLS = ["mcp__connapse__search_knowledge", "mcp__connapse__get_document"]
FILE_TOOLS = ["mcp__files__grep_files", "mcp__files__find_files", "mcp__files__read_file"]
CONNAPSE_HIDDEN = ["container_create", "container_list", "container_delete", "list_files", "upload_file",
                   "delete_file", "bulk_delete", "bulk_upload", "container_stats", "container_describe"]
SETUPS = {"rag": RAG_TOOLS, "files": FILE_TOOLS, "both": RAG_TOOLS + FILE_TOOLS}


def mcp_config(setup, args, out):
    servers = {}
    if setup in ("rag", "both"):
        servers["connapse"] = {"type": "http", "url": args.connapse,
                               "headers": {"X-Api-Key": os.environ["CON142_PAT"]}}
    if setup in ("files", "both"):
        servers["files"] = {"type": "stdio", "command": args.tool_server,
                            "args": ["--root", args.corpus, "--rg", args.rg]}
    path = out / f"mcp-{setup}.json"
    path.write_text(json.dumps({"mcpServers": servers}), encoding="utf-8")
    return path


def run_one(q, setup, args, out, mcp_path, system_prompt):
    allowed = SETUPS[setup]
    cmd = [args.claude, "-p", q["question"],
           "--model", args.model, "--max-turns", str(args.max_turns),
           "--tools", "", "--strict-mcp-config", "--mcp-config", str(mcp_path), "--setting-sources", "",
           "--system-prompt", system_prompt, "--allowedTools", ",".join(allowed),
           "--disallowedTools", ",".join("mcp__connapse__" + t for t in CONNAPSE_HIDDEN),
           "--no-session-persistence", "--output-format", "stream-json", "--verbose"]
    started = time.time()
    proc = subprocess.run(cmd, cwd=args.cwd, capture_output=True, text=True, encoding="utf-8",
                          creationflags=BELOW_NORMAL if os.name == "nt" else 0, timeout=args.timeout)
    elapsed = time.time() - started
    raw = out / "raw" / setup / f"{q['id']}.jsonl"
    raw.parent.mkdir(parents=True, exist_ok=True)
    raw.write_text(proc.stdout + ("\n#stderr\n" + proc.stderr if proc.stderr else ""), encoding="utf-8")

    calls, init, result = [], None, None
    for line in proc.stdout.splitlines():
        try:
            ev = json.loads(line)
        except json.JSONDecodeError:
            continue
        if ev.get("type") == "system" and ev.get("subtype") == "init":
            init = ev
        elif ev.get("type") == "assistant":
            for block in ev.get("message", {}).get("content", []):
                if block.get("type") == "tool_use":
                    calls.append({"name": block["name"].split("__")[-1], "input": block.get("input")})
        elif ev.get("type") == "result":
            result = ev

    tools_seen = sorted(t for t in (init or {}).get("tools", []))
    unexpected = [t for t in tools_seen if t not in allowed]
    usage = (result or {}).get("usage", {})
    return {
        "id": q["id"], "kind": q["kind"], "area": q["area"], "setup": setup, "model": args.model,
        "answer": (result or {}).get("result", ""), "is_error": (result or {}).get("is_error", True),
        "subtype": (result or {}).get("subtype"), "cost_usd": (result or {}).get("total_cost_usd"),
        "num_turns": (result or {}).get("num_turns"), "wall_s": round(elapsed, 1),
        "input_tokens": usage.get("input_tokens"), "output_tokens": usage.get("output_tokens"),
        "cache_read_tokens": usage.get("cache_read_input_tokens"),
        "cache_write_tokens": usage.get("cache_creation_input_tokens"),
        "tool_calls": len(calls), "calls": calls, "tools_seen": tools_seen, "unexpected_tools": unexpected,
        "mcp_servers": (init or {}).get("mcp_servers"), "exit_code": proc.returncode,
    }


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--setup", required=True, choices=SETUPS)
    p.add_argument("--questions", default=str(HERE / "questions.json"))
    p.add_argument("--out", required=True)
    p.add_argument("--connapse", default="http://localhost:5142/mcp")
    p.add_argument("--tool-server", required=True)
    p.add_argument("--corpus", required=True)
    p.add_argument("--rg", required=True)
    p.add_argument("--cwd", required=True, help="empty folder the agent runs in")
    p.add_argument("--claude", default="claude")
    p.add_argument("--model", default="sonnet")
    p.add_argument("--max-turns", type=int, default=25)
    p.add_argument("--timeout", type=int, default=600)
    p.add_argument("--only", nargs="*")
    p.add_argument("--pause-file", help="wait before each question while this file exists (memory guard)")
    args = p.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    questions = json.loads(Path(args.questions).read_text(encoding="utf-8"))
    if args.only:
        questions = [q for q in questions if q["id"] in args.only]
    system_prompt = ((HERE / "prompts" / "base.md").read_text(encoding="utf-8") + "\n" +
                     (HERE / "prompts" / f"{args.setup}.md").read_text(encoding="utf-8"))
    mcp_path = mcp_config(args.setup, args, out)
    summary = out / f"runs-{args.setup}.jsonl"
    done = set()
    if summary.exists():
        done = {json.loads(l)["id"] for l in summary.read_text(encoding="utf-8").splitlines() if l.strip()}

    for i, q in enumerate(questions, 1):
        if q["id"] in done:
            continue
        while args.pause_file and Path(args.pause_file).exists():
            time.sleep(30)
        rec = run_one(q, args.setup, args, out, mcp_path, system_prompt)
        with summary.open("a", encoding="utf-8") as f:
            f.write(json.dumps(rec) + "\n")
        print(f"[{args.setup} {i}/{len(questions)}] {q['id']} calls={rec['tool_calls']} "
              f"cost={rec['cost_usd']} turns={rec['num_turns']} {rec['wall_s']}s"
              + (f" UNEXPECTED={rec['unexpected_tools']}" if rec["unexpected_tools"] else ""), flush=True)
        if rec["unexpected_tools"]:
            sys.exit("agent saw tools outside its setup; stopping")


if __name__ == "__main__":
    main()
