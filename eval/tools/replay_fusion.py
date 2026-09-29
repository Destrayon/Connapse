"""Replays Connapse's hybrid fusion offline from a run's captured candidates (#552).

A run with "captureCandidates": N in its config writes, per query, each side's top N chunks with
both scores. This script fuses them the way HybridSearchService does, for any pool size up to N and
any fusion setting, collapses chunks to documents the way the eval does, and scores nDCG@10 the way
the harness does (linear gain, ties by document ID descending, queries without a relevant document
left out). A sweep then takes seconds instead of one full run per setting.

Usage:
    python eval/tools/replay_fusion.py <run-dir> --check            # replay the run's own setting, compare
    python eval/tools/replay_fusion.py <run-dir> --sweep            # grid of fusion settings
"""
import argparse
import glob
import json
import math
import os
from collections import defaultdict

import numpy as np

F = np.float32
K = 10
TOP_K = 3 * K  # the eval asks Connapse for 3k chunks, then collapses them to k documents


def load(run):
    data = {}
    for path in sorted(glob.glob(os.path.join(run, "candidates", "*.jsonl"))):
        name = os.path.basename(path)[:-6]
        rel = defaultdict(dict)
        for line in open(os.path.join(run, "qrels", name + ".tsv"), encoding="utf8"):
            q, _, d, g = line.split()
            rel[q][d] = int(g)
        queries = [json.loads(line) for line in open(path, encoding="utf8") if line.strip()]
        data[name] = (queries, rel)
    return data


def pooled(side, other, own, cross, pool):
    """HybridSearchService.WithPoolScores: this side's top chunks, then the other side's it lacks, scored here."""
    out = [(c["chunkId"], c["docId"], F(c[own])) for c in side[:pool]]
    present = {c[0] for c in out}
    for c in other[:pool]:
        if c["chunkId"] not in present and c[cross] is not None:
            present.add(c["chunkId"])
            out.append((c["chunkId"], c["docId"], F(c[cross])))
    return out


def normalise(side, method, lower=None):
    if not side:
        return []
    scores = np.array([s for _, _, s in side], dtype=F)
    if method == "minmax":
        lo, hi = scores.min(), scores.max()
    elif method == "tmm":  # theoretical min-max: fixed lower bound, observed max
        lo, hi = F(lower), scores.max()
    else:
        raise ValueError(method)
    rng = F(hi - lo)
    if rng > 0:
        norm = np.clip((scores - lo) / rng, 0, 1).astype(F)
    else:
        norm = np.where(scores > 0, F(1), F(0)).astype(F)
    return [(c, d, n) for (c, d, _), n in zip(side, norm)]


def fuse_convex(q, pool, alpha, method="minmax", vlow=0.0, klow=0.0):
    vec = normalise(pooled(q["vector"], q["keyword"], "vectorScore", "vectorScore", pool), method, vlow)
    kw = normalise(pooled(q["keyword"], q["vector"], "keywordScore", "keywordScore", pool), method, klow)
    fused = {}
    for c, d, s in vec:
        fused[c] = [d, s, F(0)]
    for c, d, s in kw:
        if c in fused:
            fused[c][2] = s
        else:
            fused[c] = [d, F(0), s]
    a = F(alpha)
    items = [(c, d, F(a * v + (F(1) - a) * k)) for c, (d, v, k) in fused.items()]
    items.sort(key=lambda x: -x[2])  # stable, like LINQ OrderByDescending
    return items


def fuse_rrf(q, pool, k_rrf):
    score = {}
    for side in ("vector", "keyword"):
        for rank, c in enumerate(q[side][:pool], 1):
            d, s = score.get(c["chunkId"], (c["docId"], 0.0))
            score[c["chunkId"]] = (d, s + 1.0 / (k_rrf + rank))
    items = [(c, d, s) for c, (d, s) in score.items()]
    items.sort(key=lambda x: -x[2])
    return items


def collapse(items, top_k=TOP_K):
    seen, ranked = set(), []
    for _, d, s in items[:top_k]:
        if d and d not in seen:
            seen.add(d)
            ranked.append((d, float(s)))
            if len(ranked) == K:
                break
    return ranked


def ndcg10(ranked, rel):
    order = sorted(ranked, key=lambda x: (-x[1], _desc(x[0])))
    dcg = sum(rel.get(d, 0) / math.log2(i + 2) for i, (d, _) in enumerate(order[:K]))
    ideal = sorted((g for g in rel.values() if g > 0), reverse=True)[:K]
    idcg = sum(g / math.log2(i + 2) for i, g in enumerate(ideal))
    return dcg / idcg if idcg else None


def _desc(doc_id):
    # ties by document ID descending (ordinal), as RankingMetrics.Order
    return [-ord(ch) for ch in doc_id] + [1]


