"""Builds the memory-question set (CON-38) from the scripted timeline and the notes written from it.

    python -I build_questions.py <dataset-dir>

Reads timeline.json, histories.jsonl and notes/<CODE>/*.md, and writes notes.jsonl (the notes in one
file) and questions.jsonl. Every answer comes from the timeline, never from the notes. The notes decide
only whether a question can be asked: a question is kept when some note holds its answer (its evidence),
so a memory system that reads the notes could answer it. A note that names a value or ticket before the
history introduced it fails the build: its writer saw the future.

Kinds: current (the value now), as_of (the value on a date), changed (what changed between two dates),
ticket (which ticket made a change), ticket_effect (what a ticket changed) and when (the date of a
change). The held-out half is every question about half the projects, chosen by a hash of the seed and
the project code, so no project's notes serve both halves.

A note may also not name a value from the scripted pools that its project never had: that would be
an invented fact, not a memory.
"""
import hashlib
import json
import random
import re
import sys
from datetime import date, timedelta
from pathlib import Path

SEED = 38
END = date(2026, 6, 30)
NAMES = {"database": "database", "region": "deploy region", "lead": "tech lead", "ci": "CI system", "cache": "cache",
         "rate_limit": "public API rate limit", "alert": "alert channel", "release": "current release"}
TICKET = re.compile(r"\b[A-Z]{3}-\d+\b")


