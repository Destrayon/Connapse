"""Builds the memory-learned dataset (CON-244, realistic arm): a container's documents and two sessions of questions.

Nothing here is written into memory up front. In session 1 an agent answers questions by searching the documents and
saves whatever memories it chooses. In session 2 a fresh agent answers different but related questions about the
same things, with and without that memory; stale families repeat their session-1 question, which a late document
has since answered differently. Every fact a task needs is in some document; memory can only save the
agent the work of finding it again.

Usage: python -I eval/tools/build_memory_learned.py eval/datasets/memory-learned

Every name is made up. The output is a pure function of SEED. Names, attributes and wording come from
build_memory_search.py so both datasets read alike.
"""

import importlib.util
import json
import random
import re
import sys
from pathlib import Path

_spec = importlib.util.spec_from_file_location("build_memory_search", Path(__file__).with_name("build_memory_search.py"))
ms = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(ms)

SEED = 244
# The scored run started on this day; no document may be dated on or after it.
RUN_DATE = "2026-11-05"

TEST_SIZES = {"current": 16, "alias": 16, "correction": 16, "location": 16, "control": 28, "stale": 12}
DEV_SIZES = {"current": 3, "alias": 3, "correction": 3, "location": 3, "control": 4, "stale": 2}


class Builder:
    def __init__(self, rng):
        self.rng = rng
        self.docs = []  # key, title, text, late
        self.tasks = []
        self.free = {(d, a) for d in ms.DEPARTMENTS for a in range(len(ms.ATTRIBUTES))}
        self.charter_aliases = {d: [] for d in ms.DEPARTMENTS}
        self.charter_keys = {d: f"charter:{d}" for d in ms.DEPARTMENTS}
        self.register = {d: [] for d in ms.DEPARTMENTS}
        self.register_keys = {d: f"register:{d}" for d in ms.DEPARTMENTS}
        self.allowed = {}  # word -> doc keys allowed to contain it
        self.change_numbers = rng.sample(range(1000, 9999), 200)
        self.revisions = rng.sample([f"{a}{b}" for a in "BCDFGHJKLMNPQRSTVWXZ" for b in "BCDFGHJKLMNPQRSTVWXZ"], 120)

    def doc(self, key, title, text, late=False):
        self.docs.append({"key": key, "title": title, "text": text, "late": late})
        return key

    def task(self, family, kind, split, session, question, gold, stale, answer, stale_answer):
        self.tasks.append({
            "family": family, "kind": kind, "split": split, "session": session,
            "documented": None if kind == "control" else True, "question": question,
            "gold": gold, "stale": stale, "answer": answer, "staleAnswer": stale_answer,
        })

    def take(self, count, include=None):
        """An attribute free for `count` departments, `include` among them when given; marks the pairs used."""
        attrs = list(range(len(ms.ATTRIBUTES)))
        self.rng.shuffle(attrs)
        for a in attrs:
            if include is not None and (include, a) not in self.free:
                continue
            pool = [d for d in ms.DEPARTMENTS if (d, a) in self.free and d != include]
            need = count - (1 if include is not None else 0)
            if len(pool) >= need:
                chosen = ([include] if include is not None else []) + self.rng.sample(pool, need)
                for d in chosen:
                    self.free.discard((d, a))
                return a, chosen
        raise RuntimeError("ran out of department/attribute pairs")

    def values(self, a, count):
        return [ms.fmt(ms.ATTRIBUTES[a], v) for v in self.rng.sample(list(ms.ATTRIBUTES[a][5]), count)]

    def line(self, a, dept, value, alt=False):
        return f"The {ms.ATTRIBUTES[a][3 if alt else 2]} for {dept} is {value}."

    def question(self, a, who):
        return f"What is the {ms.ATTRIBUTES[a][2]} for {who}?"

    # --- families: each gives a session-1 question and a related session-2 question ---------------------------

    def current(self, n, split):
        """Three revisions of a two-topic policy; only the department's policy register says which is in force."""
        a1, (dept,) = self.take(1)
        a2, _ = self.take(1, include=dept)
        area = f"{ms.ATTRIBUTES[a1][0]} and {ms.ATTRIBUTES[a2][0]}"
        v1, v2 = self.values(a1, 3), self.values(a2, 3)
        revisions = [self.revisions.pop() for _ in range(3)]
        dates = self.rng.sample(sorted({ms.date(self.rng, 2023, 2025) for _ in range(8)}), 3)
        in_force = self.rng.randrange(3)
        keys = []
        for i in range(3):
            title = f"{dept} {area} Policy, revision {revisions[i]}"
            text = "\n".join([f"{title}. Issued {dates[i]}.", self.line(a1, dept, v1[i]), self.line(a2, dept, v2[i]),
                              ms.filler(self.rng)])
            keys.append(self.doc(f"{n}:rev{i}", title, text))
        self.register[dept].append(f"The {area} Policy in force is revision {revisions[in_force]}; other revisions are drafts or superseded.")
        others = [k for i, k in enumerate(keys) if i != in_force]
        self.task(n, "current", split, 1, self.question(a1, dept), [keys[in_force]], others, v1[in_force], None)
        self.task(n, "current", split, 2, self.question(a2, dept), [keys[in_force]], others, v2[in_force], None)

    def alias(self, n, split, nickname):
        """A team nickname only the charter explains; each session asks about a different policy of that team."""
        a1, depts1 = self.take(3)
        dept = depts1[0]
        a2, depts2 = self.take(3, include=dept)
        for session, (a, depts) in enumerate(((a1, depts1), (a2, depts2)), start=1):
            values = self.values(a, len(depts))
            keys = []
            for i, d in enumerate(depts):
                title = f"{d} {ms.ATTRIBUTES[a][0]} Policy"
                text = "\n".join([f"{title}. Issued {ms.date(self.rng, 2024, 2025)}.", self.line(a, d, values[i]), ms.filler(self.rng)])
                keys.append(self.doc(f"{n}:s{session}:{i}", title, text))
            self.task(n, "alias", split, session, self.question(a, f"the {nickname} team"), [keys[0]], [], values[0], None)
        self.charter_aliases[dept].append(nickname)
        self.allowed[nickname] = {self.charter_keys[dept]}

    def correction(self, n, split):
        """A two-topic policy replaced by a renamed standard that words both topics differently and says what it replaces."""
        a1, (dept,) = self.take(1)
        a2, _ = self.take(1, include=dept)
        old1, new1 = self.values(a1, 2)
        old2, new2 = self.values(a2, 2)
        old_date, new_date = ms.date(self.rng, 2023, 2023), ms.date(self.rng, 2025, 2025)
        old_title = f"{dept} {ms.ATTRIBUTES[a1][0]} and {ms.ATTRIBUTES[a2][0]} Policy (2023)"
        old = self.doc(f"{n}:old", old_title, "\n".join([
            f"{old_title}. Issued {old_date}.", self.line(a1, dept, old1), self.line(a2, dept, old2), ms.filler(self.rng)]))
        new_title = f"{dept} {ms.ATTRIBUTES[a1][1]} and {ms.ATTRIBUTES[a2][1]} Standard"
        new = self.doc(f"{n}:new", new_title, "\n".join([
            f"{new_title}. Effective {new_date}.", f"This standard replaces the {old_title}.",
            self.line(a1, dept, new1, alt=True), self.line(a2, dept, new2, alt=True), ms.filler(self.rng)]))
        self.task(n, "correction", split, 1, self.question(a1, dept), [new], [old], new1, old1)
        self.task(n, "correction", split, 2, self.question(a2, dept), [new], [old], new2, old2)

    def location(self, n, split, system, codename, actions):
        """A system whose runbooks go by a codename that only its overview names; each session asks a different procedure."""
        keys = []
        numbers = []
        for i, act in enumerate(actions):
            number = self.change_numbers.pop()
            numbers.append(number)
            title = f"{codename} ops notes: {act[1]}"
            steps = self.rng.sample(ms.RUNBOOK_STEPS, 3)
            keys.append(self.doc(f"{n}:rb{i}", title, "\n".join([
                f"{title}.", f"Procedure for {act[1]} on {codename}. Raise change template CHG-{number} before starting.",
                *[f"{j + 1}. {s}" for j, s in enumerate(steps)]])))
        name = f"{system[0].upper()}{system[1:]}"
        overview = self.doc(f"{n}:overview", f"{name} overview", "\n".join([
            f"{name} overview.", f"The {system} is run by the Platform team and is monitored around the clock.",
            f"Internally the {system} runs under the codename {codename}, and its runbooks use that name.",
            "Capacity planning for it is reviewed every quarter."]))
        self.allowed[codename] = set(keys) | {overview}
        self.allowed[system] = {overview}
        for session in (1, 2):
            i = session - 1
            self.task(n, "location", split, session, f"Which change template do we raise to {actions[i][0]} on the {system}?",
                      [keys[i]], [], f"CHG-{numbers[i]}", None)

    def control(self, n, split):
        """Session 2 only: a policy question about something no session-1 question touched."""
        a, depts = self.take(2)
        values = self.values(a, 2)
        keys = []
        for i, d in enumerate(depts):
            title = f"{d} {ms.ATTRIBUTES[a][0]} Policy"
            keys.append(self.doc(f"{n}:c{i}", title, "\n".join([
                f"{title}. Issued {ms.date(self.rng, 2024, 2025)}.", self.line(a, d, values[i]), ms.filler(self.rng)])))
        self.task(n, "control", split, 2, self.question(a, depts[0]), [keys[0]], [], values[0], None)

    def stale(self, n, split):
        """Session 1 learns a value; an update added between the sessions replaces it; session 2 asks again."""
        a, (dept,) = self.take(1)
        old_value, new_value = self.values(a, 2)
        old_date, new_date = ms.date(self.rng, 2024, 2024), before_run(ms.date(self.rng, 2026, 2026))
        old_title = f"{dept} {ms.ATTRIBUTES[a][0]} Policy"
        old = self.doc(f"{n}:old", old_title, "\n".join([
            f"{old_title}. Issued {old_date}.", self.line(a, dept, old_value), ms.filler(self.rng)]))
        new_title = f"{dept} {ms.ATTRIBUTES[a][0]} Policy, 2026 update"
        new = self.doc(f"{n}:new", new_title, "\n".join([
            f"{new_title}. Effective {new_date}.", f"This update replaces the {old_title} issued {old_date}.",
            f"The {ms.ATTRIBUTES[a][2]} for {dept} is now {new_value}.", ms.filler(self.rng)]), late=True)
        question = self.question(a, dept)
        self.task(n, "stale", split, 1, question, [old], [], old_value, None)
        self.task(n, "stale", split, 2, question, [new], [old], new_value, old_value)

    def charters_and_registers(self):
        leads = self.rng.sample(ms.PEOPLE, len(ms.DEPARTMENTS))
        for dept, lead in zip(ms.DEPARTMENTS, leads):
            channel = "#" + dept.lower().replace(" ", "-")
            lines = [
                f"{dept} team charter.",
                f"The {dept} team owns its department's policies and the services listed in its service catalogue.",
                f"Team lead: {lead}. Chat channel: {channel}.",
            ]
            lines += [f"Within the company the team is also known as the {nick} team." for nick in self.charter_aliases[dept]]
            self.doc(self.charter_keys[dept], f"{dept} team charter", "\n".join(lines))
            entries = self.register[dept] or ["No policy currently has more than one revision."]
            self.doc(self.register_keys[dept], f"{dept} policy register",
                     "\n".join([f"{dept} policy register.", "Which revision of each department policy is in force.", *entries]))


