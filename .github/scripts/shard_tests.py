"""Split one test project's tests into shards by class, for parallel CI jobs (epic #783, #789).

Prints the VSTest filter for one shard. Classes are listed with `dotnet test --list-tests` and
dealt out by name, the ones that start their own containers first: a Postgres, Azurite, LocalStack
or SFTP start-up costs more than most classes' tests, so those are spread one per shard in turn.
The last shard runs everything the other shards don't name, so a test whose class can't be read
from its display name still runs there: no test can fall between shards.

Usage:
  shard_tests.py --project tests/Connapse.Integration.Tests --shards 3 --index 1 --filter "Category=Integration&Size!=Large"
"""
import argparse
import pathlib
import re
import subprocess
import sys

# A display name that starts with a dotted identifier path: Namespace.Class.Method or
# Namespace.Class.Method(args). Anything else (a custom DisplayName) is left to the last shard.
QUALIFIED = re.compile(r"^((?:[A-Za-z_][A-Za-z0-9_]*\.)+)[A-Za-z_][A-Za-z0-9_]*(?:\(|$)")


def classes(project: str) -> list[str]:
    out = subprocess.run(
        ["dotnet", "test", project, "--no-build", "--configuration", "Release", "--list-tests"],
        capture_output=True, text=True, check=True).stdout
    listing = out.split("The following Tests are available:", 1)
    if len(listing) < 2:
        sys.exit(f"::error::dotnet test --list-tests printed no test list for {project}")
    found = set()
    for line in listing[1].splitlines():
        m = QUALIFIED.match(line.strip())
        if m:
            found.add(m.group(1).rstrip("."))
    return sorted(found)


def own_containers(project: str) -> set[str]:
    """Classes whose source starts a container of their own, or joins a collection other than the
    shared host's (whose fixture starts one)."""
    heavy = set()
    for path in pathlib.Path(project).rglob("*.cs"):
        text = path.read_text(encoding="utf-8", errors="ignore")
        if "ContainerBuilder" in text or re.search(r'\[Collection\("(?!Integration Tests")', text):
            heavy.update(re.findall(r"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)", text))
    return heavy


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--project", required=True)
    p.add_argument("--shards", type=int, required=True)
    p.add_argument("--index", type=int, required=True, help="1-based")
    p.add_argument("--filter", required=True, help="the tier's filter, applied to every shard")
    a = p.parse_args()
    if not 1 <= a.index <= a.shards:
        sys.exit(f"::error::shard index {a.index} is outside 1..{a.shards}")

    names = classes(a.project)
    heavy = own_containers(a.project)
    ordered = [c for c in names if c.rsplit(".", 1)[-1] in heavy] + [c for c in names if c.rsplit(".", 1)[-1] not in heavy]
    named = [[c for i, c in enumerate(ordered) if i % a.shards == s] for s in range(a.shards - 1)]
    # The trailing dot keeps Foo from also matching FooBar.
    if a.index < a.shards:
        mine = named[a.index - 1]
        if not mine:
            sys.exit(f"::error::shard {a.index} of {a.shards} got no classes; use fewer shards")
        selection = "|".join(f"FullyQualifiedName~{c}." for c in mine)
    else:
        others = [c for shard in named for c in shard]
        selection = "&".join(f"FullyQualifiedName!~{c}." for c in others) or "FullyQualifiedName~."
    print(f"({a.filter})&({selection})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
