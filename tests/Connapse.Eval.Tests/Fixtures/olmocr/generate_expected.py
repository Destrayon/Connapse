# Regenerates expected.json for the olmOCR-bench check ports in tests/Connapse.Eval/Checks.
#   pip install rapidfuzz==3.14.6 fuzzysearch==0.8.1 beautifulsoup4 tqdm ; python generate_expected.py
# Downloads olmocr at the pinned commit and olmOCR-bench test files at the pinned revision, then
# records what the reference code returns on seeded samples plus hand-picked edge cases.
# The C# ports must reproduce every value.
import json
import os
import random
import sys
import tempfile
import types
import urllib.request

import fuzzysearch
import rapidfuzz
from fuzzysearch import find_near_matches
from rapidfuzz import fuzz

OLMOCR_COMMIT = "f7cfe4c22098b154c76b6ec950d1c0a464eecf8d"
BENCH_REVISION = "54a96a6fb6a2bd3b297e59869491db4d3625b711"
CATEGORIES = ["arxiv_math", "headers_footers", "long_tiny_text", "multi_column", "old_scans", "old_scans_math", "table_tests"]

assert rapidfuzz.__version__ == "3.14.6", rapidfuzz.__version__
assert fuzzysearch.__version__ == "0.8.1", fuzzysearch.__version__


def fetch(url):
    with urllib.request.urlopen(url) as response:
        return response.read().decode("utf-8")


def load_olmocr():
    root = tempfile.mkdtemp()
    base = f"https://raw.githubusercontent.com/allenai/olmocr/{OLMOCR_COMMIT}/olmocr/"
    os.makedirs(os.path.join(root, "olmocr", "bench", "katex"))
    for rel in ["repeatdetect.py", "bench/tests.py", "bench/table_parsing.py"]:
        with open(os.path.join(root, "olmocr", rel), "w", encoding="utf-8") as f:
            f.write(fetch(base + rel))
    for rel in ["__init__.py", "bench/__init__.py", "bench/katex/__init__.py"]:
        open(os.path.join(root, "olmocr", rel), "w").close()
    # MathTest renders LaTeX in a browser; the port skips math checks, so a stub suffices.
    with open(os.path.join(root, "olmocr", "bench", "katex", "render.py"), "w") as f:
        f.write("def render_equation(*a, **k): raise NotImplementedError\n")
        f.write("def compare_rendered_equations(*a, **k): raise NotImplementedError\n")
    sys.path.insert(0, root)


def load_bench():
    rows = {}
    for category in CATEGORIES:
        url = f"https://huggingface.co/datasets/allenai/olmOCR-bench/resolve/{BENCH_REVISION}/bench_data/{category}.jsonl"
        rows[category] = [json.loads(line) for line in fetch(url).splitlines() if line.strip()]
    return rows


load_olmocr()
from olmocr.bench import table_parsing  # noqa: E402
from olmocr.repeatdetect import RepeatDetector  # noqa: E402

rng = random.Random(20260927)
bench = load_bench()
texts = [t for rows in bench.values() for r in rows for t in (r.get("text"), r.get("before"), r.get("after"), r.get("cell")) if t]
UNICODE_EDGE = ["", " ", "é", "é", "İstanbul", "ΣΟΦΟΣ ΣΑΣ", "Straße", "𝔘𝔫𝔦𝔠𝔬𝔡𝔢 😀 text", "a\x1cb\x1fc", "tab\tnew\nline sep　ideo", "ǅ title", "ﬁ ligature", "Ⅻ ⅷ ²³ ½", "日本語のテキスト", "µm and μm"]


def mutate(s, n):
    chars = list(s)
    for _ in range(n):
        if not chars:
            break
        op = rng.randrange(3)
        i = rng.randrange(len(chars))
        if op == 0:
            del chars[i]
        elif op == 1:
            chars.insert(i, rng.choice("abcxyz .,-"))
        else:
            chars[i] = rng.choice("abcxyz .,-")
    return "".join(chars)


def haystack(needle=None):
    parts = [rng.choice(texts) for _ in range(rng.randrange(1, 8))]
    if needle is not None:
        parts.insert(rng.randrange(len(parts) + 1), mutate(needle, rng.randrange(0, 4)))
    return " ".join(parts)


out = {"versions": {"olmocr": OLMOCR_COMMIT, "bench": BENCH_REVISION, "rapidfuzz": rapidfuzz.__version__, "fuzzysearch": fuzzysearch.__version__}}

# Python string semantics the ports depend on.
out["pytext"] = [
    {"s": s, "lower": s.lower(), "isalnum": [c.isalnum() for c in s], "isspace": [c.isspace() for c in s], "len": len(s)}
    for s in UNICODE_EDGE + [rng.choice(texts) for _ in range(40)]
]

# rapidfuzz ratio and partial_ratio (short and long needles, equal lengths, Unicode, empties).
pairs = [(a, b) for a in UNICODE_EDGE[:6] for b in UNICODE_EDGE[:6]]
pairs += [("this is a test", "this is a test!"), ("abcd", "dcba"), ("aaaa", "aa"), ("ab" * 40, "ba" * 45)]
for _ in range(300):
    needle = rng.choice(texts)
    pairs.append((needle, haystack(needle if rng.random() < 0.6 else None)))
