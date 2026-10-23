"""PrPipeline_WallTimeUnder6Min: the proof check for epic #783, problem P10 (#789).

How long the PR pipeline takes on its own, read from the GitHub API: the longest job that runs in
parallel, from its start to its finish, plus the job running this script, from its start to now. Time
spent waiting for a free runner isn't counted, because it depends on how much other CI is running (a
stack of PRs queues behind itself), not on this pipeline. Each job's latest attempt is used, so a
re-run of one job is measured like a first run. The raw wall time is printed alongside, for reference.

Usage (in a workflow step, with GH_TOKEN set; run it as the job's last step):
  pipeline_wall.py --max-seconds 360 --current build-and-test
"""
import argparse
import json
import os
import sys
import time
import urllib.request
from datetime import datetime, timezone


def jobs() -> list[dict]:
    """The latest attempt of every job in this run."""
    repo, run = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_RUN_ID"]
    url = f"https://api.github.com/repos/{repo}/actions/runs/{run}/jobs?filter=latest&per_page=100"
    req = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {os.environ['GH_TOKEN']}",
        "Accept": "application/vnd.github+json",
    })
    with urllib.request.urlopen(req) as r:
        return json.load(r)["jobs"]


def at(s: str) -> datetime:
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--max-seconds", type=float, required=True)
    p.add_argument("--current", required=True, help="the name of the job running this script")
    a = p.parse_args()

    # A job that has just finished can still read as running for a few seconds after the jobs
    # waiting on it start, so re-read until every job has its finish time or a minute has passed.
    deadline = time.monotonic() + 60
    while True:
        everything = jobs()
        done = [j for j in everything if j["name"] != a.current]
        unfinished = [j["name"] for j in done if not j.get("started_at") or not j.get("completed_at")]
        if not unfinished or time.monotonic() > deadline:
            break
        time.sleep(5)
    if unfinished:
        print(f"::error::jobs without a start and finish time: {', '.join(unfinished)}; the pipeline time can't be measured")
        return 1
    if not done:
        print("::error::no jobs to measure; a check that measures nothing proves nothing")
        return 1

    current = next((j for j in everything if j["name"] == a.current), None)
    if current is None or not current.get("started_at"):
        print(f"::error::no job named {a.current} with a start time in this run")
        return 1
    now = datetime.now(timezone.utc)

    def runtime(j: dict) -> float:
        return (at(j["completed_at"]) - at(j["started_at"])).total_seconds()

    start = min(at(j["started_at"]) for j in done)
    for j in sorted(done, key=lambda j: j["started_at"]):
        print(f"  {(at(j['started_at']) - start).total_seconds():5.0f} s  ran {runtime(j):4.0f} s  {j['name']} ({j['conclusion']})")
    own = (now - at(current["started_at"])).total_seconds()
    longest = max(done, key=runtime)
    pipeline = runtime(longest) + own
    print(f"  raw wall time, first start to now: {(now - start).total_seconds():.0f} s")
    print(f"  pipeline: {longest['name']} {runtime(longest):.0f} s + {a.current} {own:.0f} s = {pipeline:.0f} s")
    if pipeline > a.max_seconds:
        print(f"::error::PrPipeline_WallTimeUnder6Min: the pipeline takes {pipeline:.0f} s, over its {a.max_seconds:g} s budget")
        return 1
    print(f"PrPipeline_WallTimeUnder6Min: {pipeline:.0f} s of {a.max_seconds:g} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
