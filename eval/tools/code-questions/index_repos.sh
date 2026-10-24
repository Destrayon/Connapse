#!/usr/bin/env bash
# Indexes every repo in eval/datasets/code-questions/repos.json (CON-35): a clone at the pinned commit,
# then the language's compiler-grade indexer, one repo at a time. Run it at low priority on a shared
# machine (from PowerShell with PriorityClass BelowNormal); each indexer's memory is capped.
#
#   eval/tools/code-questions/index_repos.sh <work-dir> [repo-name ...]
#
# Writes <work-dir>/src/<name> (the clone) and <work-dir>/out/<name>.scip, or <name>.roslyn.json for a
# C# repo. Delete <work-dir> when done.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repos="$here/../../datasets/code-questions/repos.json"
work=${1:?usage: index_repos.sh <work-dir> [repo-name ...]}
shift
mkdir -p "$work/src" "$work/out" "$work/tools"
work=$(cd "$work" && pwd)

field() { python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])); print(eval(sys.argv[2], {"d": d}))' "$repos" "$1"; }

# The indexers, at the versions repos.json pins, installed into the work directory only.
ts_ver=$(field 'd["indexers"]["scip-typescript"]')
py_ver=$(field 'd["indexers"]["scip-python"]')
[ -f "$work/tools/roslyn/RoslynIndex.dll" ] \
  || dotnet build "$here/RoslynIndex/RoslynIndex.csproj" -c Release -o "$work/tools/roslyn" -v q -nologo
[ -d "$work/tools/node_modules/@sourcegraph/scip-typescript" ] \
  || npm install --prefix "$work/tools" --ignore-scripts --no-audit --no-fund \
       "@sourcegraph/scip-typescript@$ts_ver" "@sourcegraph/scip-python@$py_ver"

names=("$@")
[ ${#names[@]} -gt 0 ] || mapfile -t names < <(field '"\n".join(r["name"] for r in d["repos"])')

for name in "${names[@]}"; do
  q="[r for r in d['repos'] if r['name'] == '$name'][0]"
  url=$(field "$q['url']"); commit=$(field "$q['commit']"); indexer=$(field "$q['indexer']"); target=$(field "$q['target']")
  src="$work/src/$name"; out="$work/out/$name.scip"
  echo "== $name @ $commit ($indexer)"

  if [ ! -d "$src/.git" ]; then
    git init -q "$src"
    git -C "$src" fetch -q --depth 1 "$url" "$commit"
    git -C "$src" -c advice.detachedHead=false checkout -q FETCH_HEAD
  fi
  [ "$(git -C "$src" rev-parse HEAD)" = "$commit" ] || { echo "$name is not at $commit" >&2; exit 1; }

  case "$indexer" in
    roslyn)
      # Roslyn loads the solution in this process; 4 GB is enough for Connapse and caps it there. NuGet's
      # audit is off: a pinned repo's old test dependencies turn its warnings into restore errors.
      out="$work/out/$name.roslyn.json"
      (cd "$src" && export NuGetAudit=false DOTNET_GCHeapHardLimit=0x100000000 && dotnet restore "$target" -v q \
        && dotnet "$work/tools/roslyn/RoslynIndex.dll" "$src" "$target" "$out")
      ;;
    scip-typescript)
      # Without a lockfile the dependencies float; they only give types to symbols outside the repo,
      # which no question asks about.
      (cd "$src" && { if [ -f package-lock.json ]; then npm ci --ignore-scripts --no-audit --no-fund; \
                      else npm install --ignore-scripts --no-audit --no-fund --no-package-lock; fi; } \
        && node --max-old-space-size=2048 "$work/tools/node_modules/@sourcegraph/scip-typescript/dist/src/main.js" index --output "$out")
      ;;
    scip-python)
      # In a Linux container: on Windows scip-python 0.6.6 builds a RegExp from path.sep ("\") and dies
      # at startup. Capped at 2 CPUs and 3 GB; the image's own python3 is the environment it resolves.
      MSYS_NO_PATHCONV=1 docker run --rm --cpus 2 --memory 3g -v "$(cygpath -w "$work" 2>/dev/null || echo "$work"):/w" -w "/w/src/$name" \
        node:22-bookworm bash -c "apt-get -qq update && apt-get -qq install -y python3-pip >/dev/null && npm install -g --ignore-scripts --no-audit --no-fund @sourcegraph/scip-python@$py_ver >/dev/null \
          && NODE_OPTIONS=--max-old-space-size=2048 scip-python index . --project-name $name --project-version $commit --output /w/out/$name.scip"
      ;;
    *) echo "unknown indexer $indexer" >&2; exit 1 ;;
  esac
  ls -l "$out"
done
