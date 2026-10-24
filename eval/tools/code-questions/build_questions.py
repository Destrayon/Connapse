"""Builds the code-question set (CON-35) from the indexes that index_repos.sh wrote.

    python -I build_questions.py <work-dir> <out.jsonl>

Every answer key comes from a compiler-grade indexer, never from a parser or a search: Roslyn for C#,
scip-typescript and scip-python for the rest. A key says where a symbol is defined, which files
reference it, and for a "how" question which in-repo definitions its body uses. Symbols are sampled by
how many files reference them, evenly across five bands, so rare and widely used symbols are both
asked about. Each symbol's questions fall in the tuning or the held-out half by a hash of the fixed
seed and the symbol, decided here, before any system runs.
"""
import hashlib
import json
import math
import random
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import scip  # noqa: E402

# Per repo, how many questions of each kind to draw.
TARGETS = {"definition": 32, "callers": 20, "usages": 20, "how": 12}
BANDS = 5  # reference-file counts 1, 2-3, 4-7, 8-15, 16-30
MAX_FILES = 30  # a symbol referenced from more files makes an answer key too big to judge
NOT_DEFINED_HERE = re.compile(
    r"(^|/)(tests?|[^/]*\.Tests?|docs|examples|benchmarks?|Migrations|obj|bin)/|(^|/)test_[^/]*\.py$|\.(test|spec)\.ts$"
    r"|\.(Designer|g)\.cs$|\.d\.ts$")
NAME = re.compile(r"^[A-Za-z_$][\w$]*$")


def descriptors(symbol: str) -> list[tuple[str, str]]:
    """A SCIP symbol's descriptors as (name, suffix): '/' namespace, '#' type, '.' term, '()' method."""
    s, out, i = symbol.split(" ", 4)[4], [], 0
    while i < len(s):
        if s[i] in "([":
            j = s.index(")" if s[i] == "(" else "]", i)
            out.append((s[i + 1:j], s[i]))
            i = j + 1
            continue
        if s[i] == "`":
            j = s.index("`", i + 1)
            name, i = s[i + 1:j], j + 1
        else:
            j = i
            while j < len(s) and s[j] not in "/#.:!([`":
                j += 1
            name, i = s[i:j], j
        if i < len(s) and s[i] == "(":
            i = s.index(")", i) + 2  # a method's disambiguator and the '.' after it
            out.append((name, "()"))
        else:
            out.append((name, s[i] if i < len(s) else ""))
            i += 1
    return out


def describe_scip(symbol: str) -> tuple[str, str, str] | None:
    """(kind, qualified name, noun) for a SCIP symbol worth asking about, else None."""
    d = descriptors(symbol)
    if not d:
        return None
    name, suffix = d[-1]
    parent = d[-2] if len(d) > 1 else ("", "/")
    in_type = parent[1] == "#"
    qualified = f"{parent[0]}.{name}" if in_type else name
    if suffix == "()":
        return "callable", qualified, "method" if in_type else "function"
    if suffix == "#":
        return "type", qualified, "type"
    if suffix == ".":
        return "term", qualified, "member" if in_type else "variable"
    return None


def from_scip(path: Path) -> dict:
    """Symbol records from a SCIP index: kind, name, definition, body lines, references and uses (1-based lines)."""
    docs = scip.read(str(path))
    found, refs, occurrences = {}, defaultdict(list), {}
    for doc in docs:
        p = doc.path.replace("\\", "/")
        occurrences[p] = doc.occurrences
        defs = sorted((o for o in doc.occurrences if o.roles & scip.DEFINITION and not o.symbol.startswith("local ")), key=lambda o: o.line)
        for k, o in enumerate(defs):
            # Without an enclosing range (scip-python) a body runs to the next definition outside it.
            if o.enclosing:
                body = [o.enclosing[0] + 1, o.enclosing[2] + 1]
            else:
                after = next((n.line for n in defs[k + 1:] if not n.symbol.startswith(o.symbol)), None)
                body = [o.line + 1, after if after is not None else max(x.line for x in doc.occurrences) + 1]
            d = describe_scip(o.symbol)
            if d and o.symbol not in found:
                found[o.symbol] = {"kind": d[0], "qualified": d[1], "noun": d[2], "path": p, "line": o.line + 1, "body": body}
        for o in doc.occurrences:
            if not o.roles & (scip.DEFINITION | scip.IMPORT) and not o.symbol.startswith("local "):
                refs[o.symbol].append((p, o.line + 1))
    for symbol, info in found.items():
        info["refs"] = refs.get(symbol, [])
        start, end = info["body"]
        uses = []
        for o in occurrences[info["path"]]:
            s = o.symbol
            if (start <= o.line + 1 <= end and not o.roles & scip.DEFINITION and s in found and s not in uses
                    and not s.startswith(symbol) and not symbol.startswith(s)):
                uses.append(s)
        info["uses"] = uses
    return found


def from_roslyn(path: Path) -> tuple[dict, list[str]]:
    """Symbol records from RoslynIndex's output, in from_scip's shape, and the files that didn't compile."""
    data = json.loads(path.read_text(encoding="utf-8"))
    return {r["symbol"]: {**r, "refs": [tuple(x) for x in r["refs"]]} for r in data["symbols"]}, data["errorFiles"]


def band(files: int) -> int:
    return min(int(math.log2(files)), BANDS - 1)


def split_of(seed: int, repo: str, symbol: str) -> str:
    h = hashlib.sha256(f"{seed}:{repo}:{symbol}".encode()).digest()
    return "heldout" if h[0] % 2 else "tuning"


