#!/usr/bin/env bash
# Times Zoekt queries over HTTP for each index. Median of 5 warm runs. Removes the container afterwards.
set -u
OUT=/e/tmp/con-142/results/bench_zoekt.tsv
echo -e "corpus\tquery\tmedian_ms\tfile_matches" > $OUT
median() { sort -n | awk '{a[NR]=$1} END {print a[int((NR+1)/2)]}'; }
declare -A Q=(
  [identifier]='VectorColumnManager'
  [error-message]='"Unsupported file extension"'
  [regex]='class\s[A-Za-z]+Connector\b'
  [ignore-case]='case:no "rate limit"'
  [common]='"async Task"'
)
for C in corpus big; do
  MSYS_NO_PATHCONV=1 docker run -d --rm --name con142-zoekt-web --cpus 2 --memory 4g -p 6071:6070 \
    -v "E:/tmp/con-142/zoekt/idx-$C:/data/index" sourcegraph/zoekt-webserver:latest \
    zoekt-webserver -index /data/index -rpc -listen :6070 > /dev/null
  for i in $(seq 1 60); do curl -s -o /dev/null http://localhost:6071/healthz && break; sleep 1; done
  for k in "${!Q[@]}"; do
    body=$(python -c 'import json,sys; print(json.dumps({"Q": sys.argv[1], "Opts": {"MaxDocDisplayCount": 1000}}))' "${Q[$k]}")
    curl -s -X POST -d "$body" http://localhost:6071/api/search > /dev/null
    times=()
    for r in 1 2 3 4 5; do
      s=$(date +%s%N)
      resp=$(curl -s -X POST -d "$body" http://localhost:6071/api/search)
      e=$(date +%s%N)
      times+=($(( (e - s) / 1000000 )))
    done
    n=$(echo "$resp" | python -c 'import json,sys; r=json.load(sys.stdin)["Result"]; print(r.get("FileCount", len(r.get("Files") or [])))')
    echo -e "$C\t$k\t$(printf '%s\n' "${times[@]}" | median)\t$n" >> $OUT
  done
  docker rm -f con142-zoekt-web > /dev/null
done
cat $OUT
