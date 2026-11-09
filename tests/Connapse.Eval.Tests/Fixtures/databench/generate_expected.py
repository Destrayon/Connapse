# Regenerates expected.json for TableAnswerGrader (tests/Connapse.Eval/Metrics/TableAnswerGrader.cs).
#   pip install pandas numpy ; python -I generate_expected.py
# Downloads databench_eval's eval.py at the pinned commit and records, for hand-picked and seeded cases,
# what DataBench's default_compare returns ("reference") and what the Connapse rules return ("connapse").
# The Connapse rules are written out again below, independently of the C# port, which must reproduce
# every "connapse" value. Where the two differ, the case names the CON-E6 rule change that causes it.
import json
import math
import os
import random
import re
import sys
import tempfile
import types
import urllib.request
from collections import Counter
from datetime import date

DATABENCH_COMMIT = "65f92cfad4f986f5496170f6bbcee48323f56c2f"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "expected.json")


def load_reference():
    url = f"https://raw.githubusercontent.com/jorses/databench_eval/{DATABENCH_COMMIT}/src/databench_eval/eval.py"
    with urllib.request.urlopen(url) as response:
        source = response.read().decode("utf-8")
    root = tempfile.mkdtemp()
    package = os.path.join(root, "databench_eval")
    os.makedirs(package)
    with open(os.path.join(package, "eval.py"), "w", encoding="utf-8") as f:
        f.write(source)
    with open(os.path.join(package, "__init__.py"), "w") as f:
        f.write("")
    # default_compare needs none of these; the stubs only satisfy eval.py's imports.
    with open(os.path.join(package, "utils.py"), "w") as f:
        f.write("def load_qa(**kwargs): raise NotImplementedError\n")
    sys.modules["tqdm"] = types.SimpleNamespace(tqdm=lambda x, **k: x)
    sys.modules["datasets"] = types.SimpleNamespace(Dataset=object)
    sys.path.insert(0, root)
    from databench_eval.eval import Evaluator

    return Evaluator(compare=None, qa=[]).default_compare


# ---- The Connapse rules, written independently of the C# port ----

STRIP = "[]'\" "
NULLS = {"nan", "", " ", "np.nan", "None"}
ISO = re.compile(r"^(\d{4}-\d{2}-\d{2})(?:[Tt ](\d{2}):(\d{2})(?::(\d{2})(?:\.\d+)?)?(?:[Zz]|[+-]\d{2}:?\d{2})?)?$")


def text(v):
    return None if v is None else str(v)


def is_null(v):
    return v is None or str(v).strip(STRIP) in NULLS


def norm(s):
    return re.sub(r"\s+", " ", s.strip()).lower()


def iso_date(s):
    m = ISO.match(s.strip())
    if not m:
        return None
    if m.group(2) is not None and (int(m.group(2)) > 23 or int(m.group(3)) > 59 or (m.group(4) is not None and int(m.group(4)) > 59)):
        return None
    try:
        return date.fromisoformat(m.group(1))
    except ValueError:
        return None


def number(v):
    if isinstance(v, bool):
        v = str(v)
    if isinstance(v, (int, float)):
        return float(v), isinstance(v, int)
    cleaned = "".join(c for c in str(v) if c in "0123456789.-")
    if not re.fullmatch(r"-?(\d+\.?\d*|\.\d+)", cleaned):
        return None
    return float(cleaned), "." not in cleaned


def close(v, t, integral):
    return v == t if integral else abs(v - t) <= 0.01 * abs(t)


def items(v):
    if isinstance(v, list):
        return list(v)
    return [p.strip() for p in str(v).strip("[]").split(",")]


def connapse(value, truth, semantic):
    if is_null(value) or is_null(truth):
        return is_null(value) and is_null(truth)
    if semantic == "boolean":
        a, b = str(value).strip(STRIP).lower(), str(truth).strip(STRIP).lower()
        return (a in {"true", "yes", "y"} and b in {"true", "yes", "y"}) or (a in {"false", "no", "n"} and b in {"false", "no", "n"})
    if semantic == "category":
        a, b = norm(str(value).strip(STRIP)), norm(str(truth).strip(STRIP))
        if a == b:
            return True
        da, db = iso_date(a), iso_date(b)
        return da is not None and db is not None and da == db
    if semantic == "number":
        a, b = number(value), number(truth)
        return a is not None and b is not None and close(a[0], b[0], b[1])
    if semantic == "list[category]":
        def item(x):
            s = ("None" if x is None else str(x)).strip(STRIP)
            return "" if s in NULLS else norm(s)

        a, b = [item(x) for x in items(value)], [item(x) for x in items(truth)]
        if len(a) != len(b):
            return False
        da, db = [iso_date(x) for x in a], [iso_date(x) for x in b]
        if None not in da and None not in db:
            return Counter(da) == Counter(db)
        return Counter(a) == Counter(b)
    if semantic == "list[number]":
        a = [number(x) for x in items(value) if x is not None and str(x).strip()]
        b = [number(x) for x in items(truth) if x is not None and str(x).strip()]
        if None in a or None in b or len(a) != len(b):
            return False
        return all(close(x[0], y[0], y[1]) for x, y in zip(sorted(a), sorted(b)))
    raise ValueError(semantic)


