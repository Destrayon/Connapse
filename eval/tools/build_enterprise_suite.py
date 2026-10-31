"""Builds the eval harness's enterprise suite (#662) from EnterpriseRAG-Bench (Onyx, MIT).

The source is huggingface.co/datasets/onyx-dot-app/EnterpriseRAG-Bench at a pinned revision:
about 512,000 generated documents of one fictional company (Slack, Gmail, Linear, Google Drive,
HubSpot, Fireflies, GitHub, Jira, Confluence) in one Parquet row group, and 500 questions.

Three datasets come out, all rewritten in 10,000-row groups so the harness can stream them:
  erb-50k   every gold document, each scored question's top 100 by BM25 over the FULL corpus
            (the hard distractors a small corpus would otherwise lack), and the rest of 50,000
            filled by a stable hash of the document ID. The pool follows what the questions ask
            about, so sources don't keep their full-corpus shares (Slack 34% here, 56% there);
  erb-10k   a subset of erb-50k for routine runs (CON-151): every gold document, each scored
            question's top K by the same BM25 with K the largest that keeps the set at 10,000 or
            fewer, and the rest of 10,000 filled from erb-50k's other documents by the same hash;
  erb-full  the whole corpus.
Questions are copied unchanged; the adapter leaves out the ones with no gold documents.

BM25 is the dev suite's (Lucene's k1 1.2, b 0.75, english-stemmed, stop-word-free tokens) over
title + content, computed in one streaming pass that keeps postings for query terms only.

The build streams the source twice in 5,000-row batches with two tokenising workers instead of
loading it whole with a worker per CPU (17.5 GB at 32 workers). The outputs keep the bytes of that
first build: Parquet pages break where Arrow chunks do, so each row group is written with the
chunks the whole read had (see _Cuts).

Usage:
    python eval/tools/build_enterprise_suite.py            # build all three; pin them, or check existing pins
    python eval/tools/build_enterprise_suite.py --register # pin already-built files
    python eval/tools/build_enterprise_suite.py --repin    # build and replace existing pins

Outputs land in eval/.cache/datasets/<name>/<version>/ with build.json; eval/MANIFEST.json pins
their sha256 under the "enterprise-v1", "enterprise-10k" and "enterprise-full" suites.
"""
import bisect
import concurrent.futures as cf
import hashlib
import json
import math
import os
import sys
import time
from array import array
from collections import Counter, defaultdict, deque
from itertools import groupby

import pyarrow as pa
import pyarrow.compute as pc
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
SMALL = 10_000
TOP_K = 100
ROW_GROUP = 10_000
K1, B = 1.2, 0.75
VERSION = REVISION[:12]
COLUMNS = ["doc_id", "source_type", "title", "content"]
BATCH = 5_000       # source rows per tokenising task and per write slice
WORKERS = 2
IN_FLIGHT = 4       # tasks submitted and not yet merged; bounds the texts held at once
SCAN_ROWS = 1 << 17                 # the batch size of the dataset scan behind pq.read_table
CHUNK_BYTES = 2**31 - 1             # where a string column read whole starts a new Arrow chunk


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


_terms = None


def _init(terms):
    global _terms
    _terms = terms


def _stats(texts):
    lengths, postings = [], []
    for text in texts:
        toks = tokens(text)
        lengths.append(len(toks))
        postings.append(Counter(t for t in toks if t in _terms))
    return lengths, postings


class _Cuts:
    """Source rows where pq.read_table's table started a new chunk: every SCAN_ROWS rows, and wherever a
    column's strings since its last chunk would pass CHUNK_BYTES (Arrow's ChunkedBinaryBuilder)."""

    def __init__(self):
        self.rows, self._bytes = set(), dict.fromkeys(COLUMNS, 0)

    def add(self, batch, offset):
        for name in COLUMNS:
            run = self._bytes[name]
            for i, n in enumerate(pc.binary_length(batch.column(name)).fill_null(0).to_pylist()):
                if run and run + n > CHUNK_BYTES:
                    self.rows.add(offset + i)
                    run = 0
                run += n
            self._bytes[name] = run

    def sorted(self, n_rows):
        return sorted(self.rows | set(range(SCAN_ROWS, n_rows, SCAN_ROWS)))


