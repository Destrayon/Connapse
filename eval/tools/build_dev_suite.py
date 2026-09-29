"""Builds the eval harness's multi-domain dev suite (#552) from BEIR dev splits.

For each dataset: sample up to 100 dev (or train) queries with a fixed seed, drop any that are
NanoBEIR test queries (by ID or normalised text), and pool a small corpus the way NanoBEIR's was
pooled: every judged document, plus each query's top 50 by BM25 and top 50 by dense similarity
over the FULL corpus. Output is NanoBEIR-shaped Parquet (corpus/queries/qrels), so the harness
loads it with its existing beir-parquet adapter.

BM25 is Lucene's (k1 1.2, b 0.75) over english-stemmed, stop-word-free tokens, computed in two
streaming passes so multi-million-document corpora never sit in memory. Dense similarity uses
nomic-embed-text-v1.5 (Connapse's default embedder) served by a local TEI container.

Usage:
    docker run -d --name tei-devsuite --gpus all -p 18080:80 \
        ghcr.io/huggingface/text-embeddings-inference:120-1.9 \
        --model-id nomic-ai/nomic-embed-text-v1.5 --max-client-batch-size 512
    python eval/tools/build_dev_suite.py [dataset ...]              # build, then pin in MANIFEST.json
    python eval/tools/build_dev_suite.py --register [dataset ...]   # pin already-built files

Outputs land where the harness looks for them, eval/.cache/datasets/<name>/<version>/, with
build.json (counts, sha256 of every file); eval/MANIFEST.json pins those hashes under the "dev"
suite, and its "build:" URLs make the harness point here when the files are missing.
Rebuilding on another machine may pick slightly different dense neighbours (GPU arithmetic), so
the manifest pins the sha256 of the files built here.
"""
import concurrent.futures as cf
import hashlib
import heapq
import json
import math
import multiprocessing as mp
import os
import random
import re
import sys
import time
from collections import Counter, deque
from dataclasses import dataclass

import numpy as np
import pyarrow as pa
import pyarrow.parquet as pq
import requests
import Stemmer

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CACHE = os.path.join(ROOT, "eval", ".cache")
OUT = os.path.join(CACHE, "datasets")
TEI = os.environ.get("TEI_URL", "http://localhost:18080")

SEED = 552
QUERIES_PER_DATASET = 100
POOL_PER_SIDE = 50
K1, B = 1.2, 0.75
MAX_DOC_CHARS = 2000  # ~400 tokens: bounds embedding cost on long Wikipedia intros


@dataclass(frozen=True)
class Source:
    name: str          # dev-suite dataset name
    repo: str          # Hugging Face dataset repo (BEIR layout)
    revision: str
    qrels: str         # which split's judgments to sample queries from
    nano: str | None   # NanoBEIR test dataset whose queries must be excluded


SOURCES = [
    Source("dev-msmarco", "mteb/msmarco", "49b09e5f3736c430f448d07150a5c2d425c24ce3", "dev", "nanobeir-msmarco"),
    Source("dev-hotpotqa", "mteb/hotpotqa", "60b39fafe9b0b81ce6be80c295de49ace5b7a14e", "dev", "nanobeir-hotpotqa"),
    Source("dev-fever", "mteb/fever", "493f9624bb2900b794705fd2209319685bda7ed2", "dev", "nanobeir-fever"),
    Source("dev-quora", "mteb/quora", "7f060c1bdc9b3d5161eecc05aa59921bca4dd185", "dev", "nanobeir-quora"),
    Source("dev-fiqa", "mteb/fiqa", "5e59eeb3a7df6b85882112b747008547c21587ea", "dev", "nanobeir-fiqa"),
    Source("dev-nfcorpus", "mteb/nfcorpus", "52ac3f19d3449632d9f00aab0ad34a110fc03816", "dev", "nanobeir-nfcorpus"),
    Source("dev-dbpedia", "mteb/dbpedia", "7d8780d8ef0d0a55d13fa797fe39224ec806592c", "dev", "nanobeir-dbpedia"),
    Source("dev-scifact", "mteb/scifact", "cf10ab6856b15b0e670ef8ae5dae4e266c12d035", "train", "nanobeir-scifact"),
]

# English stop words, as in PostgreSQL's english config (snowball list).
STOP = set("""i me my myself we our ours ourselves you your yours yourself yourselves he him his himself she
her hers herself it its itself they them their theirs themselves what which who whom this that these
those am is are was were be been being have has had having do does did doing a an the and but if or
because as until while of at by for with about against between into through during before after above
below to from up down in out on off over under again further then once here there when where why how
all any both each few more most other some such no nor not only own same so than too very s t can will
just don should now""".split())
TOKEN = re.compile(r"[a-z0-9]+")
_stemmer = None