# ---- Cases ----

# DataBench's own test cases (tests/test_eval.py at the pinned commit), then edge cases for each rule change.
# A case that the two rules grade differently carries the reason.
HAND = [
    ("boolean", True, "True"), ("boolean", "True", True), ("boolean", "[True]", "True"), ("boolean", "False", "False"),
    ("boolean", " True ", "True"), ("boolean", "false", "False"), ("boolean", "True", "yes"), ("boolean", "False", "no"),
    ("boolean", "True", "no"), ("boolean", "False", "yes"), ("boolean", "true", "yes"), ("boolean", "false", "no"),
    ("boolean", "yes", "yes"), ("boolean", "no", "no"), ("boolean", None, "True"), ("boolean", "Y", "true"),
    ("boolean", "maybe", "maybe"), ("boolean", False, "n"),
    ("category", "Barack Obama", "Barack Obama"), ("category", " Barack Obama ", "Barack Obama"), ("category", "T-rex", "T-rex"),
    ("category", "", ""), ("category", " ", ""), ("category", "Different", "Strings"), ("category", None, "String"),
    ("category", "2016-12-22 00:00:00+00:00", "2016-12-22"), ("category", "2016-12-22T00:00:00Z", "2016-12-22"),
    ("category", "22/12/2016", "2016-12-22", "ISO dates only"), ("category", "Dec 22, 2016", "2016-12-22", "ISO dates only"),
    ("category", "2016-12-22", "2016-12-23"), ("category", "Invalid Date", "2016-12-22"), ("category", "2016-13-45", "2016-12-22"),
    ("category", None, None), (" category ", 123, "123"),
    ("category", "barack obama", "Barack Obama", "case-insensitive categories"),
    ("category", "Barack   Obama", "Barack Obama", "collapsed whitespace"),
    ("category", "Barack\tObama", "barack obama", "case-insensitive categories"),
    ("category", "2016-12-22T25:00:00", "2016-12-22"), ("category", "2016-12-22T10:61", "2016-12-22"),
    ("category", "2016-12-22T10:30:61", "2016-12-22"), ("category", "2016-12-22T10:30:15.123+0530", "2016-12-22"),
    ("category", "2016-02-30", "2016-02-30"), ("category", "2016-02-30", "2016-03-01"),
    ("number", "123.45", 123.45), ("number", "$123.45", "123.45"), ("number", "-123.45", "-123.45"),
    ("number", "1.234", "1.23"), ("number", "$1,234.56", "1234.56"), ("number", "invalid", "123.45"), ("number", None, "123.45"),
    ("number", 123.45, "123.45"), ("number", 100, 100), ("number", 101, 100), ("number", 100.001, 100, "exact integers"), ("number", "100.0", 100),
    ("number", 100.5, 100.0, "1% tolerance"), ("number", 100.999, 100.0, "1% tolerance"), ("number", 101.01, 100.0),
    ("number", 99.0, 100.0, "1% tolerance"), ("number", 98.99, 100.0), ("number", 0, 0), ("number", 0.001, 0.0, "1% tolerance"),
    ("number", "-5", -5), ("number", "5-", "5"), ("number", "1.2.3", "1.2"), ("number", ".5", 0.5), ("number", "5.", 5.0),
    ("number", True, 1), ("number", "1,000", 1000), ("number", "1e3", 1000), ("number", 1e3, 1000),
    ("number", 0.3333, 0.33), ("number", 12.344, 12.34),
    ("list[category]", "['a', 'b', 'c']", "['c', 'b', 'a']"), ("list[category]", "a,b,c", "c,b,a"),
    ("list[category]", "[a, b, c]", "[c, b, a]"), ("list[category]", "['a', 'b']", "['a', 'b', 'c']"),
    ("list[category]", "invalid", "['a', 'b']"), ("list[category]", None, "['a', 'b']"), ("list[category]", "[nan]", "['']"),
    ("list[category]", "['2023-01-01', '2023-01-02']", "['2023-01-02', '2023-01-01']"),
    ("list[category]", "['2023-01-01T00:00:00Z', '2023-01-02 00:00:00+00:00']", "['2023-01-02', '2023-01-01']"),
    ("list[category]", "['2023-01-01']", "['2023-01-02']"), ("list[category]", "[]", "[]"),
    ("list[category]", ["a", "b"], ["b", "a"]), ("list[category]", ["A", "b"], "['a', 'B']", "case-insensitive categories"),
    ("list[category]", ["Smith, John", "Lee"], ["Lee", "Smith, John"]),
    ("list[category]", ["Smith, John", "Lee"], "['Lee', 'Smith, John']", "JSON arrays are not split on commas"),
    ("list[category]", ["a", "a", "b"], ["a", "b", "b"], "lists are multisets"),
    ("list[category]", ["a", None], ["a", ""]), ("list[category]", ["x", "y"], ["x", "z"]),
    ("list[category]", "['01/01/2023', '2023-01-02']", "['2023-01-02', '2023-01-01']", "ISO dates only"),
    ("list[number]", "[1, 2, 3]", "[3, 2, 1]"), ("list[number]", "1,2,3", "3,2,1"), ("list[number]", "[1, 2]", "[1, 2, 3]"),
    ("list[number]", "invalid", "[1, 2]"), ("list[number]", None, "[1, 2]"), ("list[number]", "[1.1, 2.2]", "[2.2, 1.1]"),
    ("list[number]", "[1.0, 2.0]", "[2.0, 1.0]"), ("list[number]", "[1.0]", "[2.0]"),
    ("list[number]", "[0.995, 0.4924, 0.3099, 0.281]", "[0.99, 0.49, 0.30, 0.28]", "1% tolerance"),
    ("list[number]", [1, 2, 3], [3, 1, 2]), ("list[number]", [1, 1, 2], [1, 2, 2], "lists are multisets"),
    ("list[number]", [10.05, 20], [20, 10.0], "1% tolerance"), ("list[number]", [10.2, 20], [20, 10.0]), ("list[number]", [21, 10], [20, 10]),
    ("list[number]", "[$1,000]", "[1000]"), ("list[number]", "[1, , 2]", "[1, 2]"), ("list[number]", ["x", 2], [1, 2]),
    ("list[number]", [1, 2], ["x", 2]),
]