for _ in range(150):
    length = rng.randrange(1, 140)
    alphabet = rng.choice(["ab", "abcde ", "the quick brown fox"])
    needle = "".join(rng.choice(alphabet) for _ in range(length))
    pairs.append((needle, "".join(rng.choice(alphabet) for _ in range(rng.randrange(0, 300)))))
for _ in range(60):
    a = rng.choice(texts)
    pairs.append((a, mutate(a, rng.randrange(0, 5))))
out["fuzz"] = [{"a": a, "b": b, "ratio": fuzz.ratio(a, b), "partial_ratio": fuzz.partial_ratio(a, b)} for a, b in pairs]

# fuzzysearch.find_near_matches with max_l_dist 0..3, covering the exact, linear-programming and
# n-gram code paths.
near = []
for _ in range(400):
    needle = rng.choice(texts)
    dist = rng.randrange(0, 4)
    if dist > len(needle) // 2:
        dist = len(needle) // 2
    hay = haystack(needle if rng.random() < 0.7 else None)
    if rng.random() < 0.3:
        hay = hay + " " + mutate(needle, rng.randrange(0, 3))
    near.append((needle, hay, dist))
for needle, hay, dist in [("ab", "xxabxxaxbxx", 1), ("abc", "abcabcab", 1), ("a", "bbb", 1), ("PATTERN", "---PATERN---", 1), ("aaa", "aaaaaaaa", 0), ("xy", "", 1), ("abcdef", "abxdefabcdeff", 2)]:
    near.append((needle, hay, dist))
out["near_matches"] = [
    {"sub": s, "seq": q, "max_l_dist": d, "matches": [[m.start, m.end, m.dist] for m in find_near_matches(s, q, max_l_dist=d)]}
    for s, q, d in near
]

# RepeatDetector (max_ngram_size=5, as BaselineTest uses).
repeat_inputs = ["", "a", "abab", "a" * 1000, "abc " * 50, "end end end\tend\nend", "日本日本日本"] + [haystack() for _ in range(20)]
repeat_inputs += ["x" + "ab" * rng.randrange(1, 60) for _ in range(10)]
out["repeats"] = []
for s in repeat_inputs:
    detector = RepeatDetector(max_ngram_size=5)
    detector.add_letters(s)
    out["repeats"].append({"s": s, "repeats": detector.ngram_repeats()})


# Markdown table parsing: cell texts, headings and every relation.
def table_to_json(table):
    def key(rc):
        return f"{rc[0]},{rc[1]}"

    cells = sorted(table.cell_text)
    return {
        "cells": {key(rc): table.cell_text[rc] for rc in cells},
        "headings": sorted(key(rc) for rc in table.heading_cells),
        "rectangular": table.is_rectangular,
        "up": {key(rc): sorted(key(x) for x in table.up_relations[rc]) for rc in cells},
        "down": {key(rc): sorted(key(x) for x in table.down_relations[rc]) for rc in cells},
        "left": {key(rc): sorted(key(x) for x in table.left_relations[rc]) for rc in cells},
        "right": {key(rc): sorted(key(x) for x in table.right_relations[rc]) for rc in cells},
        "top_heading": {key(rc): sorted(key(x) for x in table.top_heading_relations(*rc)) for rc in cells},
        "left_heading": {key(rc): sorted(key(x) for x in table.left_heading_relations(*rc)) for rc in cells},
    }


def random_markdown_table():
    rows, cols = rng.randrange(1, 6), rng.randrange(1, 5)
    lines = []
    for r in range(rows):
        width = cols if rng.random() < 0.8 else rng.randrange(1, cols + 2)
        cells = [rng.choice(texts)[:12].replace("|", "/").replace("\n", " ") for _ in range(width)]
        line = " | ".join(cells)
        if rng.random() < 0.7:
            line = "| " + line + " |"
        lines.append(line)
        if r == 0 and rng.random() < 0.7:
            lines.append("|" + "|".join(["---"] * cols) + "|")
    prose = ["Intro paragraph.", "", "Some text"] if rng.random() < 0.5 else []
    return "\n".join(prose + lines + (["", "After."] if rng.random() < 0.5 else []))


markdown_inputs = [
    "",
    "no tables here",
    "| a | b |\n|---|---|\n| 1 | 2 |",
    "a | b\n1 | 2",
    "| only one row |",
    "| h1 | h2 | h3 |\n| :-- | :-: | --: |\n| x |  | z |\n| p | q |",
    "text\n| a | b |\n| c | d |\nmiddle\n| e | f |\n| g | h |",
    "| a |\n|---|\n| - |\n| : |",
    "  | indented | row |\n  | second | row |",
] + [random_markdown_table() for _ in range(40)]
out["md_tables"] = [{"md": md, "tables": [table_to_json(t) for t in table_parsing.parse_markdown_tables(md)]} for md in markdown_inputs]

with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "expected.json"), "w", encoding="utf-8") as f:
    json.dump(out, f, ensure_ascii=False, indent=1)
    f.write("\n")
print({k: len(v) for k, v in out.items() if isinstance(v, list)})
