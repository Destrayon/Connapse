"""Builds the eval harness's enterprise suite (#662) from EnterpriseRAG-Bench (Onyx, MIT).

The source is huggingface.co/datasets/onyx-dot-app/EnterpriseRAG-Bench at a pinned revision:
about 512,000 generated documents of one fictional company (Slack, Gmail, Linear, Google Drive,
HubSpot, Fireflies, GitHub, Jira, Confluence) in one Parquet row group, and 500 questions.

Two datasets come out, both rewritten in 10,000-row groups so the harness can stream them:
  erb-50k   every gold document, each scored question's top 100 by BM25 over the FULL corpus
            (the hard distractors a small corpus would otherwise lack), and the rest of 50,000
            filled by a stable hash of the document ID. The pool follows what the questions ask
            about, so sources don't keep their full-corpus shares (Slack 34% here, 56% there);
  erb-full  the whole corpus.
Questions are copied unchanged; the adapter leaves out the ones with no gold documents.

BM25 is the dev suite's (Lucene's k1 1.2, b 0.75, english-stemmed, stop-word-free tokens) over
title + content, computed in one streaming pass that keeps postings for query terms only.

Usage:
    python eval/tools/build_enterprise_suite.py            # build both; pin them, or check existing pins
    python eval/tools/build_enterprise_suite.py --register # pin already-built files
    python eval/tools/build_enterprise_suite.py --repin    # build and replace existing pins

Outputs land in eval/.cache/datasets/<name>/<version>/ with build.json; eval/MANIFEST.json pins
their sha256 under the "enterprise-v1" and "enterprise-full" suites.
"""
import concurrent.futures as cf
import hashlib
import json
import math
import os
import sys
import time
from collections import Counter, defaultdict

import pyarrow as pa
import pyarrow.parquet as pq
import requests

sys.path.insert(0, os.path.dirname(__file__))
from build_dev_suite import tokens  # noqa: E402  (same tokenizer as the dev suite)

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
REVISION = "69916e31c68aa5963c00248fd7f0bc12d04fd235"
BASE = f"https://huggingface.co/datasets/onyx-dot-app/EnterpriseRAG-Bench/resolve/{REVISION}"
SOURCES = {
    "documents.parquet": ("data/documents/test.parquet", "6b0747bf160af9427b12101537d53056ac592ada9831c1a98ae01fa50a8d2a9f"),
    "questions.parquet": ("data/questions/test.parquet", "e25066f4eff3843dd0f3df0d1348113471e072e75007ffe390a0aa83f2a80af2"),
}
SAMPLE = 50_000
TOP_K = 100
ROW_GROUP = 10_000
K1, B = 1.2, 0.75
VERSION = REVISION[:12]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def source(name):
    rel, expected = SOURCES[name]
    directory = os.path.join(ROOT, "eval", ".cache", "sources", "enterprise-rag-bench", REVISION)
    os.makedirs(directory, exist_ok=True)
    path = os.path.join(directory, name)
    if not os.path.exists(path):
        print(f"downloading {rel}", flush=True)
        with requests.get(f"{BASE}/{rel}", stream=True, timeout=60) as r:
            r.raise_for_status()
            with open(path + ".part", "wb") as f:
                for block in r.iter_content(1 << 20):
                    f.write(block)
        os.replace(path + ".part", path)
    actual = sha256(path)
    if actual != expected:
        raise SystemExit(f"{path}: sha256 {actual}, expected {expected}")
    return path


def fraction(doc_id):
    """The C# adapter's and this script's shared stable [0, 1) per document."""
    return int.from_bytes(hashlib.sha256(doc_id.encode()).digest()[:8], "little") / 2**64


def _stats(args):
    texts, terms = args
    lengths, postings = [], []
    for text in texts:
        toks = tokens(text)
        lengths.append(len(toks))
        counts = Counter(t for t in toks if t in terms)
        postings.append(counts)
    return lengths, postings


