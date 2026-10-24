"""The grep baseline for the code-question set (CON-35): what `grep -w -c <name>` finds.

    python -I grep_baseline.py <work-dir> <questions.jsonl> <out.json>

For each question, every text file the repo tracks at its pinned commit is ranked by how many of its
lines hold the symbol's own name (the last part of `Type.Member`) as a whole word, which is the count
`grep -w -c` prints, most first, ties by path.
Recall@k is the share of the answer key's files in the top k, capped at k when the key is bigger:
|top-k ∩ key| / min(|key|, k). The later gate for the code engine is Recall@10 0.15 above this, on the
held-out half.
"""
import json
import re
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

KS = (5, 10, 20)
WORD = re.compile(r"[A-Za-z_$][\w$]*")


def corpus(src: Path) -> dict[str, Counter]:
    """For each tracked text file, how many lines hold each whole word. Binary files (a NUL byte) are skipped."""
    files = subprocess.run(["git", "-C", str(src), "ls-files", "-z"], capture_output=True, check=True).stdout.decode().split("\0")
    out = {}
    for f in filter(None, files):
        data = (src / f).read_bytes()
        if b"\0" not in data[:8192]:
            lines = Counter()
            for line in data.decode("utf-8", errors="replace").splitlines():
                lines.update(set(WORD.findall(line)))
            out[f] = lines
    return out


def recall(ranked: list[str], key: list[str], k: int) -> float:
    return len(set(ranked[:k]) & set(key)) / min(len(key), k)


def main() -> int:
    work, questions_path, out = Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])
    questions = [json.loads(line) for line in questions_path.read_text(encoding="utf-8").splitlines() if line]
    words = {}
    groups = defaultdict(lambda: defaultdict(list))
    for q in questions:
        if q["repo"] not in words:
            words[q["repo"]] = corpus(work / "src" / q["repo"])
        term = q["name"].split(".")[-1]
        hits = sorted(((-c[term], f) for f, c in words[q["repo"]].items() if c[term]))
        ranked = [f for _, f in hits]
        # Crowded: more files hold the name than the top 10 has room for, so grep's ranking decides.
        crowd = "crowded" if len(ranked) > 10 else "sparse"
        for k in KS:
            r = recall(ranked, q["answer"]["files"], k)
            for group in (f"{q['split']}", f"{q['split']}/{q['kind']}", f"{q['split']}/{crowd}",
                          f"{q['split']}/{q['kind']}/{q['repo']}", f"{q['split']}/{crowd}/{q['kind']}"):
                groups[group][f"recall@{k}"].append(r)

    result = {g: {"questions": len(m["recall@10"]), **{name: round(sum(v) / len(v), 4) for name, v in m.items()}}
              for g, m in sorted(groups.items())}
    out.write_text(json.dumps({"system": "grep -w -c, files ranked by matching lines", "groups": result}, indent=2) + "\n", encoding="utf-8")
    for g, m in result.items():
        if g.count("/") < 2:
            print(f"{g:24} n={m['questions']:3}  R@5 {m['recall@5']:.3f}  R@10 {m['recall@10']:.3f}  R@20 {m['recall@20']:.3f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
