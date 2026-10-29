"""PrPipeline_WallTimeUnder6Min: the proof check for epic #783, problem P10 (#789).

How long the PR pipeline takes on its own, read from the GitHub API: the longest job that runs in
parallel, from its start to its finish, plus the job running this script, from its start to now. Time
spent waiting for a free runner isn't counted, because it depends on how much other CI is running (a
stack of PRs queues behind itself), not on this pipeline. Each job's latest attempt is used, so a
re-run of one job is measured like a first run. The raw wall time is printed alongside, for reference.

A step that only waits on a third-party service, such as the Codecov upload or a container registry, is
counted up to its --network-step allowance and no further (CON-127): one slow response from that
service is the runner's network, not this pipeline, and it used to fail PRs whose tests all passed. The
time over the allowance is printed as a warning, so a service that stays slow is still seen.

A setup step, such as checkout, restore, build or starting Ollama, is counted the same way, up to its
job's allowance in the --setup-allowances file (CON-141): that step's median over 30 green runs, so a
runner that is slow at setup counts as a usual one. Runners of the same image vary by tens of seconds
there (a build that is 123 s at the median took 163 s on #1024's first run), and that alone put PRs
whose tests had not changed over the budget. Tests and checks are counted in full: those are what a
PR's code makes slower. A setup step's time over its allowance is printed, without a warning, since
about half of all runs have one.

Usage (in a workflow step, with GH_TOKEN set; run it as the job's last step):
  pipeline_wall.py --max-seconds 360 --current build-and-test --network-step "Upload coverage reports=15" \
    --setup-allowances .github/setup-allowances.json
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


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def allowance(s: str) -> tuple[str, float]:
    """A --network-step value, "<step name>=<seconds>"."""
    name, sep, seconds = s.rpartition("=")
    try:
        if not sep or not name:
            raise ValueError
        return name, float(seconds)
    except ValueError:
        raise argparse.ArgumentTypeError(f"expected <step name>=<seconds>, got {s!r}") from None


def counted(j: dict, allowances: dict[str, float]) -> tuple[float, list[tuple[str, float, float]]]:
    """A finished job's run time with each network step capped at its allowance, and the steps that went over."""
    seconds = (at(j["completed_at"]) - at(j["started_at"])).total_seconds()
    over = []
    for s in j.get("steps") or []:
        if s["name"] not in allowances or not s.get("started_at") or not s.get("completed_at"):
            continue
        took = (at(s["completed_at"]) - at(s["started_at"])).total_seconds()
        if took > allowances[s["name"]]:
            seconds -= took - allowances[s["name"]]
            over.append((s["name"], took, allowances[s["name"]]))
    return seconds, over


def job_allowances(name: str, network: dict[str, float], setup: dict[str, dict[str, float]]) -> dict[str, float]:
    """The allowances for one job: every network step's, and its own setup steps'. A matrix job such as
    "integration (2)" uses the allowances of "integration"."""
    return network | setup.get(name.split(" (")[0], {})


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--max-seconds", type=float, required=True)
    p.add_argument("--current", required=True, help="the name of the job running this script")
    p.add_argument("--network-step", type=allowance, action="append", default=[], metavar="NAME=SECONDS",
                   help="a step that only waits on a third-party service, counted up to SECONDS")
    p.add_argument("--setup-allowances", metavar="FILE",
                   help='JSON {"<job>": {"<setup step>": seconds}}: each setup step counted up to its seconds')
    a = p.parse_args()
    network = dict(a.network_step)
    setup = {}
    if a.setup_allowances:
        with open(a.setup_allowances, encoding="utf-8") as f:
            setup = json.load(f)

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
    now = utcnow()

    def runtime(j: dict) -> float:
        return (at(j["completed_at"]) - at(j["started_at"])).total_seconds()

    def counted_job(j: dict) -> tuple[float, list[tuple[str, float, float]]]:
        return counted(j, job_allowances(j["name"], network, setup))

    start = min(at(j["started_at"]) for j in done)
    for j in sorted(done, key=lambda j: j["started_at"]):
        seconds, over = counted_job(j)
        print(f"  {(at(j['started_at']) - start).total_seconds():5.0f} s  ran {runtime(j):4.0f} s"
              f"  counted {seconds:4.0f} s  {j['name']} ({j['conclusion']})")
        for name, took, allowed in over:
            if name in network:
                print(f"::warning::{j['name']}: {name} took {took:.0f} s; counted {allowed:g} s, the rest waited on a slow third-party service")
            else:
                print(f"           {name} took {took:.0f} s; counted {allowed:g} s, its usual setup time")
    own = (now - at(current["started_at"])).total_seconds()
    longest = max(done, key=lambda j: counted_job(j)[0])
    longest_seconds = counted_job(longest)[0]
    pipeline = longest_seconds + own
    print(f"  raw wall time, first start to now: {(now - start).total_seconds():.0f} s")
    print(f"  pipeline: {longest['name']} {longest_seconds:.0f} s + {a.current} {own:.0f} s = {pipeline:.0f} s")
    if pipeline > a.max_seconds:
        print(f"::error::PrPipeline_WallTimeUnder6Min: the pipeline takes {pipeline:.0f} s, over its {a.max_seconds:g} s budget")
        return 1
    print(f"PrPipeline_WallTimeUnder6Min: {pipeline:.0f} s of {a.max_seconds:g} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
