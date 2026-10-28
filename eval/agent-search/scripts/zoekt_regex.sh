#!/usr/bin/env bash
# Finds the Zoekt query form that runs the connector-class regex.
MSYS_NO_PATHCONV=1 docker run -d --rm --name con142-zoekt-web --cpus 2 --memory 4g -p 6071:6070 \
  -v "E:/tmp/con-142/zoekt/idx-corpus:/data/index" sourcegraph/zoekt-webserver:latest \
  zoekt-webserver -index /data/index -rpc -listen :6070 > /dev/null
for i in $(seq 1 60); do curl -s -o /dev/null http://localhost:6071/healthz && break; sleep 1; done
for q in 'class\s[A-Za-z]+Connector\b' 'regex:class\s[A-Za-z]+Connector' '"class [A-Za-z]+Connector"'; do
  body=$(python -c 'import json,sys; print(json.dumps({"Q": sys.argv[1]}))' "$q")
  echo "$q -> $(curl -s -X POST -d "$body" http://localhost:6071/api/search | python -c 'import json,sys; r=json.load(sys.stdin); r=r.get("Result",r); print(r.get("FileCount", r))')"
done
docker rm -f con142-zoekt-web > /dev/null
