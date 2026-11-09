#!/usr/bin/env python3
"""Numbers for the CON-278 bars in eval/pass-bars.md, from the `scores` command's JSON lines.

    python eval/tools/score_calibration.py <run.jsonl> [--baseline <baseline.jsonl>]

Bar 1: foreign queries' top-1 score (median, share at 0.8 or more).
Bar 2: pooled AUROC of the score over in-domain top-10 hits of judged queries, relevant (grade > 0) against
       not; with a baseline, the difference and its 95% bootstrap interval (queries resampled, 1,000 draws,
       seed 278).
Bar 3: no-match flag rate on foreign queries and on in-domain queries whose top-1 hit is relevant.
Bar 4: with a baseline, in-domain queries whose top-10 chunk list differs.
Bar 5: p95 search time over in-domain queries.
Standard library only.
"""
import argparse
import json
import math
import random
import statistics
import sys


def load(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def key(q):
    return (q["dataset"], q["queryId"], q["kind"])


def top1(q):
    return q["hits"][0]["score"] if q["hits"] else 0.0


def p95(values):
    values = sorted(values)
    return values[max(0, math.ceil(0.95 * len(values)) - 1)] if values else float("nan")


def auroc(pairs):
    """Mann-Whitney AUROC over (score, relevant) pairs, ties counted half."""
    pos = sum(1 for _, r in pairs if r)
    neg = len(pairs) - pos
    if pos == 0 or neg == 0:
        return float("nan")
    ordered = sorted(pairs, key=lambda p: p[0])
    rank_sum = 0.0
    i = 0
    while i < len(ordered):
        j = i
        while j < len(ordered) and ordered[j][0] == ordered[i][0]:
            j += 1
        mid = (i + 1 + j) / 2  # average 1-based rank of the tie group
        rank_sum += mid * sum(1 for p in ordered[i:j] if p[1])
        i = j
    return (rank_sum - pos * (pos + 1) / 2) / (pos * neg)


def judged_in(rows):
    return {key(q): [(h["score"], h["grade"] > 0) for h in q["hits"]]
            for q in rows if q["kind"] == "in" and q["judged"]}


def summary(rows):
    foreign = [q for q in rows if q["kind"] == "foreign"]
    inside = [q for q in rows if q["kind"] == "in"]
    f_top = [top1(q) for q in foreign]
    relevant_top = [q for q in inside if q["judged"] and q["hits"] and q["hits"][0]["grade"] > 0]
    pooled = [p for hits in judged_in(rows).values() for p in hits]
    return {
        "queries": {"in": len(inside), "foreign": len(foreign)},
        "bar1": {"foreign_top1_median": statistics.median(f_top) if f_top else float("nan"),
                 "foreign_top1_share_ge_0.8": sum(s >= 0.8 for s in f_top) / len(f_top) if f_top else float("nan"),
                 "in_top1_median": statistics.median([top1(q) for q in inside]) if inside else float("nan")},
        "bar2": {"auroc": auroc(pooled), "hits": len(pooled)},
        "bar3": {"no_match_foreign": sum(q["noGoodMatch"] for q in foreign) / len(foreign) if foreign else float("nan"),
                 "no_match_in_relevant_top1": sum(q["noGoodMatch"] for q in relevant_top) / len(relevant_top)
                 if relevant_top else float("nan"),
                 "no_match_in_all": sum(q["noGoodMatch"] for q in inside) / len(inside) if inside else float("nan")},
        "bar5": {"p95_ms": p95([q["durationMs"] for q in inside]),
                 "median_ms": statistics.median([q["durationMs"] for q in inside]) if inside else float("nan")},
    }


def compare(run, base, draws=1000, seed=278):
    a, b = judged_in(run), judged_in(base)
    shared = sorted(set(a) & set(b))
    diff = auroc([p for k in shared for p in a[k]]) - auroc([p for k in shared for p in b[k]])
    rng = random.Random(seed)
    boot = []
    for _ in range(draws):
        sample = [shared[rng.randrange(len(shared))] for _ in shared]
        d = auroc([p for k in sample for p in a[k]]) - auroc([p for k in sample for p in b[k]])
        if not math.isnan(d):
            boot.append(d)
    boot.sort()
    lo, hi = boot[int(0.025 * len(boot))], boot[min(len(boot) - 1, int(0.975 * len(boot)))]

    ra = {key(q): [h["chunkId"] for h in q["hits"]] for q in run if q["kind"] == "in"}
    rb = {key(q): [h["chunkId"] for h in q["hits"]] for q in base if q["kind"] == "in"}
    common = set(ra) & set(rb)
    changed = sorted(k for k in common if ra[k] != rb[k])
    return {"bar2_auroc_diff": diff, "bar2_ci95": [lo, hi], "bar2_queries": len(shared),
            "bar4_in_queries_compared": len(common), "bar4_rankings_changed": len(changed),
            "bar4_missing": len(set(ra) ^ set(rb)), "bar4_examples": [list(k) for k in changed[:5]]}


def per_dataset(rows):
    out = {}
    for name in sorted({q["dataset"] for q in rows}):
        sub = [q for q in rows if q["dataset"] == name]
        s = summary(sub)
        out[name] = {"foreign_top1_median": round(s["bar1"]["foreign_top1_median"], 3),
                     "in_top1_median": round(s["bar1"]["in_top1_median"], 3),
                     "auroc": round(s["bar2"]["auroc"], 3),
                     "no_match_foreign": round(s["bar3"]["no_match_foreign"], 3),
                     "no_match_in_relevant_top1": round(s["bar3"]["no_match_in_relevant_top1"], 3)
                     if not math.isnan(s["bar3"]["no_match_in_relevant_top1"]) else None,
                     "p95_ms": round(s["bar5"]["p95_ms"])}
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("run")
    parser.add_argument("--baseline")
    parser.add_argument("--per-dataset", action="store_true")
    args = parser.parse_args()
    run = load(args.run)
    result = {"run": summary(run)}
    if args.baseline:
        base = load(args.baseline)
        result["baseline"] = summary(base)
        result["compare"] = compare(run, base)
    if args.per_dataset:
        result["per_dataset"] = per_dataset(run)
    json.dump(result, sys.stdout, indent=2)
    print()


if __name__ == "__main__":
    main()