def build(work: Path, repos: dict) -> list[dict]:
    seed, questions = repos["seed"], []
    for repo in repos["repos"]:
        name = repo["name"]
        src = work / "src" / name
        if repo["indexer"] == "roslyn":
            found, error_files = from_roslyn(work / "out" / f"{name}.roslyn.json")
        else:
            found, error_files = from_scip(work / "out" / f"{name}.scip"), []
        # What a body uses: in-repo methods and types, and anything defined in another file (a TypeScript
        # `const f = () => ...` is a variable to the indexer, yet calling it is calling code elsewhere).
        for info in found.values():
            info["uses"] = [u for u in info["uses"] if u in found
                            and (found[u]["kind"] in ("callable", "type") or found[u]["path"] != info["path"])]

        # scip-python files a function nested in a method under the class, as if it were a method; its
        # def sits deeper than its class's members (or, outside a class, deeper than the module's).
        if repo["language"] == "python":
            lines = {}

            def indent(info):
                text = lines.setdefault(info["path"], (src / info["path"]).read_text(encoding="utf-8").splitlines())[info["line"] - 1]
                return len(text) - len(text.lstrip())

            for s, info in found.items():
                owner = s[:s.rindex("#") + 1] if "#" in s.split("/")[-1] else None
                expected = indent(found[owner]) + 4 if owner in found else 0
                info["nested"] = info["kind"] == "callable" and indent(info) > expected

        # A question names a symbol by its qualified name, so that name must pick out one definition.
        counts = defaultdict(int)
        for info in found.values():
            counts[(info["kind"], info["qualified"])] += 1
        candidates = {}
        for s, info in found.items():
            short = info["qualified"].split(".")[-1]
            files = sorted({p for p, _ in info["refs"]})
            if (counts[(info["kind"], info["qualified"])] == 1 and NAME.match(short) and len(short) >= 3 and not short.startswith("__")
                    and not info.get("nested")
                    and not NOT_DEFINED_HERE.search(info["path"]) and 1 <= len(files) <= MAX_FILES):
                candidates[s] = files

        # The index may miss uses in two kinds of file: Razor markup, which isn't compiled into the indexed
        # C#, and C# files that don't compile (tests of Razor components), where a name may not bind. A
        # symbol either names outside its known references is asked about only where those uses don't
        # matter: its definition and its body.
        blind = {}
        if repo["language"] == "csharp":
            blind = {str(p.relative_to(src)).replace("\\", "/"): p for p in src.rglob("*") if p.suffix in (".razor", ".cshtml")}
            blind.update({f: src / f for f in error_files})
        blind_words = {f: set(re.findall(r"[A-Za-z_$][\w$]*", p.read_text(encoding="utf-8", errors="replace"))) for f, p in blind.items()}
        unindexed = {s for s, files in candidates.items()
                     if any(found[s]["qualified"].split(".")[-1] in w and f not in files for f, w in blind_words.items())}

        for kind, target in TARGETS.items():
            if kind == "definition":
                pool = list(candidates)
            elif kind == "callers":
                pool = [s for s in candidates if found[s]["kind"] == "callable" and s not in unindexed]
            elif kind == "usages":
                pool = [s for s in candidates if found[s]["kind"] in ("type", "term") and s not in unindexed]
            else:
                pool = [s for s in candidates if found[s]["kind"] == "callable"
                        and found[s]["body"][1] - found[s]["body"][0] >= 7 and len(found[s]["uses"]) >= 2]
            rng = random.Random(f"{seed}:{name}:{kind}")
            by_band = defaultdict(list)
            for s in sorted(pool):
                by_band[band(len(candidates[s]))].append(s)
            for b in by_band.values():
                rng.shuffle(b)
            picked = []
            while len(picked) < target and any(by_band.values()):
                for b in range(BANDS):
                    if by_band[b] and len(picked) < target:
                        picked.append(by_band[b].pop())

            for n, s in enumerate(picked, 1):
                info, files = found[s], candidates[s]
                noun, qualified = info["noun"], info["qualified"]
                answer = {"definition": {"path": info["path"], "lines": info["body"]}}
                if kind == "definition":
                    text, answer["files"] = f"Where is the {noun} `{qualified}` defined?", [info["path"]]
                elif kind == "callers":
                    text, answer["files"] = f"What calls the {noun} `{qualified}`?", files
                elif kind == "usages":
                    text, answer["files"] = f"What uses the {noun} `{qualified}`?", files
                else:
                    text = f"How does the {noun} `{qualified}` work?"
                    answer["uses"] = [{"symbol": u, "path": found[u]["path"], "line": found[u]["line"]} for u in info["uses"]]
                    answer["files"] = sorted({info["path"], *(found[u]["path"] for u in info["uses"])})
                if kind in ("callers", "usages"):
                    answer["locations"] = [{"path": p, "line": line} for p, line in sorted(set(info["refs"]))]
                questions.append({
                    "id": f"{name}-{kind}-{n:03d}", "repo": name, "kind": kind, "question": text, "name": qualified,
                    "symbol": s, "referencingFiles": len(files), "split": split_of(seed, name, s), "answer": answer,
                })
    return questions


def main() -> int:
    work, out = Path(sys.argv[1]), Path(sys.argv[2])
    repos = json.loads((Path(__file__).parent / "../../datasets/code-questions/repos.json").read_text(encoding="utf-8"))
    questions = build(work, repos)
    with out.open("w", encoding="utf-8", newline="\n") as f:
        for q in questions:
            f.write(json.dumps(q, ensure_ascii=False) + "\n")
    tally = defaultdict(int)
    for q in questions:
        tally[(q["repo"], q["kind"], q["split"])] += 1
    for key in sorted(tally):
        print(*key, tally[key])
    print(len(questions), "questions")
    return 0


if __name__ == "__main__":
    sys.exit(main())
