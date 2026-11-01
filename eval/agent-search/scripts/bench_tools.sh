#!/usr/bin/env bash
# Times grep, ripgrep and ast-grep on the corpus and its 30x copy. Median of 5 warm runs.
set -u
BIN=/e/tmp/con-142/bin
OUT=/e/tmp/con-142/results/bench_tools.tsv
mkdir -p /e/tmp/con-142/results
echo -e "tool\tcorpus\tquery\tmedian_ms\tmatches" > "$OUT"

median() { sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }

run() { # tool corpus label cmd...
  local tool=$1 corpus=$2 label=$3; shift 3
  "$@" > /dev/null 2>&1   # warm the file cache
  local times=() n
  for i in 1 2 3 4 5; do
    local s=$(date +%s%N)
    n=$("$@" 2>/dev/null | wc -l)
    local e=$(date +%s%N)
    times+=($(( (e - s) / 1000000 )))
  done
  local m=$(printf '%s\n' "${times[@]}" | median)
  echo -e "$tool\t$corpus\t$label\t$m\t$n" >> "$OUT"
}

for C in corpus big; do
  D=/e/tmp/con-142/$C
  run grep "$C" identifier grep -rn 'VectorColumnManager' "$D"
  run rg   "$C" identifier "$BIN/rg.exe" -n 'VectorColumnManager' "$D"
  run grep "$C" error-message grep -rn 'Unsupported file extension' "$D"
  run rg   "$C" error-message "$BIN/rg.exe" -n 'Unsupported file extension' "$D"
  run grep "$C" regex grep -rnE 'class [A-Za-z]+Connector\b' "$D"
  run rg   "$C" regex "$BIN/rg.exe" -n 'class \w+Connector\b' "$D"
  run grep "$C" ignore-case grep -rni 'rate limit' "$D"
  run rg   "$C" ignore-case "$BIN/rg.exe" -ni 'rate limit' "$D"
  run grep "$C" common grep -rn 'async Task' "$D"
  run rg   "$C" common "$BIN/rg.exe" -n 'async Task' "$D"
  run ast-grep "$C" structural "$BIN/ast-grep.exe" run --lang csharp -p 'class $N : IDocumentParser { $$$ }' "$D"
  run rg   "$C" structural "$BIN/rg.exe" -n 'class \w+\s*:\s*IDocumentParser' "$D"
done
cat "$OUT"
