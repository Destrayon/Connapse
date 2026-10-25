"""Writes the scripted project histories for the memory-question set (CON-38).

    python -I make_histories.py <out-dir>

Ten fictional projects run from 2026-01-05 to 2026-06-26. Each has eight facts (database, region, lead,
CI, cache, rate limit, alert channel, release) whose values change on known dates through tickets.
Some changes are announced by a plan first, some revert to an earlier value, and unrelated work fills
the gaps. Events on one day form one work session, which is what a note is later written from.

Writes histories.jsonl (every event), sessions.jsonl (each session's log, as the note writer sees
it) and timeline.json (each fact's values and the date and ticket each took effect). The timeline is
the answer key; it is fixed by the seed before any note exists.
"""
import json
import random
import sys
from datetime import date, timedelta
from pathlib import Path

SEED = 38
START, END = date(2026, 1, 5), date(2026, 6, 26)
PROJECTS = [("Halcyon", "HAL"), ("Bramblewood", "BRM"), ("Quillfeather", "QFT"), ("Marrowgate", "MRW"), ("Tidewater", "TDW"),
            ("Emberline", "EMB"), ("Saltmarsh", "SLT"), ("Kestrel Row", "KST"), ("Ashgrove", "ASH"), ("Lanternfish", "LNF")]
PEOPLE = ["Odessa Venn", "Tamsin Quarrell", "Ilario Brandtmoss", "Wren Halloway", "Caspian Oduya", "Priya Laskin", "Jonah Felgate",
          "Marisol Ternby", "Anouk Vasquell", "Ruben Ashcombe", "Signe Morrow-Dale", "Teodor Klyne", "Haruki Belmonde", "Ines Coldwater"]
VALUES = {
    "database": ["PostgreSQL 15", "PostgreSQL 16", "MySQL 8.0", "MariaDB 11", "CockroachDB 23.2", "SQLite 3.45"],
    "region": ["eu-west-1", "eu-central-1", "us-east-2", "us-west-2", "ap-southeast-1", "ca-central-1"],
    "lead": PEOPLE,
    "ci": ["GitHub Actions", "GitLab CI", "Buildkite", "Jenkins", "CircleCI"],
    "cache": ["Redis 7", "Valkey 8", "Memcached 1.6", "no cache"],
    "rate_limit": ["100 requests/minute", "250 requests/minute", "500 requests/minute", "1000 requests/minute"],
}
NAMES = {"database": "database", "region": "deploy region", "lead": "tech lead", "ci": "CI system", "cache": "cache",
         "rate_limit": "public API rate limit", "alert": "alert channel", "release": "current release"}
REASONS = ["to cut costs", "after the load test", "to match the platform team's standard", "for latency to the main customer",
           "after an outage review", "because the old one is end-of-life", "to simplify on-call", "at the customer's request"]
NOISE = ["Fixed a flaky test in the export job", "Wrote the runbook for restoring a backup", "Triaged three bug reports from support",
         "Paired on the billing webhook retry logic", "Cleaned up unused feature flags", "Reviewed the security questionnaire answers",
         "Profiled the search endpoint; nothing alarming", "Updated the onboarding docs for new contributors",
         "Sprint planning: no scope changes", "Rotated the staging TLS certificate", "Investigated a memory spike in the worker; a leaky cache key"]


def days(rng: random.Random, n: int, gap: int) -> list[date]:
    """n change dates, at least gap days apart, after kickoff week."""
    span = (END - START).days
    while True:
        picked = sorted(rng.sample(range(10, span), n))
        if all(b - a >= gap for a, b in zip(picked, picked[1:])):
            return [START + timedelta(d) for d in picked]


