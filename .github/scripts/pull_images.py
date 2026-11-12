"""Pulls CI's container images so that one registry hiccup doesn't fail a job (CON-338).

A 502 from Docker Hub while pulling pgvector/pgvector:pg17 failed mcp-conformance, and with it
build-and-test, on a PR that passed on rerun (run 43180157020). Each image is pulled up to ATTEMPTS
times with growing waits between tries, as Docker's own troubleshooting guide asks of automated
scripts. A Docker Hub image that still fails is pulled from Google's cache of Docker Hub,
mirror.gcr.io, and tagged with its Docker Hub name, so `docker run` and Testcontainers find it
locally and don't pull it again. The mirror holds only popular images and is a fallback, never
the first try: its copy can lag Docker Hub's tag.

Images are pulled in parallel. The exit code is the number of images that could not be pulled.

Usage: pull_images.py IMAGE [IMAGE ...]
"""
import concurrent.futures
import subprocess
import sys
import time

ATTEMPTS = 3
BACKOFF_SECONDS = (5, 15)  # the waits before the second and third tries
MIRROR = "mirror.gcr.io"


def run(args: list[str]) -> tuple[int, str]:
    proc = subprocess.run(args, capture_output=True, text=True)
    return proc.returncode, (proc.stdout + proc.stderr).strip()


def docker_hub_name(image: str) -> str | None:
    """The image's path on Docker Hub, or None when it lives on another registry."""
    first, sep, _ = image.partition("/")
    if sep and ("." in first or ":" in first or first == "localhost"):
        if first not in ("docker.io", "index.docker.io", "registry-1.docker.io"):
            return None
        image = image[len(first) + 1:]
    return image if "/" in image else "library/" + image


def pull(image: str, sleep=time.sleep, runner=run, log=print) -> bool:
    for attempt in range(1, ATTEMPTS + 1):
        code, out = runner(["docker", "pull", "-q", image])
        if code == 0:
            log(f"{image}: pulled" + (f" on try {attempt}" if attempt > 1 else ""))
            return True
        log(f"{image}: try {attempt} of {ATTEMPTS} failed: {out}")
        if attempt < ATTEMPTS:
            sleep(BACKOFF_SECONDS[attempt - 1])

    hub = docker_hub_name(image)
    if hub is None:
        return False
    mirrored = f"{MIRROR}/{hub}"
    code, out = runner(["docker", "pull", "-q", mirrored])
    if code == 0:
        code, out = runner(["docker", "tag", mirrored, image])
        if code == 0:
            log(f"{image}: pulled from {MIRROR}")
            return True
    log(f"{image}: {MIRROR} failed too: {out}")
    return False


def main(images: list[str]) -> int:
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, len(images))) as pool:
        results = list(pool.map(lambda i: pull(i, log=lambda m: print(m, flush=True)), images))
    failed = [i for i, ok in zip(images, results) if not ok]
    for image in failed:
        print(f"::warning::could not pull {image}", flush=True)
    return len(failed)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    sys.exit(main(sys.argv[1:]))
