"""CON-142: exact-string search over Connapse's stored chunk text versus ripgrep over the original files.

For each pattern (a literal string taken from the exact questions), measures:
  - latency: a case-sensitive substring scan of `chunks.content` in the container (psql, median of N)
    against `rg -l -F` over the corpus (process start included, median of N);
  - which files each finds. A file rg finds but no chunk contains means the string straddles a chunk
    edge or the stored text differs from the file. A document whose chunks match but whose file does not
    means text was added to the chunk (a breadcrumb or context header), a false match for grep.

    python chunks_vs_files.py --corpus E:/tmp/con-142/corpus --rg E:/tmp/con-142/bin/rg.exe \
        --pg-container con142-postgres-1 --container con142-corpus --out E:/tmp/con-142/chunks_vs_files.json
"""
import argparse
import json
import re
import statistics
import subprocess
import time
from pathlib import Path

PATTERNS = [
    "Each item's metadata must contain a 'modelId'", "too_many_entries", "does not support connection testing",
    "Unknown FusionMethod", "EnsureIndexesAsync", "PageGuard", "LogSanitizer", "TryRead", "MaxPoolSize",
    "MaxAttempts", "ParserFilesPerProcess", "FusionAlpha", "ef_construction", "frequency-tier",
    "ShouldWithholdDeletions", "with-reranker", "invalid-search", "connapseos.atlassian.net",
    "public static string Sanitize", "private const int",
]


def sql_literal(s):
    return "'" + s.replace("'", "''") + "'"


def psql(args, sql):
    proc = subprocess.run(["docker", "exec", "-i", args.pg_container, "psql", "-U", "connapse", "-d", "connapse",
                           "-At", "-v", "ON_ERROR_STOP=1"], input=sql, capture_output=True, text=True,
                          encoding="utf-8", check=True)
    return proc.stdout


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--corpus", required=True)
    p.add_argument("--rg", required=True)
    p.add_argument("--pg-container", default="con142-postgres-1")
    p.add_argument("--container", default="con142-corpus")
    p.add_argument("--repeats", type=int, default=5)
    p.add_argument("--out", required=True)
    args = p.parse_args()
    corpus = Path(args.corpus)

    cid = psql(args, f"select id from containers where name = {sql_literal(args.container)};").strip()
    scope = f"from chunks c join documents d on d.id = c.document_id where d.container_id = '{cid}'"
    totals = psql(args, f"select count(*), coalesce(sum(length(c.content)),0) {scope};").strip().split("|")
    results = []
    for pat in PATTERNS:
        lit = sql_literal(pat)
        timed = "\\timing on\n" + "".join(f"select count(*) {scope} and strpos(c.content, {lit}) > 0;\n"
                                          for _ in range(args.repeats))
        times = [float(m) for m in re.findall(r"Time: ([\d.]+) ms", psql(args, timed))]
        rows = psql(args, f"select distinct d.path {scope} and strpos(c.content, {lit}) > 0;")
        db_files = {re.sub(r"\.(cs|razor)\.txt$", r".\1", r.strip()).lstrip("/") for r in rows.splitlines() if r.strip()}

        rg_times, out = [], ""
        for _ in range(args.repeats):
            t = time.perf_counter()
            out = subprocess.run([args.rg, "--no-config", "-l", "-F", "-e", pat, "--", str(corpus)],
                                 capture_output=True, text=True, encoding="utf-8").stdout
            rg_times.append((time.perf_counter() - t) * 1000)
        rg_files = {Path(l).resolve().relative_to(corpus.resolve()).as_posix() for l in out.splitlines() if l.strip()}

        results.append({
            "pattern": pat, "db_ms": round(statistics.median(times), 1) if times else None,
            "rg_ms": round(statistics.median(rg_times), 1), "rg_files": len(rg_files), "db_files": len(db_files),
            "missed_by_chunks": sorted(rg_files - db_files), "extra_in_chunks": sorted(db_files - rg_files),
        })
        r = results[-1]
        print(f"{pat[:40]:40} db={r['db_ms']}ms rg={r['rg_ms']}ms files rg={r['rg_files']} db={r['db_files']} "
              f"missed={len(r['missed_by_chunks'])} extra={len(r['extra_in_chunks'])}", flush=True)

    summary = {"chunks": int(totals[0]), "chunk_chars": int(totals[1]),
               "corpus_files": sum(1 for f in corpus.rglob("*") if f.is_file()),
               "median_db_ms": statistics.median(r["db_ms"] for r in results if r["db_ms"] is not None),
               "median_rg_ms": statistics.median(r["rg_ms"] for r in results),
               "patterns_with_missed_files": sum(1 for r in results if r["missed_by_chunks"]),
               "patterns_with_extra_files": sum(1 for r in results if r["extra_in_chunks"]),
               "results": results}
    Path(args.out).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k != "results"}))


if __name__ == "__main__":
    main()