def tokens(text):
    global _stemmer
    if _stemmer is None:
        _stemmer = Stemmer.Stemmer("english")
    return _stemmer.stemWords([t for t in TOKEN.findall(text.lower()) if t not in STOP])


def download(src, rel):
    path = os.path.join(CACHE, "beir", src.repo.replace("/", "__"), src.revision, rel)
    if os.path.exists(path):
        return path
    os.makedirs(os.path.dirname(path), exist_ok=True)
    url = f"https://huggingface.co/datasets/{src.repo}/resolve/{src.revision}/{rel}"
    print(f"  downloading {url}", flush=True)
    with requests.get(url, stream=True, timeout=60) as r:
        r.raise_for_status()
        with open(path + ".part", "wb") as f:
            for chunk in r.iter_content(1 << 20):
                f.write(chunk)
    os.replace(path + ".part", path)
    return path


def read_jsonl(path):
    with open(path, encoding="utf8") as f:
        for line in f:
            if line.strip():
                yield json.loads(line)


def doc_text(row):
    title = (row.get("title") or "").strip()
    return f"{title} {row.get('text') or ''}".strip() if title else (row.get("text") or "")


def nano_exclusions(nano):
    """IDs and normalised texts of a NanoBEIR test set's queries, at the revision eval/MANIFEST.json
    pins. Fetched (and sha256-checked) when the harness hasn't downloaded it yet, so a fresh clone
    can build the dev suite; a mismatch stops the build rather than excluding the wrong queries."""
    ids, texts = set(), set()
    if nano is None:
        return ids, texts
    manifest = json.load(open(os.path.join(ROOT, "eval", "MANIFEST.json"), encoding="utf8"))
    entry = manifest["datasets"][nano]
    spec = next(f for f in entry["files"] if f["name"] == "queries.parquet")
    path = os.path.join(CACHE, "datasets", nano, entry["version"], "queries.parquet")
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        print(f"  downloading {nano} test queries (to exclude them)", flush=True)
        r = requests.get(spec["url"], timeout=120)
        r.raise_for_status()
        with open(path + ".part", "wb") as f:
            f.write(r.content)
        os.replace(path + ".part", path)
    actual = hashlib.sha256(open(path, "rb").read()).hexdigest()
    if actual != spec["sha256"]:
        raise SystemExit(f"{path}: sha256 {actual} does not match eval/MANIFEST.json ({spec['sha256']})")
    table = pq.read_table(path).to_pydict()
    ids.update(table["_id"])
    texts.update(" ".join(t.lower().split()) for t in table["text"])
    return ids, texts


# ---- pass 1: document frequencies and lengths (parallel) -------------------------------------

def _stats_chunk(lines):
    df, lengths = Counter(), []
    for line in lines:
        toks = tokens(doc_text(json.loads(line)))
        lengths.append(len(toks))
        df.update(set(toks))
    return df, lengths


def _line_chunks(path, size=20000):
    chunk = []
    with open(path, encoding="utf8") as f:
        for line in f:
            if line.strip():
                chunk.append(line)
                if len(chunk) == size:
                    yield chunk
                    chunk = []
    if chunk:
        yield chunk


# ---- pass 2: BM25 top-k for the sampled queries (parallel) ------------------------------------

_Q = None  # per-worker: (query ids, per-query {term: idf}, avgdl, lengths offset map)


def _init_scoring(query_terms, avgdl):
    global _Q
    _Q = (query_terms, avgdl)


def _score_chunk(args):
    lines, _ = args
    query_terms, avgdl = _Q
    vocab = set().union(*[set(q) for q in query_terms.values()])
    best = {qid: [] for qid in query_terms}
    for line in lines:
        row = json.loads(line)
        toks = tokens(doc_text(row))
        present = Counter(t for t in toks if t in vocab)
        if not present:
            continue
        norm = K1 * (1 - B + B * len(toks) / avgdl)
        for qid, weights in query_terms.items():
            s = 0.0
            for term, w in weights.items():
                f = present.get(term)
                if f:
                    s += w * f / (f + norm)
            if s > 0:
                heap = best[qid]
                if len(heap) < POOL_PER_SIDE:
                    heapq.heappush(heap, (s, row["_id"]))
                elif s > heap[0][0]:
                    heapq.heapreplace(heap, (s, row["_id"]))
    return best


