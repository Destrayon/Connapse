"""CON-142: is the Recursive chunker fast enough to replace Semantic for the eval corpus?

Picks 20 files the Semantic container already finished, uploads the same files to a probe container with
the Recursive strategy, and compares per-file ingest time from Connapse.Web's log line
"Successfully ingested document <id>: <n> chunks in <ms>ms". Both runs share the same 4 ingestion workers
and the same 2-CPU Ollama.

    python chunker_probe.py clear-backlog --base ... --email ... --password ... --container-id <semantic id>
    python chunker_probe.py probe --base ... --email ... --password ... --container-id <semantic id> \
        --corpus E:/tmp/con-142/corpus --web-log E:/tmp/con-142/web.log --out E:/tmp/con-142/chunker_probe.json

clear-backlog deletes the Semantic container's not-yet-ingested documents so the probe isn't queued behind
them; the finished ones stay.
"""
import argparse
import json
import re
import statistics
import subprocess
import time
import urllib.error
from pathlib import Path

from ingest import CODE_EXTENSIONS, call, multipart

LOG_LINE = re.compile(r"Successfully ingested document ([0-9a-f-]{36}): (\d+) chunks in (\d+)ms")


def psql(sql):
    return subprocess.run(["docker", "exec", "-i", "con142-postgres-1", "psql", "-U", "connapse", "-d", "connapse",
                           "-At", "-F", "\t", "-v", "ON_ERROR_STOP=1"], input=sql, capture_output=True, text=True,
                          encoding="utf-8", check=True).stdout


def docs(cid, where):
    rows = psql(f"select id, path, file_name, size_bytes, ingestion_status from documents "
                f"where container_id = '{cid}' and {where};")
    return [dict(zip(("id", "path", "file_name", "size", "status"), r.split("\t"))) for r in rows.splitlines() if r]


def log_times(web_log):
    return {m[1]: (int(m[2]), int(m[3])) for m in LOG_LINE.finditer(Path(web_log).read_text(encoding="utf-8", errors="replace"))}


def clear_backlog(args, jwt):
    pending = docs(args.container_id, "ingestion_status <> 'Ready'")
    for i in range(0, len(pending), 100):
        resp = call("POST", f"{args.base}/api/containers/{args.container_id}/files/bulk-delete", jwt,
                    {"fileIds": [d["id"] for d in pending[i:i + 100]]})
        print(f"deleted {resp['deletedCount'] if 'deletedCount' in resp else resp}", flush=True)
    print(f"cleared {len(pending)} pending documents; {len(docs(args.container_id, 'true'))} remain")


def probe(args, jwt):
    times = log_times(args.web_log)
    ready = [d for d in docs(args.container_id, "ingestion_status = 'Ready'") if d["id"] in times]
    ready.sort(key=lambda d: d["id"])
    sample = ready[:args.n]
    try:
        pid = call("GET", f"{args.base}/api/containers/by-name/con142-probe-recursive", jwt)["id"]
    except urllib.error.HTTPError:
        pid = call("POST", f"{args.base}/api/containers", jwt,
                   {"name": "con142-probe-recursive", "description": "CON-142 chunker timing probe"})["id"]
    by_folder = {}
    for d in sample:
        # documents.path is the full path, file name included.
        by_folder.setdefault(d["path"][:-len(d["file_name"])], []).append(d["file_name"])
    for folder, names in by_folder.items():
        files = []
        for n in names:
            original = n[:-4] if n.endswith(".txt") and Path(n[:-4]).suffix in CODE_EXTENSIONS else n
            files.append((n, (Path(args.corpus) / folder.strip("/") / original).read_bytes()))
        body, ctype = multipart(files, folder, strategy="Recursive")
        call("POST", f"{args.base}/api/containers/{pid}/files", jwt, body, ctype)
    print(f"uploaded {len(sample)} files to probe container {pid}", flush=True)

    while True:
        probe_docs = docs(pid, "true")
        if probe_docs and all(d["status"] in ("Ready", "Failed") for d in probe_docs):
            break
        time.sleep(15)
    times = log_times(args.web_log)
    key = {d["path"]: d for d in probe_docs}
    rows = []
    for d in sample:
        r = key.get(d["path"])
        if r and r["id"] in times:
            rows.append({"file": d["path"], "size": int(d["size"]),
                         "semantic_ms": times[d["id"]][1], "semantic_chunks": times[d["id"]][0],
                         "recursive_ms": times[r["id"]][1], "recursive_chunks": times[r["id"]][0]})
    sem = statistics.median(r["semantic_ms"] for r in rows)
    rec = statistics.median(r["recursive_ms"] for r in rows)
    summary = {"files": len(rows), "median_semantic_s": round(sem / 1000, 1), "median_recursive_s": round(rec / 1000, 1),
               "speedup": round(sem / rec, 2), "total_semantic_s": round(sum(r["semantic_ms"] for r in rows) / 1000, 1),
               "total_recursive_s": round(sum(r["recursive_ms"] for r in rows) / 1000, 1),
               "probe_container": pid, "rows": rows}
    Path(args.out).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k != "rows"}))


