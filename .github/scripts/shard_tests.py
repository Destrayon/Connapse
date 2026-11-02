"""Split one test project's tests into shards by class, for parallel CI jobs (epic #783, #789, #843).

Prints the VSTest filter for one shard. Classes are listed with `dotnet test --list-tests` and dealt
out by how long they take, so the shards finish together. The ones that start their own containers go
first, one per shard in turn: a Postgres, Azurite, LocalStack or SFTP start-up costs more than most
classes' tests. The rest go longest first, each to the shard with the least work so far.

How long a class takes comes from --timings, a file this script writes from CI's TRX results
(--make-timings). Every shard reads the same committed file, so all of them agree on who runs what. A
class the file doesn't know (a new one) is estimated from its test count at the median seconds per
test of the classes it does know; with no timings at all, every test counts one second.

The last shard runs everything the other shards don't name, so a test whose class can't be read from
its display name still runs there: no test can fall between shards.

Usage:
  shard_tests.py --project tests/Connapse.Integration.Tests --shards 3 --index 1 --filter "Category=Integration&Size!=Large" \\
      --timings .github/test-timings/integration-tests.json --summary "$GITHUB_STEP_SUMMARY"
  shard_tests.py --make-timings --trx "results/**/*.trx" --out .github/test-timings/integration-tests.json
"""
import argparse
import datetime
import glob
import json
import pathlib
import re
import statistics
import subprocess
import sys
import xml.etree.ElementTree as ET

# A display name that starts with a dotted identifier path: Namespace.Class.Method or
# Namespace.Class.Method(args). Anything else (a custom DisplayName) is left to the last shard.
QUALIFIED = re.compile(r"^((?:[A-Za-z_][A-Za-z0-9_]*\.)+)[A-Za-z_][A-Za-z0-9_]*(?:\(|$)")

TRX = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# Seconds a test counts for when there are no timings to estimate from.
DEFAULT_SECONDS_PER_TEST = 1.0


def classes(project: str) -> dict[str, int]:
    """Each class in the project, with how many tests it has."""
    out = subprocess.run(
        ["dotnet", "test", project, "--no-build", "--configuration", "Release", "--list-tests"],
        capture_output=True, text=True, check=True).stdout
    listing = out.split("The following Tests are available:", 1)
    if len(listing) < 2:
        sys.exit(f"::error::dotnet test --list-tests printed no test list for {project}")
    return count_classes(listing[1].splitlines())


def count_classes(lines: list[str]) -> dict[str, int]:
    found: dict[str, int] = {}
    for line in lines:
        m = QUALIFIED.match(line.strip())
        if m:
            name = m.group(1).rstrip(".")
            found[name] = found.get(name, 0) + 1
    return found


def own_containers(project: str) -> set[str]:
    """Classes whose source starts a container of their own, or joins a collection other than the
    shared host's (whose fixture starts one)."""
    heavy = set()
    for path in pathlib.Path(project).rglob("*.cs"):
        text = path.read_text(encoding="utf-8", errors="ignore")
        if "ContainerBuilder" in text or re.search(r'\[Collection\("(?!Integration Tests")', text):
            heavy.update(re.findall(r"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", text))
    return heavy


def estimate(counts: dict[str, int], timings: dict[str, float]) -> dict[str, float]:
    """Seconds each class is expected to take: its timing, or its test count at the median rate."""
    rates = [timings[c] / n for c, n in counts.items() if c in timings and n > 0]
    per_test = statistics.median(rates) if rates else DEFAULT_SECONDS_PER_TEST
    return {c: timings[c] if c in timings else n * per_test for c, n in counts.items()}


def plan(counts: dict[str, int], heavy: set[str], timings: dict[str, float], shards: int) -> list[list[str]]:
    """The classes for each shard. Container classes are spread first, fewest per shard; the rest go
    longest first to the least loaded shard. Ties break by name, so every shard computes the same plan."""
    seconds = estimate(counts, timings)
    load = [0.0] * shards
    containers = [0] * shards
    assigned: list[list[str]] = [[] for _ in range(shards)]
    by_weight = lambda c: (-seconds[c], c)
    for c in sorted((c for c in counts if c.rsplit(".", 1)[-1] in heavy), key=by_weight):
        s = min(range(shards), key=lambda i: (containers[i], load[i], i))
        assigned[s].append(c); load[s] += seconds[c]; containers[s] += 1
    for c in sorted((c for c in counts if c.rsplit(".", 1)[-1] not in heavy), key=by_weight):
        s = min(range(shards), key=lambda i: (load[i], i))
        assigned[s].append(c); load[s] += seconds[c]
    return assigned


def by_name(counts: dict[str, int], heavy: set[str], shards: int) -> list[list[str]]:
    """The order before #843, kept for comparing against: container classes first, then the rest, dealt
    round robin by name."""
    names = sorted(counts)
    ordered = [c for c in names if c.rsplit(".", 1)[-1] in heavy] + [c for c in names if c.rsplit(".", 1)[-1] not in heavy]
    return [[c for i, c in enumerate(ordered) if i % shards == s] for s in range(shards)]


def shard_filter(assigned: list[list[str]], index: int, tier: str) -> str:
    """The VSTest filter for one shard. The trailing dot keeps Foo from also matching FooBar."""
    if index < len(assigned):
        mine = assigned[index - 1]
        if not mine:
            sys.exit(f"::error::shard {index} of {len(assigned)} got no classes; use fewer shards")
        selection = "|".join(f"FullyQualifiedName~{c}." for c in mine)
    else:
        others = [c for shard in assigned[:-1] for c in shard]
        selection = "&".join(f"FullyQualifiedName!~{c}." for c in others) or "FullyQualifiedName~."
    return f"({tier})&({selection})"