def embed(texts, prefix):
    out = []
    for i in range(0, len(texts), 256):
        batch = [prefix + t[:MAX_DOC_CHARS] for t in texts[i:i + 256]]
        for attempt in range(50):
            r = requests.post(f"{TEI}/embed", json={"inputs": batch, "truncate": True}, timeout=300)
            if r.status_code == 429:
                time.sleep(0.2 * (attempt + 1))
                continue
            r.raise_for_status()
            out.extend(r.json())
            break
        else:
            raise RuntimeError("TEI kept returning 429")
    return np.asarray(out, dtype=np.float32)


def build(src):
    t0 = time.time()
    print(f"== {src.name} ({src.repo}@{src.revision[:7]}, {src.qrels})", flush=True)
    corpus_path = download(src, "corpus.jsonl")
    queries_path = download(src, "queries.jsonl")
    qrels_path = download(src, f"qrels/{src.qrels}.tsv")

    # Sample queries: judged relevant, not a NanoBEIR test query by ID or text.
    qrels = {}
    with open(qrels_path, encoding="utf8") as f:
        next(f)
        for line in f:
            q, d, g = line.rstrip("\n").split("\t")
            qrels.setdefault(q, {})[d] = int(float(g))
    excluded_ids, excluded_texts = nano_exclusions(src.nano)
    texts = {r["_id"]: r["text"] for r in read_jsonl(queries_path) if r["_id"] in qrels}
    seen_texts = set()
    candidates = []
    for qid in sorted(texts):
        norm = " ".join(texts[qid].lower().split())
        if qid in excluded_ids or norm in excluded_texts or norm in seen_texts:
            continue
        if not any(g > 0 for g in qrels[qid].values()) or not tokens(texts[qid]):
            continue
        seen_texts.add(norm)
        candidates.append(qid)
    rng = random.Random(f"{SEED}:{src.name}")
    sampled = sorted(rng.sample(candidates, min(QUERIES_PER_DATASET, len(candidates))))
    print(f"  queries: {len(sampled)} sampled of {len(candidates)} eligible", flush=True)

    # Pass 1: df and lengths.
    # Pool.imap has no backpressure and would queue the whole corpus; keep a bounded window.
    df, n, total_len = Counter(), 0, 0
    workers = max(1, os.cpu_count() - 4)
    with mp.Pool(workers) as pool:
        inflight = deque()
        def collect(result):
            nonlocal n, total_len
            chunk_df, lengths = result
            df.update(chunk_df)
            n += len(lengths)
            total_len += sum(lengths)
        for chunk in _line_chunks(corpus_path):
            inflight.append(pool.apply_async(_stats_chunk, (chunk,)))
            while len(inflight) > 2 * workers:
                collect(inflight.popleft().get())
        while inflight:
            collect(inflight.popleft().get())
    avgdl = total_len / n
    print(f"  pass 1: {n} documents, avgdl {avgdl:.1f} ({time.time() - t0:.0f}s)", flush=True)

    query_terms = {}
    for qid in sampled:
        weights = Counter()
        for term in tokens(texts[qid]):
            if df.get(term):
                weights[term] += math.log(1 + (n - df[term] + 0.5) / (df[term] + 0.5))
        query_terms[qid] = dict(weights)
    del df

    # Pass 2: BM25 top-k (worker processes) while the dense side streams through TEI.
    qvec = embed([texts[q] for q in sampled], "search_query: ")
    dense = {qid: [] for qid in sampled}
    bm25 = {qid: [] for qid in sampled}

    def merge(target, part):
        for qid, heap in part.items():
            for item in heap:
                h = target[qid]
                if len(h) < POOL_PER_SIDE:
                    heapq.heappush(h, item)
                elif item[0] > h[0][0]:
                    heapq.heapreplace(h, item)

    workers = max(1, os.cpu_count() - 6)
    with mp.Pool(workers, initializer=_init_scoring, initargs=(query_terms, avgdl)) as pool,             cf.ThreadPoolExecutor(3) as gpu:
        bm25_inflight, dense_inflight = deque(), deque()
        for chunk in _line_chunks(corpus_path):
            rows = [json.loads(line) for line in chunk]
            bm25_inflight.append(pool.apply_async(_score_chunk, ((chunk, None),)))
            dense_inflight.append(([r["_id"] for r in rows], gpu.submit(embed, [doc_text(r) for r in rows], "search_document: ")))
            while len(bm25_inflight) > 2 * workers:
                merge(bm25, bm25_inflight.popleft().get())
            while len(dense_inflight) > 4:
                ids, fut = dense_inflight.popleft()
                _merge_dense(dense, sampled, qvec, ids, fut.result())
        while bm25_inflight:
            merge(bm25, bm25_inflight.popleft().get())
        while dense_inflight:
            ids, fut = dense_inflight.popleft()
            _merge_dense(dense, sampled, qvec, ids, fut.result())
    print(f"  pass 2: BM25 + dense pooling done ({time.time() - t0:.0f}s)", flush=True)

    # Pool: judged documents plus both sides' top-k.
    pool_ids = set()
    for qid in sampled:
        pool_ids.update(qrels[qid])
        pool_ids.update(d for _, d in bm25[qid])
        pool_ids.update(d for _, d in dense[qid])

    out = os.path.join(OUT, src.name, version(src))
    os.makedirs(out, exist_ok=True)
    corpus_rows = {"_id": [], "title": [], "text": []}
    for row in read_jsonl(corpus_path):
        if row["_id"] in pool_ids:
            corpus_rows["_id"].append(row["_id"])
            corpus_rows["title"].append(row.get("title") or "")
            corpus_rows["text"].append(row.get("text") or "")
    in_corpus = set(corpus_rows["_id"])
    qrel_rows = {"query-id": [], "corpus-id": [], "score": []}
    for qid in sampled:
        for d, g in sorted(qrels[qid].items()):
            if d in in_corpus:
                qrel_rows["query-id"].append(qid)
                qrel_rows["corpus-id"].append(d)
                qrel_rows["score"].append(g)
    pq.write_table(pa.table(corpus_rows), os.path.join(out, "corpus.parquet"))
    pq.write_table(pa.table({"_id": sampled, "text": [texts[q] for q in sampled]}), os.path.join(out, "queries.parquet"))
    pq.write_table(pa.table(qrel_rows), os.path.join(out, "qrels.parquet"))

    record = {
        "name": src.name, "version": version(src), "repo": src.repo, "revision": src.revision, "split": src.qrels,
        "seed": SEED, "queries": len(sampled), "eligibleQueries": len(candidates),
        "fullCorpus": n, "pooledCorpus": len(in_corpus), "judgments": len(qrel_rows["query-id"]),
        "poolPerSide": POOL_PER_SIDE, "denseModel": "nomic-ai/nomic-embed-text-v1.5",
        "files": {f: hashlib.sha256(open(os.path.join(out, f), "rb").read()).hexdigest()
                  for f in ("corpus.parquet", "queries.parquet", "qrels.parquet")},
        "seconds": round(time.time() - t0),
    }
    json.dump(record, open(os.path.join(out, "build.json"), "w"), indent=1)
    print(f"  wrote {out}: {len(in_corpus)} documents, {len(sampled)} queries ({record['seconds']}s)", flush=True)