def splits(codes: list[str]) -> dict[str, str]:
    """Half the projects held out: the half that sorts first by a hash of the seed and the code."""
    ranked = sorted(codes, key=lambda c: hashlib.sha256(f"{SEED}:{c}".encode()).hexdigest())
    return {c: "heldout" if i < len(ranked) // 2 else "tuning" for i, c in enumerate(ranked)}


def value_at(segments: list[dict], when: date) -> dict:
    return [s for s in segments if date.fromisoformat(s["from"]) <= when][-1]


def main() -> int:
    root = Path(sys.argv[1])
    timeline = json.loads((root / "timeline.json").read_text(encoding="utf-8"))
    events = [json.loads(line) for line in (root / "histories.jsonl").read_text(encoding="utf-8").splitlines()]
    notes = []
    for path in sorted((root / "notes").glob("*/*.md")):
        head, _, body = path.read_text(encoding="utf-8").partition("\n")
        notes.append({"note": path.stem, "code": path.parent.name, "date": head.removeprefix("date:").strip(), "text": body.strip()})
    with (root / "notes.jsonl").open("w", encoding="utf-8", newline="\n") as f:
        f.writelines(json.dumps(n, ensure_ascii=False) + "\n" for n in notes)

    # Every value and ticket a note names must already have appeared in its project's history.
    first_seen = {}
    for e in events:
        for token in [e["ticket"], e["from"], e["to"], *(s["value"] for f in timeline[e["code"]].values() for s in f if e["kind"] == "kickoff" and s["ticket"] is None)]:
            if token:
                first_seen.setdefault((e["code"], token), e["date"])
    known = {}
    for (code, token) in first_seen:
        known.setdefault(code, set()).add(token)
    leaks = []
    for n in notes:
        for token in known[n["code"]]:
            if re.search(rf"(?<![\w.-]){re.escape(token)}(?![\w-])", n["text"]) and first_seen[(n["code"], token)] > n["date"]:
                leaks.append(f"{n['note']} names {token!r}, first seen {first_seen[(n['code'], token)]}")
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from make_histories import VALUES  # noqa: E402
    pool = {v for vs in VALUES.values() for v in vs}
    for n in notes:
        for v in pool - known[n["code"]]:
            if re.search(rf"(?<![\w.-]){re.escape(v)}(?![\w-])", n["text"]):
                leaks.append(f"{n['note']} names {v!r}, which {n['code']}'s history never had")
    if leaks:
        print("notes name what their writer hadn't seen yet:", *leaks, sep="\n  ")
        return 1

    def mentions(code: str, *tokens: str, on: str | None = None, since: str | None = None, until: str | None = None) -> list[str]:
        return [n["note"] for n in notes if n["code"] == code and all(re.search(rf"(?<![\w.-]){re.escape(t)}(?![\w-])", n["text"]) for t in tokens)
                and (on is None or n["date"] == on) and (since is None or n["date"] >= since) and (until is None or n["date"] <= until)]

    split = splits(list(timeline))
    rng = random.Random(SEED)
    questions, dropped = [], 0
    project_names = {e["code"]: e["project"] for e in events}
    for code, facts in timeline.items():
        name = project_names[code]
        found = []

        def add(kind, text, answer, evidence):
            nonlocal dropped
            if not evidence:
                dropped += 1
                return
            found.append({"kind": kind, "question": text, "answer": answer, "evidence": evidence})

        for fact, segments in facts.items():
            noun = NAMES[fact]
            last = segments[-1]
            add("current", f"What is {name}'s {noun} now?", {"value": last["value"], "since": last["from"], "ticket": last["ticket"]},
                mentions(code, last["value"], since=last["from"]))

            # A day inside an earlier stretch when one exists, so "as of" differs from "now".
            stretch = rng.choice(segments[:-1] or segments)
            start = date.fromisoformat(stretch["from"])
            nxt = segments[segments.index(stretch) + 1]["from"] if stretch is not last else END.isoformat()
            asked = start + timedelta(rng.randint(0, max(0, (date.fromisoformat(nxt) - start).days - 1)))
            add("as_of", f"What was {name}'s {noun} on {asked.isoformat()}?", {"value": stretch["value"], "since": stretch["from"], "ticket": stretch["ticket"]},
                mentions(code, stretch["value"], since=stretch["from"], until=asked.isoformat()))

            for s in segments[1:]:
                taken = sum(1 for x in segments if x["value"] == s["value"])
                prev = segments[segments.index(s) - 1]["value"]
                effect = {"fact": fact, "from": prev, "to": s["value"], "date": s["from"]}
                evidence = mentions(code, s["ticket"], s["value"])
                add("ticket_effect", f"What did {s['ticket']} change in {name}?", effect, evidence)
                # A value the fact took twice has two answers, so it isn't asked about by value.
                if taken == 1:
                    add("ticket", f"Which ticket changed {name}'s {noun} to {s['value']}?", {"ticket": s["ticket"]}, evidence)
                    add("when", f"When did {name}'s {noun} change to {s['value']}?", {"date": s["from"]}, mentions(code, s["value"], on=s["from"]))

        # Two windows per project; one may hold no changes at all, which is itself an answer.
        for _ in range(2):
            a = date(2026, 1, 12) + timedelta(rng.randint(0, 120))
            b = a + timedelta(rng.randint(21, 56))
            changes = [{"fact": f, "from": segs[i - 1]["value"], "to": s["value"], "date": s["from"], "ticket": s["ticket"]}
                       for f, segs in facts.items() for i, s in enumerate(segs) if i and a.isoformat() <= s["from"] <= b.isoformat()]
            evidence = sorted({n for c in changes for n in mentions(code, c["to"], on=c["date"])})
            covered = all(mentions(code, c["to"], on=c["date"]) for c in changes)
            add("changed", f"What changed in {name}'s setup between {a.isoformat()} and {b.isoformat()}?",
                {"changes": sorted(changes, key=lambda c: c["date"])}, evidence if covered and changes else (["(none)"] if not changes else []))

        for k, q in enumerate(rng.sample(found, len(found)), 1):
            questions.append({"id": f"{code}-{k:03d}", "project": name, "code": code, "split": split[code], **q})

    questions.sort(key=lambda q: q["id"])
    with (root / "questions.jsonl").open("w", encoding="utf-8", newline="\n") as f:
        f.writelines(json.dumps(q, ensure_ascii=False) + "\n" for q in questions)
    kinds = {}
    for q in questions:
        kinds.setdefault((q["kind"], q["split"]), 0)
        kinds[(q["kind"], q["split"])] += 1
    print(len(notes), "notes,", len(questions), "questions,", dropped, "dropped for want of a note that answers them")
    for k in sorted(kinds):
        print(*k, kinds[k])
    return 0


if __name__ == "__main__":
    sys.exit(main())