def plan(rng):
    entries = [(kind, split) for split, sizes in (("dev", DEV_SIZES), ("test", TEST_SIZES))
               for kind, size in sizes.items() for _ in range(size)]
    rng.shuffle(entries)
    return entries


def build(rng):
    b = Builder(rng)
    nicknames = rng.sample(ms.NICKNAMES, len(ms.NICKNAMES))
    systems = rng.sample(range(len(ms.SYSTEMS)), len(ms.SYSTEMS))
    for i, (kind, split) in enumerate(plan(rng)):
        n = f"f{i}"
        if kind == "current":
            b.current(n, split)
        elif kind == "alias":
            b.alias(n, split, nicknames.pop())
        elif kind == "correction":
            b.correction(n, split)
        elif kind == "location":
            s = systems.pop()
            b.location(n, split, ms.SYSTEMS[s], ms.CODENAMES[s], rng.sample(ms.ACTIONS, 3))
        elif kind == "control":
            b.control(n, split)
        else:
            b.stale(n, split)
    b.charters_and_registers()
    return b


def before_run(date):
    """Moves a date on or after RUN_DATE two months back, after drawing it, so the random stream is unchanged."""
    if date < RUN_DATE:
        return date
    year, month, day = date.split("-")
    return f"{year}-{int(month) - 2:02d}-{day}"