def random_cases(rng):
    words = ["Alpha", "beta Co", "North East", "Orbit Labs", "q3", "Delta", "ACME Ltd", "san jose"]
    out = []
    for _ in range(60):
        t = round(rng.uniform(-5000, 5000), rng.choice([0, 1, 2, 3]))
        t = int(t) if rng.random() < 0.4 else t
        v = t * (1 + rng.uniform(-0.03, 0.03)) if rng.random() < 0.7 else t
        v = round(v, 4)
        if rng.random() < 0.3:
            v = f"${v:,}"
        out.append(("number", v, t))
    for _ in range(40):
        w = rng.choice(words)
        v = rng.choice([w, w.upper(), w.lower(), f"  {w} ", w.replace(" ", "  "), rng.choice(words)])
        out.append(("category", v, w))
    for _ in range(25):
        d = date(2020, 1, 1).toordinal() + rng.randrange(1500)
        iso = date.fromordinal(d).isoformat()
        other = date.fromordinal(d + rng.choice([0, 0, 1])).isoformat()
        v = rng.choice([other, other + "T00:00:00Z", other + " 12:30:00+02:00", other + "T08:15"])
        out.append(("category", v, iso))
    for _ in range(25):
        a = rng.choice(["yes", "no", "True", "false", "Y", "n", "maybe"])
        b = rng.choice(["yes", "no", "True", "false"])
        out.append(("boolean", a, b))
    for _ in range(30):
        base = rng.sample(words, rng.randint(1, 4))
        v = base[:]
        rng.shuffle(v)
        if rng.random() < 0.3:
            v[0] = v[0].upper()
        if rng.random() < 0.2:
            v = v[:-1] if len(v) > 1 else v + ["extra"]
        out.append(("list[category]", v, base))
    for _ in range(30):
        base = [round(rng.uniform(0, 1000), rng.choice([0, 2])) for _ in range(rng.randint(1, 5))]
        base = [int(x) if float(x).is_integer() and rng.random() < 0.5 else x for x in base]
        v = [round(x * (1 + rng.uniform(-0.02, 0.02)), 3) if rng.random() < 0.5 else x for x in base]
        rng.shuffle(v)
        out.append(("list[number]", v, base))
    return out


def main():
    reference = load_reference()
    rng = random.Random(20261108)
    cases = []
    for entry in HAND:
        semantic, value, truth = entry[:3]
        reason = entry[3] if len(entry) > 3 else None
        ref = bool(reference(value, truth, semantic))
        con = connapse(value, truth, semantic.strip())
        if ref != con and reason is None:
            raise SystemExit(f"unexplained deviation: {entry} reference={ref} connapse={con}")
        if ref == con and reason is not None:
            raise SystemExit(f"reason given but no deviation: {entry}")
        cases.append({"type": semantic, "value": value, "truth": truth, "reference": ref, "connapse": con, "deviation": reason})
    for semantic, value, truth in random_cases(rng):
        ref = bool(reference(value, truth, semantic))
        con = connapse(value, truth, semantic)
        reason = None
        if ref != con:
            # Seeded cases differ from DataBench only through the number, case or multiset rules.
            reason = {"number": "1% tolerance", "list[number]": "1% tolerance", "category": "case-insensitive categories",
                      "list[category]": "case-insensitive categories"}.get(semantic)
            if reason is None:
                raise SystemExit(f"unexplained seeded deviation: {semantic} {value!r} {truth!r}")
        cases.append({"type": semantic, "value": value, "truth": truth, "reference": ref, "connapse": con, "deviation": reason})

    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"databench_commit": DATABENCH_COMMIT, "cases": cases}, f, indent=1, ensure_ascii=False)
        f.write("\n")
    print(f"{len(cases)} cases, {sum(c['deviation'] is not None for c in cases)} deviations -> {OUT}")


if __name__ == "__main__":
    main()