def project(rng: random.Random, name: str, code: str) -> tuple[list[dict], dict]:
    ticket = iter(range(rng.randint(3, 20), 10_000, 1))
    slug = code.lower()
    values = {f: list(v) for f, v in VALUES.items()}
    values["alert"] = [f"#{slug}-alerts", f"#{slug}-pager", f"#{slug}-oncall", f"#platform-alerts"]
    current = {f: rng.choice(v) for f, v in values.items()}
    major, minor = rng.randint(0, 2), rng.randint(1, 9)
    current["release"] = f"v{major}.{minor}.0"
    timeline = {f: [{"from": START.isoformat(), "value": v, "ticket": None}] for f, v in current.items()}
    events = [{"date": START.isoformat(), "kind": "kickoff", "ticket": None, "fact": None, "from": None, "to": None,
               "text": f"Kickoff for {name}. Starting setup: " + "; ".join(f"{NAMES[f]} {v}" for f, v in current.items()) + "."}]

    changes = []
    for fact in list(VALUES) + ["alert"]:
        n = rng.choices([0, 1, 2, 3], weights=[15, 35, 30, 20])[0]
        changes += [(d, fact) for d in days(rng, n, 14)] if n else []
    changes += [(d, "release") for d in days(rng, rng.randint(2, 4), 21)]

    for when, fact in sorted(changes):
        old = current[fact]
        if fact == "release":
            m, n, _ = (int(x) for x in old[1:].split("."))
            new = f"v{m}.{n + 1}.0" if rng.random() < 0.8 else f"v{m + 1}.0.0"
        else:
            earlier = [s["value"] for s in timeline[fact][:-1] if s["value"] != old]
            pool = [v for v in values[fact] if v != old]
            # A quarter of the changes go back to a value the fact had before.
            new = rng.choice(earlier) if earlier and rng.random() < 0.25 else rng.choice(pool)
        t = f"{code}-{next(ticket)}"
        reason = rng.choice(REASONS)
        if fact != "release" and rng.random() < 0.3:
            ahead = when - timedelta(rng.randint(5, 20))
            # After the fact's last change, so the plan's "from" is the value in effect when it is made.
            if ahead > date.fromisoformat(timeline[fact][-1]["from"]):
                events.append({"date": ahead.isoformat(), "kind": "plan", "ticket": t, "fact": fact, "from": old, "to": new,
                               "text": f"Opened {t}: plan to move the {NAMES[fact]} from {old} to {new} {reason}; not done yet."})
        text = (f"Released {new} ({t})." if fact == "release"
                else f"{t} done: the {NAMES[fact]} changed from {old} to {new} {reason}.")
        events.append({"date": when.isoformat(), "kind": "change", "ticket": t, "fact": fact, "from": old, "to": new, "text": text})
        timeline[fact].append({"from": when.isoformat(), "value": new, "ticket": t})
        current[fact] = new

    for when in days(rng, 8, 3):
        t = f"{code}-{next(ticket)}"
        events.append({"date": when.isoformat(), "kind": "noise", "ticket": t, "fact": None, "from": None, "to": None,
                       "text": f"{rng.choice(NOISE)} ({t})."})

    events.sort(key=lambda e: (e["date"], ["kickoff", "plan", "change", "noise"].index(e["kind"])))
    for e in events:
        e["project"], e["code"] = name, code
    return events, timeline


def main() -> int:
    out = Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    rng = random.Random(SEED)
    events, timelines, sessions = [], {}, []
    for name, code in PROJECTS:
        evs, timelines[code] = project(rng, name, code)
        events += evs
        for day in sorted({e["date"] for e in evs}):
            todays = [e for e in evs if e["date"] == day]
            sessions.append({"session": f"{code}-{day}", "project": name, "code": code, "date": day,
                             "log": "\n".join(f"- {e['text']}" for e in todays)})
    with (out / "histories.jsonl").open("w", encoding="utf-8", newline="\n") as f:
        f.writelines(json.dumps(e, ensure_ascii=False) + "\n" for e in events)
    with (out / "sessions.jsonl").open("w", encoding="utf-8", newline="\n") as f:
        f.writelines(json.dumps(s, ensure_ascii=False) + "\n" for s in sessions)
    (out / "timeline.json").write_text(json.dumps(timelines, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(len(events), "events,", len(sessions), "sessions,", sum(len(v) - 1 for t in timelines.values() for v in t.values()), "changes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