def attributed_seconds(results: list[tuple[datetime.datetime, datetime.datetime, str]]) -> dict[str, float]:
    """Wall-clock seconds per class in one run. Unlike TRX durations, this keeps what happens between
    tests, so a run's classes add up to its span. Each test counts from its start to its end. A gap
    before the next test of the same class is that class's; a gap where the class changes is the next
    class's, since that is when it builds its fixture and starts its containers. Only the host's
    start-up, before the first test, belongs to no class: every shard pays it."""
    ordered = sorted(results)
    seconds: dict[str, float] = {}
    for i, (start, end, cls) in enumerate(ordered):
        seconds[cls] = seconds.get(cls, 0.0) + max((end - start).total_seconds(), 0.0)
        if i + 1 < len(ordered):
            following, _, next_cls = ordered[i + 1]
            # Overlapping tests (a parallel collection) leave no gap.
            gap = max((following - end).total_seconds(), 0.0)
            seconds[next_cls] = seconds.get(next_cls, 0.0) + gap
    return seconds


def _time(text: str) -> datetime.datetime:
    # TRX writes seven fractional digits; Python reads six.
    return datetime.datetime.fromisoformat(re.sub(r"(\.\d{6})\d+", r"\1", text))


def trx_results(path: str) -> list[tuple[datetime.datetime, datetime.datetime, str]]:
    root = ET.parse(path).getroot()
    class_of = {t.get("id"): t.find("t:TestMethod", TRX).get("className") for t in root.iterfind(".//t:UnitTest", TRX)}
    return [(_time(r.get("startTime")), _time(r.get("endTime")), class_of[r.get("testId")])
            for r in root.iterfind(".//t:UnitTestResult", TRX)
            if r.get("startTime") and r.get("endTime") and r.get("testId") in class_of]


def make_timings(pattern: str) -> dict[str, float]:
    """Per-class seconds from every TRX file the pattern matches; each file is one shard's run."""
    files = sorted(glob.glob(pattern, recursive=True))
    if not files:
        sys.exit(f"::error::no TRX files match {pattern}")
    seconds: dict[str, float] = {}
    for path in files:
        for cls, s in attributed_seconds(trx_results(path)).items():
            seconds[cls] = seconds.get(cls, 0.0) + s
    return {c: round(s, 1) for c, s in sorted(seconds.items())}


def read_timings(path: str | None) -> dict[str, float]:
    if not path or not pathlib.Path(path).exists():
        return {}
    return json.loads(pathlib.Path(path).read_text(encoding="utf-8"))["seconds"]


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--make-timings", action="store_true", help="write per-class seconds from --trx to --out")
    p.add_argument("--trx", help="TRX files to read timings from (a glob)")
    p.add_argument("--out", help="where --make-timings writes")
    p.add_argument("--project")
    p.add_argument("--shards", type=int)
    p.add_argument("--index", type=int, help="1-based")
    p.add_argument("--filter", help="the tier's filter, applied to every shard")
    p.add_argument("--timings", help="per-class seconds, from --make-timings")
    p.add_argument("--summary", help="a Markdown file to append this shard's plan to ($GITHUB_STEP_SUMMARY)")
    p.add_argument("--predicted-out", help="a file to write this shard's predicted seconds to")
    p.add_argument("--strategy", choices=["timed", "name"], default="timed",
                   help="timed (by past run time) or name (round robin, as before #843, for comparing)")
    a = p.parse_args()

    if a.make_timings:
        if not a.trx or not a.out:
            sys.exit("::error::--make-timings needs --trx and --out")
        seconds = make_timings(a.trx)
        pathlib.Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        pathlib.Path(a.out).write_text(json.dumps({"seconds": seconds}, indent=2) + "\n", encoding="utf-8")
        print(f"{len(seconds)} classes, {sum(seconds.values()):.0f} s in all")
        return 0

    if not (a.project and a.shards and a.index and a.filter):
        sys.exit("::error::--project, --shards, --index and --filter are required")
    if not 1 <= a.index <= a.shards:
        sys.exit(f"::error::shard index {a.index} is outside 1..{a.shards}")

    counts = classes(a.project)
    timings = read_timings(a.timings)
    heavy = own_containers(a.project)
    assigned = plan(counts, heavy, timings, a.shards) if a.strategy == "timed" else by_name(counts, heavy, a.shards)
    print(shard_filter(assigned, a.index, a.filter))

    seconds = estimate(counts, timings)
    predicted = [sum(seconds[c] for c in shard) for shard in assigned]
    if a.predicted_out:
        pathlib.Path(a.predicted_out).write_text(f"{predicted[a.index - 1]:.0f}\n", encoding="utf-8")
    if a.summary:
        unknown = sorted(c for c in counts if c not in timings)
        lines = [f"### Shard {a.index} of {a.shards} ({a.strategy})",
                 f"{len(assigned[a.index - 1])} classes, predicted {predicted[a.index - 1]:.0f} s of tests "
                 f"(all shards: {', '.join(f'{s:.0f}' for s in predicted)} s).",
                 f"Timed from history: {len(counts) - len(unknown)} classes; estimated from test count: {len(unknown)}.", ""]
        with open(a.summary, "a", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
