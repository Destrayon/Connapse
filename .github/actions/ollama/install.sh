#!/usr/bin/env bash
# Installs the CPU-only part of Ollama $OLLAMA_VERSION into $OLLAMA_DIST (bin/ollama and lib/ollama), trying
# three sources in turn so one host's outage doesn't fail the job (CON-286: ollama.com answered 504):
# ollama.com, the same file from the GitHub release, then the ollama/ollama image on Docker Hub.
# The CUDA libraries are skipped: they are 2.4 GB of the tarball and the runners have no GPU.
set -uo pipefail

version="${OLLAMA_VERSION:?}"
dist="${OLLAMA_DIST:?}"
file="ollama-linux-amd64.tar.zst"
tmp="$dist.partial"

from_url() {
  curl -fsSL --retry 3 --retry-all-errors --connect-timeout 20 --max-time 900 "$1" \
    | tar --zstd -x -C "$tmp" --exclude='lib/ollama/cuda_*' --exclude='lib/ollama/rocm*' \
        --exclude='lib/ollama/vulkan*' --exclude='lib/ollama/mlx*'
}

from_image() {
  local id
  docker pull -q "ollama/ollama:$version" || return 1
  id=$(docker create "ollama/ollama:$version") || return 1
  mkdir -p "$tmp/bin" "$tmp/lib"
  docker cp "$id:/usr/bin/ollama" "$tmp/bin/ollama" && docker cp "$id:/usr/lib/ollama" "$tmp/lib/ollama"
  local status=$?
  docker rm -f "$id" > /dev/null
  rm -rf "$tmp"/lib/ollama/cuda_* "$tmp"/lib/ollama/rocm* "$tmp"/lib/ollama/vulkan* "$tmp"/lib/ollama/mlx*
  docker image rm -f "ollama/ollama:$version" > /dev/null
  return $status
}

for source in "https://ollama.com/download/$file?version=$version" \
              "https://github.com/ollama/ollama/releases/download/v$version/$file" \
              "docker:ollama/ollama:$version"; do
  rm -rf "$tmp" && mkdir -p "$tmp"
  echo "Installing Ollama $version from $source"
  if [[ "$source" == docker:* ]]; then from_image; else from_url "$source"; fi \
    && "$tmp/bin/ollama" --version > /dev/null 2>&1 \
    && { rm -rf "$dist"; mv "$tmp" "$dist"; du -sh "$dist"; exit 0; }
  echo "::warning::could not install Ollama from $source; trying the next source"
done

echo "::error::Ollama $version could not be installed from any source"
exit 1