def register(src):
    """Pins a built dataset in eval/MANIFEST.json under the "dev" suite (tags copied from its
    NanoBEIR sibling), so the manifest's hashes are always the ones this build produced."""
    record = json.load(open(os.path.join(OUT, src.name, version(src), "build.json")))
    path = os.path.join(ROOT, "eval", "MANIFEST.json")
    manifest = json.load(open(path, encoding="utf8"))
    builder = "build:eval/tools/build_dev_suite.py"
    manifest["datasets"][src.name] = {
        "adapter": "beir-parquet",
        "version": version(src),
        "tags": manifest["datasets"][src.nano]["tags"],
        "files": [{"name": f, "url": builder, "sha256": record["files"][f]}
                  for f in ("corpus.parquet", "queries.parquet", "qrels.parquet")],
    }
    order = [s.name for s in SOURCES]
    dev = set(manifest["suites"].get("dev", [])) | {src.name}
    manifest["suites"]["dev"] = [n for n in order if n in dev]
    with open(path, "w", encoding="utf8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print(f"  registered {src.name} in eval/MANIFEST.json", flush=True)


def version(src):
    """The harness's dataset version: upstream revision plus the sampling seed."""
    return f"{src.revision[:12]}-s{SEED}"


def _merge_dense(dense, sampled, qvec, ids, dvec):
    scores = qvec @ dvec.T  # TEI returns normalised vectors: dot product = cosine
    k = min(POOL_PER_SIDE, len(ids))
    for qi, qid in enumerate(sampled):
        top = np.argpartition(-scores[qi], k - 1)[:k]
        heap = dense[qid]
        for j in top:
            item = (float(scores[qi, j]), ids[j])
            if len(heap) < POOL_PER_SIDE:
                heapq.heappush(heap, item)
            elif item[0] > heap[0][0]:
                heapq.heapreplace(heap, item)


if __name__ == "__main__":
    register_only = "--register" in sys.argv
    wanted = set(sys.argv[1:]) - {"--register"}
    for source in SOURCES:
        if not wanted or source.name in wanted:
            if not register_only:
                build(source)
            register(source)
