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


def collapse(items):
    seen, ranked = set(), []
    for _, d, s in items[:TOP_K]:
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


def evaluate(data, fuse):
    per = {}
    for name, (queries, rel) in data.items():
        vals = [v for q in queries if (v := ndcg10(collapse(fuse(q)), rel.get(q["queryId"], {}))) is not None]
        per[name] = (sum(vals) / len(vals), vals)
    return per


def summary(per):
    means = [m for m, _ in per.values()]
    return sum(means) / len(means), min(means)


def check(run, data):
    per = evaluate(data, lambda q: fuse_convex(q, 30, 0.3))
    report = json.load(open(os.path.join(run, "report.json"), encoding="utf8"))
    actual = {d["name"]: d["means"]["nDCG@10"] for d in report["datasets"] if not d.get("invalid")}
    for name, (mean, _) in per.items():
        print(f"{name:14} replay {mean:.4f}  run {actual.get(name, float('nan')):.4f}")
    print(f"{'portfolio':14} replay {summary(per)[0]:.4f}")


def sweep(data):
    rows = []
    for pool in (30, 50, 100):
        for alpha in (0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0):
            rows.append((f"minmax p{pool} a{alpha}", evaluate(data, lambda q, p=pool, a=alpha: fuse_convex(q, p, a))))
            rows.append((f"tmm    p{pool} a{alpha}", evaluate(data, lambda q, p=pool, a=alpha: fuse_convex(q, p, a, "tmm"))))
        for k in (10, 60):
            rows.append((f"rrf    p{pool} k{k}", evaluate(data, lambda q, p=pool, kk=k: fuse_rrf(q, p, kk))))
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