def fresh(args, jwt):
    """Recursive on files neither container has seen, so the embedding cache (keyed by chunk text) can't help.
    Compared as ms per KB against every Semantic file already ingested."""
    pid = call("GET", f"{args.base}/api/containers/by-name/con142-probe-recursive", jwt)["id"]
    seen = {d["path"] for d in docs(args.container_id, "true") + docs(pid, "true")}
    corpus = Path(args.corpus)
    candidates = []
    for f in sorted(corpus.rglob("*")):
        if f.is_file():
            name = f.name + (".txt" if f.suffix in CODE_EXTENSIONS else "")
            path = "/" + f.relative_to(corpus).parent.as_posix().strip(".").strip("/") + "/"
            path = path.replace("//", "/")
            if path + name not in seen:
                candidates.append((path, name, f))
    sample = candidates[::max(1, len(candidates) // args.n)][:args.n]
    for path, name, f in sample:
        body, ctype = multipart([(name, f.read_bytes())], path, strategy="Recursive")
        call("POST", f"{args.base}/api/containers/{pid}/files", jwt, body, ctype)
    wanted = {p + n for p, n, _ in sample}
    print(f"uploaded {len(sample)} fresh files", flush=True)
    while True:
        got = [d for d in docs(pid, "true") if d["path"] in wanted]
        if len(got) == len(sample) and all(d["status"] in ("Ready", "Failed") for d in got):
            break
        time.sleep(15)
    times = log_times(args.web_log)

    def ms_per_kb(rows):
        return [times[d["id"]][1] / max(1, int(d["size"]) / 1024) for d in rows if d["id"] in times]

    rec = ms_per_kb(got)
    sem = ms_per_kb(docs(args.container_id, "ingestion_status = 'Ready'"))
    summary = {"recursive_files": len(rec), "semantic_files": len(sem),
               "median_recursive_ms_per_kb": round(statistics.median(rec)), "median_semantic_ms_per_kb": round(statistics.median(sem)),
               "speedup": round(statistics.median(sem) / statistics.median(rec), 2),
               "recursive_s_per_file": [round(times[d["id"]][1] / 1000, 1) for d in got if d["id"] in times]}
    Path(args.out).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["clear-backlog", "probe", "fresh"])
    p.add_argument("--base", required=True)
    p.add_argument("--email", required=True)
    p.add_argument("--password", required=True)
    p.add_argument("--container-id", required=True)
    p.add_argument("--corpus")
    p.add_argument("--web-log")
    p.add_argument("--n", type=int, default=20)
    p.add_argument("--out")
    args = p.parse_args()
    args.base = args.base.rstrip("/")
    jwt = call("POST", f"{args.base}/api/v1/auth/token", body={"email": args.email, "password": args.password})["accessToken"]
    {"clear-backlog": clear_backlog, "probe": probe, "fresh": fresh}[args.action](args, jwt)


if __name__ == "__main__":
    main()
