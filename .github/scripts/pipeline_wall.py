"""PrPipeline_WallTimeUnder6Min: the proof check for epic #783, problem P10 (#789).

Wall time of this workflow run attempt, from the first job's start to the last job's finish, read
from the GitHub API. Queue time before the first job starts doesn't count; time a job waits for a
runner after that does, because the person waiting on the PR waits for it too. The job running this
script hasn't finished, so it counts from its start to now: run this as the job's last step.

Usage (in a workflow step, with GH_TOKEN set):
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
    repo, run, attempt = os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_RUN_ID"], os.environ.get("GITHUB_RUN_ATTEMPT", "1")
    url = f"https://api.github.com/repos/{repo}/actions/runs/{run}/attempts/{attempt}/jobs?per_page=100"
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
        print(f"::error::jobs without a start and finish time: {', '.join(unfinished)}; the wall time can't be measured")
        return 1
    if not done:
        print("::error::no jobs to measure; a check that measures nothing proves nothing")
        return 1

    current = next((j for j in everything if j["name"] == a.current), None)
    if current is None or not current.get("started_at"):
        print(f"::error::no job named {a.current} with a start time in this run attempt")
        return 1
    done.append({**current, "completed_at": datetime.now(timezone.utc).isoformat(), "conclusion": "running"})
    start = min(at(j["started_at"]) for j in done)
    end = max(at(j["completed_at"]) for j in done)
    for j in sorted(done, key=lambda j: j["started_at"]):
        s, e = at(j["started_at"]), at(j["completed_at"])
        print(f"  {(s - start).total_seconds():5.0f} s +{(e - s).total_seconds():4.0f} s  {j['name']} ({j['conclusion']})")
    wall = (end - start).total_seconds()
    if wall > a.max_seconds:
        print(f"::error::PrPipeline_WallTimeUnder6Min: the pipeline took {wall:.0f} s wall, over its {a.max_seconds:g} s budget")
        return 1
    print(f"PrPipeline_WallTimeUnder6Min: {wall:.0f} s of {a.max_seconds:g} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