def ranked_docs(q, fuse):
    """The eval's documents for a query: 3k chunks collapsed to k documents; when that leaves fewer than
    k documents from a full page of chunks, the eval searches again with 10k chunks, which also widens
    each side's pool to 100 (ConnapseSearchSystem.SearchAsync, HybridSearchService pool = max(TopK, pool))."""
    items = fuse(q, None)
    ranked = collapse(items)
    if len(ranked) < K and len(items) >= TOP_K:
        ranked = collapse(fuse(q, 10 * K), 10 * K)
    return ranked


def evaluate(data, fuse):
    """fuse(q, top_k) -> fused chunks; top_k None means the first search, 100 the eval's retry.
    Per dataset: (mean nDCG@10, per-query values, their query IDs)."""
    per = {}
    for name, (queries, rel) in data.items():
        scored = [(q["queryId"], v) for q in queries
                  if (v := ndcg10(ranked_docs(q, fuse), rel.get(q["queryId"], {}))) is not None]
        vals = [v for _, v in scored]
        per[name] = (sum(vals) / len(vals), vals, [qid for qid, _ in scored])
    return per


def capture_width(data):
    """How many chunks per side the run captured (the widest list seen)."""
    return max(len(q[side]) for queries, _ in data.values() for q in queries for side in ("vector", "keyword"))


def summary(per):
    means = [m for m, *_ in per.values()]
    return sum(means) / len(means), min(means)


def convex(pool, alpha, method="minmax"):
    # the eval's retry asks for 100 chunks, and HybridSearchService pools max(TopK, pool) per side
    return lambda q, top_k: fuse_convex(q, pool if top_k is None else max(top_k, pool), alpha, method)


def rrf(pool, k_rrf):
    return lambda q, top_k: fuse_rrf(q, pool if top_k is None else max(top_k, pool), k_rrf)


def check(run, data, tolerance=0.005):
    """Replays the run's own fusion setting and fails when any dataset's nDCG@10 differs by more than tolerance."""
    manifest = json.load(open(os.path.join(run, "manifest.json"), encoding="utf8"))
    described = manifest["systemDescription"]
    method = manifest.get("settings", {}).get("Knowledge:Search:FusionMethod", "ConvexCombination")
    if method.lower() != "convexcombination":
        raise SystemExit(f"replay covers ConvexCombination only; this run used {method}")
    alpha, pool = float(described["search.fusionAlpha"]), int(described["search.hybridCandidatePool"])
    per = evaluate(data, convex(pool, alpha))
    report = json.load(open(os.path.join(run, "report.json"), encoding="utf8"))
    actual = {d["name"]: d for d in report["datasets"] if not d.get("invalid")}
    worst = 0.0
    print(f"replaying alpha {alpha}, pool {pool}")
    for name, (mean, vals, qids) in per.items():
        # the run's mean over the same queries the replay scored: a failed capture drops a query from both
        recorded = actual[name]["perQuery"]
        run_mean = sum(recorded[q]["nDCG@10"] for q in qids) / len(qids)
        missing = len(recorded) - len(qids)
        worst = max(worst, abs(mean - run_mean))
        print(f"{name:14} replay {mean:.4f}  run {run_mean:.4f}" + (f"  ({missing} queries not captured, left out of both)" if missing else ""))
    print(f"{'portfolio':14} replay {summary(per)[0]:.4f}")
    if worst > tolerance:
        raise SystemExit(f"replay differs from the run by {worst:.4f} nDCG@10 (tolerance {tolerance})")


def sweep(data):
    rows = []
    width = capture_width(data)
    pools = [p for p in (30, 50, 100) if p <= width]
    if len(pools) < 3:
        print(f"capture holds {width} chunks per side: sweeping pools {pools} only")
    for pool in pools:
        for alpha in (0.3, 0.4, 0.5, 0.6, 0.7, 0.75, 0.8, 0.9, 1.0):
            rows.append((f"minmax p{pool} a{alpha}", evaluate(data, convex(pool, alpha))))
            rows.append((f"tmm    p{pool} a{alpha}", evaluate(data, convex(pool, alpha, "tmm"))))
        for k in (10, 60):
            rows.append((f"rrf    p{pool} k{k}", evaluate(data, rrf(pool, k))))
    names = list(data)
    print("setting".ljust(22) + "macro  worst " + " ".join(n.replace("dev-", "")[:8].rjust(8) for n in names))
    for label, per in sorted(rows, key=lambda r: -summary(r[1])[0]):
        macro, worst = summary(per)
        print(f"{label:22}{macro:.4f} {worst:.4f} " + " ".join(f"{per[n][0]:8.4f}" for n in names))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--sweep", action="store_true")
    args = ap.parse_args()
    loaded = load(args.run)
    if args.check:
        check(args.run, loaded)
    if args.sweep:
        sweep(loaded)
