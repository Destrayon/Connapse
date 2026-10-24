#!/usr/bin/env bash
# CON-30 baselines under the machine-friendly limits (PM, 2026-10-24): containers capped at 2 CPUs
# (Ollama 4 GB, Postgres and MinIO 2 GB each), Ollama on CPU only, 2 ingestion workers and 1 embedding
# request in flight, one suite at a time, nothing left running between suites. The caller starts this
# at BelowNormal priority, which the dotnet processes inherit.
cd /d/CodeProjects/Connapse/.claude/worktrees/con-e2-graphrag-research-600623 || exit 1
export Knowledge__Embedding__BaseUrl=http://localhost:11435
export Knowledge__Embedding__MaxConcurrentIngestionRequests=1
export Hangfire__IngestionWorkerCount=2
export Hangfire__SummaryWorkerCount=1
export CONNAPSE_EVAL_CONTAINER_CPUS=2
export CONNAPSE_EVAL_CONTAINER_MEMORY_MB=2048

stop_ollama() { docker rm -f eval-ollama-11435 >/dev/null 2>&1; }
trap stop_ollama EXIT

for suite in "$@"; do
  echo "=== $suite start $(date -u +%FT%TZ)"
  stop_ollama
  docker run -d --rm --name eval-ollama-11435 -p 11435:11434 --cpus 2 --memory 4g --memory-swap 4g \
    -e OLLAMA_NUM_PARALLEL=1 -e OLLAMA_MAX_LOADED_MODELS=1 -v eval-ollama:/root/.ollama ollama/ollama >/dev/null
  until curl -s localhost:11435/api/tags >/dev/null; do sleep 1; done
  tests/Connapse.Eval/bin/Release/net10.0/Connapse.Eval.exe run --suite "$suite" --config hybrid > "/d/tmp/con30/light-$suite.log" 2>&1
  echo "=== $suite exit $? $(date -u +%FT%TZ)"
  stop_ollama
done
