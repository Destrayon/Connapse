"""Builds the vector-index scale set (#571): real MS MARCO passage and query embeddings, grouped into
topically coherent containers of realistic sizes, for the eval harness's `vector-index --scale`.

Passages are sampled with a fixed seed from the full MS MARCO corpus, embedded with
nomic-embed-text-v1.5 through a local TEI container exactly as Connapse prepares them
("search_document: " / "search_query: " prefixes; TEI's tokenizer lowercases, as Connapse does for
Ollama), then grouped by nearest of 256 sampled topic centroids. Whole topic groups fill each
container in turn, so a container covers a few topics, as a real knowledge base does, instead of a
uniform sample of everything (which would make filtered search look easier than it is).

Usage (TEI running as in build_dev_suite.py):
    python eval/tools/build_scale_set.py [--passages 1800000] [--queries 200]

Output in eval/.cache/scale/<version>/: vectors.f32 (N x 768 float32, row order = passages.parquet),
passages.parquet (_id, container), queries.f32 + queries.parquet (_id, text), build.json.
"""
import argparse
import hashlib
import json
import os
import random
import sys
import time
from concurrent.futures import ThreadPoolExecutor

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq
import requests

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
MSMARCO = os.path.join(ROOT, "eval", ".cache", "beir", "mteb__msmarco", "49b09e5f3736c430f448d07150a5c2d425c24ce3")
TEI = os.environ.get("TEI_URL", "http://localhost:18080")
SEED = 571
DIMS = 768
# Container sizes to measure, then the rest of the passages in 1k containers.
SIZES = [1_000_000, 300_000] + [100_000] * 3 + [10_000] * 10 + [1_000] * 100


def embed(texts, prefix):
    session = requests.Session()

    def call(batch):
        for attempt in range(30):
            r = session.post(f"{TEI}/embed", json={"inputs": [prefix + t for t in batch], "truncate": True}, timeout=300)
            if r.status_code == 429:  # TEI's queue is full: back off, up to ~2 minutes in total
                time.sleep(min(0.25 * 2 ** attempt, 8))
                continue
            r.raise_for_status()
            return np.asarray(r.json(), dtype=np.float32)
        raise RuntimeError("TEI kept returning 429")

    batches = [texts[i:i + 200] for i in range(0, len(texts), 200)]
    out = np.empty((len(texts), DIMS), dtype=np.float32)
    done, t0 = 0, time.time()
    with ThreadPoolExecutor(2) as pool:
        for i, vecs in enumerate(pool.map(call, batches)):
            out[i * 200:i * 200 + len(vecs)] = vecs
            done += len(vecs)
            if i % 200 == 0:
                print(f"  embedded {done}/{len(texts)} ({done / max(time.time() - t0, 1):.0f}/s)", flush=True)
    return out / np.linalg.norm(out, axis=1, keepdims=True)


def sample_passages(n):
    # Reservoir-free: count lines once, then pick line numbers.
    with open(os.path.join(MSMARCO, "corpus.jsonl"), "rb") as f:
        total = sum(1 for _ in f)
    wanted = set(random.Random(SEED).sample(range(total), n))
    ids, texts = [], []
    with open(os.path.join(MSMARCO, "corpus.jsonl"), encoding="utf8") as f:
        for i, line in enumerate(f):
            if i in wanted:
                row = json.loads(line)
                ids.append(row["_id"])
                texts.append(((row.get("title") or "") + "\n\n" + row["text"]).strip())
    return ids, texts, total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--passages", type=int, default=sum(SIZES))
    ap.add_argument("--queries", type=int, default=200)
    args = ap.parse_args()
    version = f"msmarco-{args.passages}-s{SEED}"
    out = os.path.join(ROOT, "eval", ".cache", "scale", version)
    os.makedirs(out, exist_ok=True)
    t0 = time.time()

    ids, texts, total = sample_passages(args.passages)
    print(f"sampled {len(ids)} of {total} passages ({time.time() - t0:.0f}s)", flush=True)
    vectors = embed(texts, "search_document: ")

    # Topic groups: nearest of 256 sampled passages, then whole groups fill containers in turn.
    rng = np.random.default_rng(SEED)
    centroids = vectors[rng.choice(len(vectors), 256, replace=False)]
    topic = np.empty(len(vectors), dtype=np.int32)
    for i in range(0, len(vectors), 100_000):
        topic[i:i + 100_000] = np.argmax(vectors[i:i + 100_000] @ centroids.T, axis=1)
    order = np.concatenate([np.flatnonzero(topic == t) for t in rng.permutation(256)])
    sizes = [s for s in SIZES if s <= len(order)]
    container = np.empty(len(order), dtype=np.int32)
    start = 0
    for c, size in enumerate(sizes):
        container[order[start:start + size]] = c
        start += size
    for c, i in enumerate(range(start, len(order), 1_000), start=len(sizes)):
        container[order[i:i + 1_000]] = c

    vectors.tofile(os.path.join(out, "vectors.f32"))
    pq.write_table(pa.table({"_id": ids, "container": container}), os.path.join(out, "passages.parquet"))

    queries = [json.loads(l) for l in open(os.path.join(MSMARCO, "queries.jsonl"), encoding="utf8")]
    dev = {l.split("\t")[0] for l in open(os.path.join(MSMARCO, "qrels", "dev.tsv"), encoding="utf8")}
    dev_queries = [q for q in queries if q["_id"] in dev]
    picked = random.Random(SEED).sample(dev_queries, args.queries)
    qvec = embed([q["text"] for q in picked], "search_query: ")
    qvec.tofile(os.path.join(out, "queries.f32"))
    pq.write_table(pa.table({"_id": [q["_id"] for q in picked], "text": [q["text"] for q in picked]}),
                   os.path.join(out, "queries.parquet"))

    sizes_used = np.bincount(container).tolist()
    record = {"version": version, "seed": SEED, "passages": len(ids), "corpus": total, "queries": len(picked),
              "dims": DIMS, "containerSizes": sizes_used[:len(sizes)], "fillerContainers": len(sizes_used) - len(sizes),
              "model": "nomic-ai/nomic-embed-text-v1.5", "seconds": round(time.time() - t0)}
    json.dump(record, open(os.path.join(out, "build.json"), "w"), indent=1)
    print(json.dumps(record), flush=True)


if __name__ == "__main__":
    sys.exit(main())