def check(b):
    titles = [d["title"] for d in b.docs]
    assert len(titles) == len(set(titles)), "document titles must be unique"
    for word, allowed in b.allowed.items():
        pattern = re.compile(rf"\b{re.escape(word)}\b", re.IGNORECASE)
        for d in b.docs:
            if pattern.search(d["title"] + "\n" + d["text"]) and d["key"] not in allowed:
                raise AssertionError(f"'{word}' leaks into {d['title']}")
    for d in b.docs:
        for date in re.findall(r"\d{4}-\d{2}-\d{2}", d["text"]):
            assert date < RUN_DATE, f"{d['title']} is dated {date}, after the run"
    by_key = {d["key"]: d for d in b.docs}
    for t in b.tasks:
        for key in t["gold"]:
            assert t["answer"] in by_key[key]["text"], f"gold document lacks the answer: {t['question']}"
        if t["staleAnswer"]:
            assert t["staleAnswer"] != t["answer"]
        if t["kind"] == "correction":
            phrase = re.search(r"What is the (.+) for ", t["question"]).group(1)
            assert phrase not in by_key[t["gold"][0]]["text"], "the standard must not use the old wording"
        if t["session"] == 1:
            assert not any(by_key[k]["late"] for k in t["gold"]), "session 1 can only cite documents present then"
    families = {}
    for t in b.tasks:
        families.setdefault(t["family"], set()).add(t["session"])
    for family, sessions in families.items():
        kind = next(t["kind"] for t in b.tasks if t["family"] == family)
        assert sessions == ({2} if kind == "control" else {1, 2}), f"{family} lacks a session"