def build():
    docs_path, questions_path = source("documents.parquet"), source("questions.parquet")
    questions = pq.read_table(questions_path).to_pylist()
    scored = [q for q in questions if q["expected_doc_ids"]]
    gold = {d for q in scored for d in q["expected_doc_ids"]}
    query_tokens = {q["question_id"]: tokens(q["question"]) for q in scored}
    terms = set(t for ts in query_tokens.values() for t in ts)
    print(f"{len(scored)} scored questions, {len(gold)} gold documents, {len(terms)} query terms", flush=True)

    # One streaming pass: document lengths and term frequencies for query terms only.
    table = pq.read_table(docs_path, columns=["doc_id", "source_type", "title", "content"])
    # Four IDs appear twice at this revision (three pair different sources, one is the same Jira
    # ticket twice); the first copy is kept so every ID names one document.
    seen, duplicates, mask = set(), [], []
    for i in table.column("doc_id").to_pylist():
        mask.append(i not in seen)
        if i in seen:
            duplicates.append(i)
        seen.add(i)
    table = table.filter(pa.array(mask))
    ids = table.column("doc_id").to_pylist()
    titles = table.column("title").to_pylist()
    contents = table.column("content").to_pylist()
    texts = [f"{t or ''}\n\n{c or ''}" for t, c in zip(titles, contents)]
    del titles, contents
    started = time.time()
    lengths, df, tf = [], Counter(), defaultdict(list)
    chunks = [texts[i:i + 5000] for i in range(0, len(texts), 5000)]
    with cf.ProcessPoolExecutor() as pool:
        offset = 0
        for chunk_lengths, chunk_postings in pool.map(_stats, [(c, terms) for c in chunks]):
            for i, counts in enumerate(chunk_postings):
                for term, n in counts.items():
                    tf[term].append((offset + i, n))
                    df[term] += 1
            lengths.extend(chunk_lengths)
            offset += len(chunk_lengths)
            print(f"  tokenised {offset}/{len(texts)} ({time.time() - started:.0f} s)", flush=True)
    del texts

    n_docs = len(lengths)
    avgdl = sum(lengths) / n_docs
    bm25_top = {}
    for qid, qtoks in query_tokens.items():
        scores = defaultdict(float)
        # Sorted terms fix the float accumulation order and (score, ID) breaks ties, so the pool
        # doesn't depend on Python's per-process string hashing and the pins reproduce.
        for term in sorted(set(qtoks)):
            idf = math.log(1 + (n_docs - df[term] + 0.5) / (df[term] + 0.5))
            for doc, n in tf.get(term, []):
                scores[doc] += idf * n * (K1 + 1) / (n + K1 * (1 - B + B * lengths[doc] / avgdl))
        bm25_top[qid] = [ids[d] for d, _ in sorted(scores.items(), key=lambda p: (-p[1], ids[p[0]]))[:TOP_K]]

    pooled = set().union(*bm25_top.values()) - gold
    rest = sorted((fraction(i), i) for i in ids if i not in gold and i not in pooled)
    fill = [i for _, i in rest[: max(0, SAMPLE - len(gold) - len(pooled))]]
    keep = gold | pooled | set(fill)
    gold_hits = sum(1 for q in scored for d in q["expected_doc_ids"] if d in bm25_top[q["question_id"]])

    outputs = {}
    for name, selected in (("erb-50k", keep), ("erb-full", None)):
        directory = os.path.join(ROOT, "eval", ".cache", "datasets", name, VERSION)
        os.makedirs(directory, exist_ok=True)
        subset = table if selected is None else table.filter(pa.compute.is_in(table.column("doc_id"), value_set=pa.array(sorted(selected))))
        pq.write_table(subset, os.path.join(directory, "documents.parquet"), row_group_size=ROW_GROUP)
        pq.write_table(pq.read_table(questions_path), os.path.join(directory, "questions.parquet"))
        info = {
            "revision": REVISION,
            "documents": subset.num_rows,
            "duplicates_dropped": sorted(duplicates),
            "by_source": dict(Counter(subset.column("source_type").to_pylist())),
            "files": {f: sha256(os.path.join(directory, f)) for f in ("documents.parquet", "questions.parquet")},
        }
        if selected is not None:
            info.update(gold=len(gold), bm25_pooled=len(pooled), hash_fill=len(fill), top_k=TOP_K,
                        bm25_gold_recall_at_100=round(gold_hits / sum(len(q["expected_doc_ids"]) for q in scored), 4))
        with open(os.path.join(directory, "build.json"), "w") as f:
            json.dump(info, f, indent=2)
        outputs[name] = info
        print(json.dumps({name: {k: v for k, v in info.items() if k != "files"}}), flush=True)
    return outputs


def register(repin):
    manifest_path = os.path.join(ROOT, "eval", "MANIFEST.json")
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    for name, suite in (("erb-50k", "enterprise-v1"), ("erb-full", "enterprise-full")):
        with open(os.path.join(ROOT, "eval", ".cache", "datasets", name, VERSION, "build.json")) as f:
            files = json.load(f)["files"]
        pinned = manifest["datasets"].get(name)
        if pinned and pinned["version"] == VERSION and not repin:
            expected = {f["name"]: f["sha256"] for f in pinned["files"]}
            if expected != files:
                raise SystemExit(f"{name}: the build does not match the pinned files; pass --repin to replace the pins")
            print(f"{name}: matches its pins")
            continue
        manifest["datasets"][name] = {
            "adapter": "enterprise-rag-bench",
            "version": VERSION,
            "tags": ["domain:enterprise", "source:enterprise-rag-bench"],
            "files": [{"name": f, "url": "build:eval/tools/build_enterprise_suite.py", "sha256": h} for f, h in files.items()],
        }
        manifest["suites"][suite] = [name]
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print("eval/MANIFEST.json up to date")


if __name__ == "__main__":
    if "--register" not in sys.argv:
        build()
    register("--repin" in sys.argv)
