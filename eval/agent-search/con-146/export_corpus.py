"""CON-146: writes erb-50k's documents to disk exactly as the eval harness uploaded them to Connapse.

Same folder, file name and text as `ConnapseSearchSystem.UploadFolder`, `UploadName` and `Compose` (a titled text
document goes in `/<row index, 7 digits>/<safe title>.txt` and starts with its title), so a path the file tools
return is the same path `search_knowledge` returns.

    python export_corpus.py --dataset E:/tmp/con-32/cache/datasets/erb-50k/69916e31c68a --out E:/tmp/con-146/corpus \
        [--check-paths db-paths.txt]
"""
import argparse
import os
from pathlib import Path

import pyarrow.parquet as pq


def safe_file_name(title):
    if title is None or not title.strip():
        return None
    name = "".join(" " if (ord(c) < 32 or 127 <= ord(c) < 160 or c in '/\\:*?"<>|') else c for c in title)
    cleaned = " ".join(p for p in name.split(" ") if p).strip(". ")
    if len(cleaned) > 200:
        cleaned = cleaned[:200].rstrip(". ")
    return cleaned or None


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--dataset", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--check-paths", help="file of Connapse document paths, one per line, to compare against")
    args = p.parse_args()

    out = Path(args.out)
    paths = []
    index = 0
    for batch in pq.ParquetFile(os.path.join(args.dataset, "documents.parquet")).iter_batches(columns=["title", "content"]):
        for row in batch.to_pylist():
            title = row["title"] if row["title"] and row["title"].strip() else None
            name = safe_file_name(title)
            rel = f"{index:07d}/{name}.txt" if name else f"{index:07d}.txt"
            text = f"{title}\n\n{row['content'] or ''}" if title else (row["content"] or "")
            target = out / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(text.encode("utf-8"))
            paths.append("/" + rel)
            index += 1
    print(f"wrote {index} documents to {out}")

    if args.check_paths:
        db = set(Path(args.check_paths).read_text(encoding="utf-8").splitlines())
        mine = set(paths)
        print(f"same paths: {len(db & mine)}, only in Connapse: {len(db - mine)}, only on disk: {len(mine - db)}")
        for path in sorted(db ^ mine)[:5]:
            print("  differs:", path)


if __name__ == "__main__":
    main()