def write(b, out, rng):
    order = list(range(len(b.docs)))
    rng.shuffle(order)
    ids = {}
    corpus, late = [], []
    for i, index in enumerate(order):
        d = b.docs[index]
        ids[d["key"]] = f"ml-{i + 1:04d}"
        (late if d["late"] else corpus).append({"id": ids[d["key"]], "title": d["title"], "text": d["text"]})
    out.mkdir(parents=True, exist_ok=True)

    def dump(name, rows):
        with open(out / name, "w", encoding="utf-8", newline="\n") as f:
            for row in rows:
                f.write(json.dumps(row, ensure_ascii=False) + "\n")

    dump("corpus.jsonl", corpus)
    dump("late.jsonl", late)
    family_ids = {}
    counters = {}
    rows = []
    for t in b.tasks:
        if t["family"] not in family_ids:
            key = (t["split"], t["kind"])
            counters[key] = counters.get(key, 0) + 1
            family_ids[t["family"]] = f"{t['split']}-{t['kind']}-{counters[key]:02d}"
        family = family_ids[t["family"]]
        rows.append({"id": f"{family}-s{t['session']}", **t, "family": family,
                     "gold": [ids[k] for k in t["gold"]], "stale": [ids[k] for k in t["stale"]]})
    # Run order: split, then session, then a seeded shuffle, so session 1 mixes kinds the way real questions arrive.
    tasks = []
    for split in ("dev", "test"):
        for session in (1, 2):
            group = [r for r in rows if r["split"] == split and r["session"] == session]
            group.sort(key=lambda r: r["id"])
            rng.shuffle(group)
            tasks += group
    dump("tasks.jsonl", tasks)
    return corpus, late, tasks


def main():
    out = Path(sys.argv[1])
    rng = random.Random(SEED)
    b = build(rng)
    check(b)
    corpus, late, tasks = write(b, out, rng)
    print(f"{len(corpus)} documents, {len(late)} late documents, {len(tasks)} tasks")
    counts = {}
    for t in tasks:
        counts[(t["split"], t["session"], t["kind"])] = counts.get((t["split"], t["session"], t["kind"]), 0) + 1
    for (split, session, kind), count in sorted(counts.items()):
        print(f"  {split:4} session {session} {kind:10} {count}")


if __name__ == "__main__":
    main()