class _RowGroups:
    """Writes ROW_GROUP-row groups as they fill, each column chunked as the source's whole read chunked it."""

    def __init__(self, path, schema):
        self.writer, self.pending, self.rows = pq.ParquetWriter(path, schema), deque(), 0
        self.count, self.by_source = 0, Counter()

    def add(self, chunk, table):
        if table.num_rows:
            self.pending.append((chunk, table))
            self.rows += table.num_rows
            self.count += table.num_rows
            self.by_source.update(table.column("source_type").to_pylist())
        while self.rows >= ROW_GROUP:
            self._flush(ROW_GROUP)

    def _flush(self, n):
        taken, left = [], n
        while left:
            chunk, table = self.pending.popleft()
            if table.num_rows > left:
                self.pending.appendleft((chunk, table.slice(left)))
                table = table.slice(0, left)
            taken.append((chunk, table))
            left -= table.num_rows
        self.rows -= n
        parts = [pa.concat_tables([t for _, t in group]).combine_chunks() for _, group in groupby(taken, key=lambda p: p[0])]
        self.writer.write_table(pa.concat_tables(parts), row_group_size=ROW_GROUP)

    def close(self):
        if self.rows:
            self._flush(self.rows)
        self.writer.close()


def build():
    docs_path, questions_path = source("documents.parquet"), source("questions.parquet")
    questions = pq.read_table(questions_path).to_pylist()
    scored = [q for q in questions if q["expected_doc_ids"]]
    gold = {d for q in scored for d in q["expected_doc_ids"]}
    query_tokens = {q["question_id"]: tokens(q["question"]) for q in scored}
    terms = frozenset(t for ts in query_tokens.values() for t in ts)
    print(f"{len(scored)} scored questions, {len(gold)} gold documents, {len(terms)} query terms", flush=True)

    # Pass 1: document lengths and term frequencies for query terms only, a few batches in flight.
    # Four IDs appear twice at this revision (three pair different sources, one is the same Jira
    # ticket twice); the first copy is kept so every ID names one document.
    # The source is one row group, so an unbuffered read would hold content's whole 1.4 GB column chunk.
    source_file = pq.ParquetFile(docs_path, pre_buffer=False, buffer_size=1 << 20)
    n_source = source_file.metadata.num_rows
    seen, duplicates, mask, ids, cuts = set(), [], [], [], _Cuts()
    started = time.time()
    lengths, df, tf_doc, tf_n = array("i"), Counter(), defaultdict(lambda: array("i")), defaultdict(lambda: array("i"))
    with cf.ProcessPoolExecutor(WORKERS, initializer=_init, initargs=(terms,)) as pool:
        pending = deque()

        def merge():
            chunk_lengths, chunk_postings = pending.popleft().result()
            offset = len(lengths)
            for i, counts in enumerate(chunk_postings):
                for term, n in counts.items():
                    tf_doc[term].append(offset + i)
                    tf_n[term].append(n)
                    df[term] += 1
            lengths.extend(chunk_lengths)
            if len(lengths) // 50_000 != offset // 50_000:
                print(f"  tokenised {len(lengths)}/{n_source} ({time.time() - started:.0f} s)", flush=True)

        position = 0
        for batch in source_file.iter_batches(batch_size=BATCH, columns=COLUMNS):
            cuts.add(batch, position)
            position += batch.num_rows
            texts = []
            for doc_id, title, content in zip(*(batch.column(c).to_pylist() for c in ("doc_id", "title", "content"))):
                mask.append(doc_id not in seen)
                if doc_id in seen:
                    duplicates.append(doc_id)
                    continue
                seen.add(doc_id)
                ids.append(doc_id)
                texts.append(f"{title or ''}\n\n{content or ''}")
            pending.append(pool.submit(_stats, texts))
            if len(pending) >= IN_FLIGHT:
                merge()
        while pending:
            merge()
    del seen

    n_docs = len(lengths)
    avgdl = sum(lengths) / n_docs
    bm25_top = {}
    for qid, qtoks in query_tokens.items():
        scores = defaultdict(float)
        # Sorted terms fix the float accumulation order and (score, ID) breaks ties, so the pool
        # doesn't depend on Python's per-process string hashing and the pins reproduce.
        for term in sorted(set(qtoks)):
            idf = math.log(1 + (n_docs - df[term] + 0.5) / (df[term] + 0.5))
            for doc, n in zip(tf_doc.get(term, ()), tf_n.get(term, ())):
                scores[doc] += idf * n * (K1 + 1) / (n + K1 * (1 - B + B * lengths[doc] / avgdl))
        bm25_top[qid] = [ids[d] for d, _ in sorted(scores.items(), key=lambda p: (-p[1], ids[p[0]]))[:TOP_K]]
    del tf_doc, tf_n

    pooled = set().union(*bm25_top.values()) - gold
    rest = sorted((fraction(i), i) for i in ids if i not in gold and i not in pooled)
    fill = [i for _, i in rest[: max(0, SAMPLE - len(gold) - len(pooled))]]
    keep = gold | pooled | set(fill)
    gold_hits = sum(1 for q in scored for d in q["expected_doc_ids"] if d in bm25_top[q["question_id"]])
    del rest, ids

    # erb-10k: the largest per-question BM25 depth whose pool fits, then erb-50k's lowest hashes.
    small_k, small_pool = 0, set(gold)
    for k in range(1, TOP_K + 1):
        grown = small_pool | {top[k - 1] for top in bm25_top.values() if len(top) >= k}
        if len(grown) > SMALL:
            break
        small_k, small_pool = k, grown
    small_fill = sorted(keep - small_pool, key=lambda i: (fraction(i), i))[: SMALL - len(small_pool)]
    small = small_pool | set(small_fill)
    small_gold_hits = sum(1 for q in scored for d in q["expected_doc_ids"] if d in bm25_top[q["question_id"]][:small_k])

    # Pass 2: both datasets in one more read, sliced at the whole read's chunk boundaries.
    outputs, writers = {}, {}
    for name in ("erb-50k", "erb-10k", "erb-full"):
        directory = os.path.join(ROOT, "eval", ".cache", "datasets", name, VERSION)
        os.makedirs(directory, exist_ok=True)
        writers[name] = _RowGroups(os.path.join(directory, "documents.parquet"), source_file.schema_arrow)
    boundaries, position = cuts.sorted(n_source), 0
    value_sets = {"erb-50k": pa.array(sorted(keep)), "erb-10k": pa.array(sorted(small))}
    for batch in source_file.iter_batches(batch_size=BATCH, columns=COLUMNS):
        edges = [position] + boundaries[bisect.bisect_right(boundaries, position):bisect.bisect_left(boundaries, position + batch.num_rows)] + [position + batch.num_rows]
        for start, stop in zip(edges, edges[1:]):
            chunk = bisect.bisect_right(boundaries, start)
            table = pa.Table.from_batches([batch.slice(start - position, stop - start)]).filter(pa.array(mask[start:stop]))
            writers["erb-full"].add(chunk, table)
            for name, value_set in value_sets.items():
                writers[name].add(chunk, table.filter(pc.is_in(table.column("doc_id"), value_set=value_set)))
        position += batch.num_rows

    for name in ("erb-50k", "erb-10k", "erb-full"):
        directory = os.path.join(ROOT, "eval", ".cache", "datasets", name, VERSION)
        writers[name].close()
        pq.write_table(pq.read_table(questions_path), os.path.join(directory, "questions.parquet"))
        info = {
            "revision": REVISION,
            "documents": writers[name].count,
            "duplicates_dropped": sorted(duplicates),
            "by_source": dict(writers[name].by_source),
            "files": {f: sha256(os.path.join(directory, f)) for f in ("documents.parquet", "questions.parquet")},
        }
        gold_total = sum(len(q["expected_doc_ids"]) for q in scored)
        if name == "erb-50k":
            info.update(gold=len(gold), bm25_pooled=len(pooled), hash_fill=len(fill), top_k=TOP_K,
                        bm25_gold_recall_at_100=round(gold_hits / gold_total, 4))
        elif name == "erb-10k":
            info.update(gold=len(gold), bm25_pooled=len(small_pool - gold), hash_fill=len(small_fill), top_k=small_k,
                        bm25_gold_recall_at_top_k=round(small_gold_hits / gold_total, 4), subset_of="erb-50k")
        with open(os.path.join(directory, "build.json"), "w") as f:
            json.dump(info, f, indent=2)
        outputs[name] = info
        print(json.dumps({name: {k: v for k, v in info.items() if k != "files"}}), flush=True)
    return outputs


def register(repin):
    manifest_path = os.path.join(ROOT, "eval", "MANIFEST.json")
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    for name, suite in (("erb-50k", "enterprise-v1"), ("erb-10k", "enterprise-10k"), ("erb-full", "enterprise-full")):
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
