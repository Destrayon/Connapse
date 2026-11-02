"""CON-142: how much of each original file survives word for word in its stored chunks.

For every document in the container, counts the non-blank source lines (trimmed, 20+ characters) that appear
verbatim in at least one of its chunks. A line can be missing because the chunker dropped it or because it
rewrote it (for example the sentence splitter adding a space after every '.', which breaks 'a.b' in code).

    python chunk_coverage.py --corpus E:/tmp/con-142/corpus-small --out E:/tmp/con-142/chunk_coverage.json
"""
import argparse
import json
import re
import subprocess
from collections import defaultdict
from pathlib import Path

from shrink_corpus import corpus_path
from ingest import CODE_EXTENSIONS


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--corpus", required=True)
    p.add_argument("--pg-container", default="con142-postgres-1")
    p.add_argument("--container", default="con142-corpus")
    p.add_argument("--out", required=True)
    args = p.parse_args()

    sql = ("select d.path, c.content from chunks c join documents d on d.id = c.document_id "
           "join containers k on k.id = d.container_id where k.name = '" + args.container.replace("'", "''") + "';")
    raw = subprocess.run(["docker", "exec", "-i", args.pg_container, "psql", "-U", "connapse", "-d", "connapse",
                          "-At", "-F", "\x1f", "-R", "\x1e"], input=sql, capture_output=True, text=True,
                         encoding="utf-8", check=True).stdout
    chunks = defaultdict(list)
    for row in raw.split("\x1e"):
        if "\x1f" in row:
            path, content = row.split("\x1f", 1)
            chunks[corpus_path(path.strip())].append(content)

    per_file = []
    for rel, texts in sorted(chunks.items()):
        joined = "\n".join(texts)
        squashed = re.sub(r"\s+", "", joined)
        lines = [l.strip() for l in (Path(args.corpus) / rel).read_text(encoding="utf-8", errors="replace").splitlines()]
        lines = [l for l in lines if len(l) >= 20]
        found = sum(1 for l in lines if l in joined)
        # Ignoring all whitespace separates lines the chunker rewrote (still present) from lines it dropped.
        present = sum(1 for l in lines if re.sub(r"\s+", "", l) in squashed)
        per_file.append({"file": rel, "code": Path(rel).suffix in CODE_EXTENSIONS, "lines": len(lines),
                         "verbatim": found, "present_ignoring_whitespace": present})

    def share(rows, key="verbatim"):
        total = sum(r["lines"] for r in rows)
        return round(sum(r[key] for r in rows) / total, 3) if total else None

    code = [r for r in per_file if r["code"]]
    docs = [r for r in per_file if not r["code"]]
    summary = {"files": len(per_file), "verbatim_share_all": share(per_file), "verbatim_share_code": share(code),
               "verbatim_share_docs": share(docs),
               "present_share_code": share(code, "present_ignoring_whitespace"),
               "present_share_docs": share(docs, "present_ignoring_whitespace"),
               "files_fully_verbatim": sum(1 for r in per_file if r["verbatim"] == r["lines"]),
               "per_file": per_file}
    Path(args.out).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k != "per_file"}))


if __name__ == "__main__":
    main()
