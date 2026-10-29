#!/usr/bin/env bash
# Builds Zoekt indexes for corpus and big (30x) in a capped container; prints build time and index size.
set -eu
Z=/e/tmp/con-142/zoekt
mkdir -p $Z/idx-corpus $Z/idx-big
cd /e/tmp/con-142
tar -cf $Z/corpus.tar -C corpus .
tar -cf $Z/big.tar -C big .
for C in corpus big; do
  s=$(date +%s%N)
  MSYS_NO_PATHCONV=1 docker run --rm --name con142-zoekt-index --cpus 2 --memory 4g \
    -v "E:/tmp/con-142/zoekt:/z" --entrypoint zoekt-archive-index sourcegraph/zoekt-indexserver:latest \
    -disable_ctags -incremental=false -name $C -commit 0000000000000000000000000000000000000000 -branch main -index /z/idx-$C /z/$C.tar > $Z/index-$C.log 2>&1
  e=$(date +%s%N)
  echo "$C build_ms=$(( (e - s) / 1000000 )) index_bytes=$(du -sb $Z/idx-$C | cut -f1) tar_bytes=$(stat -c %s $Z/$C.tar)"
done
