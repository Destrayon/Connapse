#!/usr/bin/env bash
# Runs eval suites under machine-friendly limits, for long runs on a shared desktop: containers capped
# at 2 CPUs (Ollama 4 GB, Postgres and MinIO 2 GB each), Ollama on CPU only, 2 ingestion workers and
# 1 embedding request in flight, one suite at a time, nothing left running between suites. Start it at
# BelowNormal priority (the dotnet processes inherit it) after `dotnet build -c Release tests/Connapse.Eval`:
#   eval/tools/run_light.sh v1 enterprise-v1
# Latency from these runs compares only with runs made under the same limits.
cd "$(dirname "$0")/../.." || exit 1
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
  tests/Connapse.Eval/bin/Release/net10.0/Connapse.Eval.exe run --suite "$suite" --config hybrid > "${EVAL_LOG_DIR:-eval/runs}/light-$suite.log" 2>&1
  echo "=== $suite exit $? $(date -u +%FT%TZ)"
  stop_ollama
done
