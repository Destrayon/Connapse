"""Builds the memory-search dataset (CON-244): a container's documents, the facts agents stored about them, and
search tasks that need those facts, plus control and stale-fact tasks that measure harm.

Usage: python -I eval/tools/build_memory_search.py eval/datasets/memory-search

Every name is made up. The output is a pure function of SEED.
"""

import json
import random
import re
import sys
from pathlib import Path

SEED = 244

DEPARTMENTS = [
    "Field Operations", "Research", "Finance", "Customer Success", "Platform", "Data Science",
    "Legal", "Marketing", "Facilities", "Security", "Sales Engineering", "Manufacturing",
]

# (area, alternative area, phrase, alternative phrase, format, values)
ATTRIBUTES = [
    ("Travel", "Expense Reimbursement", "daily meal allowance", "per-diem limit", "${}", range(35, 100, 5)),
    ("Support", "Customer Records", "retention period for support tickets", "ticket archive window", "{} days", range(60, 800, 30)),
    ("Procurement", "Purchasing", "purchase-order approval threshold", "sign-off limit for purchase orders", "${:,}", range(2000, 52000, 2500)),
    ("Access Control", "Identity", "password rotation interval", "credential expiry period", "{} days", range(30, 400, 15)),
    ("Remote Work", "Hybrid Working", "home-office equipment allowance", "remote setup stipend", "${}", range(250, 1600, 50)),
    ("Training", "Learning and Development", "annual training budget per person", "yearly learning allowance", "${:,}", range(800, 6000, 200)),
    ("Hiring", "Recruitment", "referral bonus", "employee referral award", "${:,}", range(500, 8000, 250)),
    ("Overtime", "Working Hours", "overtime pay multiplier", "extra-hours rate", "{}x", [1.25, 1.4, 1.5, 1.6, 1.75, 2.0, 2.25, 2.5]),
    ("Incident Response", "On-Call", "paging acknowledgement deadline", "page response target", "{} minutes", range(5, 65, 5)),
    ("Backup", "Data Protection", "backup retention period", "snapshot keep period", "{} days", range(14, 400, 7)),
    ("Leave", "Time Off", "annual leave carry-over limit", "unused vacation rollover cap", "{} days", range(3, 26, 1)),
    ("Vendor Risk", "Supplier Management", "vendor security review cycle", "supplier assessment frequency", "every {} months", range(3, 37, 3)),
    ("IT Equipment", "Hardware Lifecycle", "laptop replacement cycle", "device refresh interval", "{} months", range(24, 61, 6)),
    ("Corporate Cards", "Spending Controls", "single-transaction card limit", "per-purchase card cap", "${:,}", range(500, 15000, 500)),
    ("Code Review", "Engineering Standards", "minimum number of code reviewers", "required approvals per change", "{} reviewers", range(1, 5)),
    ("Change Freeze", "Release Management", "release freeze length before quarter end", "pre-quarter deploy blackout", "{} days", range(2, 22, 1)),
    ("Refunds", "Billing Adjustments", "refund approval limit", "credit sign-off ceiling", "${:,}", range(100, 10000, 300)),
    ("Documentation", "Knowledge Management", "document review interval", "content audit cadence", "every {} months", range(2, 25, 2)),
    ("Visitor Access", "Site Security", "visitor badge validity", "guest pass duration", "{} hours", range(4, 73, 4)),
    ("Mobile Phones", "Telecom Stipend", "monthly phone reimbursement", "mobile plan allowance", "${}", range(20, 160, 5)),
    ("Conferences", "Events", "conference attendance limit per year", "yearly event cap", "{} conferences", range(1, 7)),
    ("Encryption", "Key Management", "encryption key rotation period", "key renewal interval", "{} days", range(30, 760, 30)),
    ("Logging", "Observability", "log retention period", "audit trail keep window", "{} days", range(15, 1100, 45)),
    ("Commuting", "Transit Benefits", "commuter subsidy", "transit benefit", "${} per month", range(40, 300, 10)),
    ("Gifts", "Business Ethics", "gift acceptance limit", "maximum gift value", "${}", range(25, 400, 25)),
    ("Probation", "Onboarding", "probation period for new hires", "new-starter trial period", "{} days", range(30, 200, 15)),
    ("Contracts", "Legal Review", "contract value requiring legal review", "legal sign-off threshold", "${:,}", range(5000, 260000, 12500)),
    ("Shift Handover", "Operations Rota", "handover overlap time", "shift changeover window", "{} minutes", range(10, 95, 5)),
    ("Privacy Requests", "Data Subject Rights", "access request response deadline", "data subject reply window", "{} days", range(5, 46, 1)),
    ("Wellness", "Employee Benefits", "annual wellness reimbursement", "yearly health stipend", "${}", range(100, 1300, 50)),
]

SYSTEMS = [
    "payments gateway", "search cluster", "billing database", "customer portal", "identity service",
    "message queue", "data warehouse", "email relay", "file storage service", "analytics pipeline",
    "mobile API", "inventory service", "notification service", "CDN edge", "reporting service",
    "ticketing system", "VPN gateway", "build farm", "feature flag service", "metrics store",
]

CODENAMES = [
    "Cobalt", "Juniper", "Halyard", "Mistral", "Tamarack", "Osprey", "Quartzite", "Larkspur", "Basalt", "Fennel",
    "Sorrel", "Kestrel", "Marigold", "Wren", "Obsidian", "Saffron", "Thistle", "Calypso", "Driftwood", "Bramble",
]

# (question wording, runbook title wording)
ACTIONS = [
    ("rotate the TLS certificate", "certificate rotation"),
    ("fail over to the standby region", "regional failover"),
    ("restore from backup", "backup restore"),
    ("add capacity before a traffic peak", "capacity scale-up"),
    ("rotate the service credentials", "credential rotation"),
    ("drain a node for patching", "node drain"),
]

NICKNAMES = [
    "Bluebird", "Lighthouse", "Night Owls", "Pathfinders", "Rocketeers", "Sherpas", "Anchors", "Firebirds",
    "Ravens", "Compass", "Lanterns", "Mavericks", "Beacons", "Voyagers", "Sparrows", "Atlas", "Harbor",
    "Summit", "Northstar", "Tugboat",
]

PEOPLE = [
    "Avery Lindqvist", "Jonah Okafor", "Priya Raman", "Mateo Ferreira", "Hana Kowalczyk", "Theo Marchetti",
    "Ines Dubois", "Kwame Asante", "Lena Hartmann", "Omar Haddad", "Sofia Petrov", "Ruben Castillo",
]

FILLER = [
    "This policy applies to all permanent staff and contractors in the department.",
    "Questions about this policy go to the department's operations lead.",
    "Exceptions need written approval from the department head and are logged.",
    "The policy is reviewed once a year by the department's leadership group.",
    "Managers are responsible for making sure their reports know this policy.",
    "Records kept under this policy are stored in the department's shared drive.",
    "Breaches of this policy are handled under the standard disciplinary process.",
    "Local law takes precedence where it sets a stricter rule than this policy.",
]

RUNBOOK_STEPS = [
    "Announce the work in the operations channel and wait for acknowledgement.",
    "Confirm that monitoring dashboards are green before starting.",
    "Take a fresh snapshot and note its identifier in the change record.",
    "Run the health checks again after each step and stop on any failure.",
    "Close the change record with the time each step finished.",
]

TEST_SIZES = {"current": 16, "alias": 16, "correction": 16, "location": 16, "control": 40, "stale": 8}
DEV_SIZES = {"current": 4, "alias": 4, "correction": 4, "location": 4, "control": 6, "stale": 2}
MEMORY_TYPES = ("current", "alias", "correction", "location")


def fmt(attribute, value):
    pattern = attribute[4]
    return pattern.format(value)


def date(rng, year_from, year_to):
    year = rng.randint(year_from, year_to)
    return f"{year}-{rng.randint(1, 12):02d}-{rng.randint(1, 28):02d}"


def filler(rng, count=2):
    return " ".join(rng.sample(FILLER, count))


class Builder:
    def __init__(self, rng):
        self.rng = rng
        self.docs = []  # dicts with key, title, text
        self.ops = []
        self.tasks = []
        self.free = {(d, a) for d in DEPARTMENTS for a in range(len(ATTRIBUTES))}
        self.charter_aliases = {d: [] for d in DEPARTMENTS}
        self.charter_keys = {d: f"charter:{d}" for d in DEPARTMENTS}
        self.allowed = {}  # word -> set of doc keys allowed to contain it
        self.change_numbers = rng.sample(range(1000, 9999), 200)
        self.revisions = rng.sample([f"{a}{b}" for a in "BCDFGHJKLMNPQRSTVWXZ" for b in "BCDFGHJKLMNPQRSTVWXZ"], 120)

    def doc(self, key, title, text):
        self.docs.append({"key": key, "title": title, "text": text})
        return key

    def op(self, **fields):
        fields["id"] = f"op-{len(self.ops) + 1:03d}"
        self.ops.append(fields)
        return fields["id"]

    def take(self, count, departments=None):
        """An attribute with `count` departments whose (department, attribute) pair no other task has used."""
        attrs = list(range(len(ATTRIBUTES)))
        self.rng.shuffle(attrs)
        for a in attrs:
            pool = [d for d in (departments or DEPARTMENTS) if (d, a) in self.free]
            if len(pool) >= count:
                chosen = self.rng.sample(pool, count)
                for d in chosen:
                    self.free.discard((d, a))
                return a, chosen
        raise RuntimeError("ran out of department/attribute pairs")

    def values(self, a, count):
        return [fmt(ATTRIBUTES[a], v) for v in self.rng.sample(list(ATTRIBUTES[a][5]), count)]

    def policy_text(self, title, issued, dept, a, value, extra=""):
        phrase = ATTRIBUTES[a][2]
        lines = [f"{title}. Issued {issued}."]
        if extra:
            lines.append(extra)
        lines.append(f"The {phrase} for {dept} is {value}.")
        lines.append(filler(self.rng))
        return "\n".join(lines)

    def task(self, kind, split, documented, question, gold, stale, answer, stale_answer, facts):
        self.tasks.append({
            "kind": kind, "split": split, "documented": documented, "question": question,
            "gold": gold, "stale": stale, "answer": answer, "staleAnswer": stale_answer, "facts": facts,
        })

    # --- task builders -------------------------------------------------------------------------------------

    def current(self, n, split, documented):
        a, (dept,) = self.take(1)
        area = ATTRIBUTES[a][0]
        values = self.values(a, 3)
        revisions = [self.revisions.pop() for _ in range(3)]
        dates = sorted({date(self.rng, 2023, 2025) for _ in range(6)})
        dates = self.rng.sample(dates, 3)
        authoritative = self.rng.randrange(3)
        keys = []
        for i in range(3):
            title = f"{dept} {area} Policy, revision {revisions[i]}"
            status = ""
            if documented:
                status = "Status: in force." if i == authoritative else self.rng.choice(
                    ["Status: superseded, kept for reference only.", "Status: draft, never adopted."])
            keys.append(self.doc(f"{n}:rev{i}", title, self.policy_text(title, dates[i], dept, a, values[i], status)))
        fact = self.op(op="remember", subject=f"{dept} {area} Policy", attribute="revision in force",
                       value=f"revision {revisions[authoritative]}", validFrom=dates[authoritative],
                       sources=[keys[authoritative]], reason="Confirmed with the policy owner.")
        others = [k for i, k in enumerate(keys) if i != authoritative]
        self.task("current", split, documented, f"What is the {ATTRIBUTES[a][2]} for {dept}?",
                  [keys[authoritative]], others, values[authoritative], None, [fact])

    def alias(self, n, split, documented, nickname):
        a, depts = self.take(4)
        dept = depts[0]
        area = ATTRIBUTES[a][0]
        values = self.values(a, 4)
        keys = []
        for i, d in enumerate(depts):
            title = f"{d} {area} Policy"
            keys.append(self.doc(f"{n}:team{i}", title, self.policy_text(title, date(self.rng, 2024, 2025), d, a, values[i])))
        charter = self.charter_keys[dept]
        if documented:
            self.charter_aliases[dept].append(nickname)
            self.allowed[nickname] = {charter}
        else:
            self.allowed[nickname] = set()
        fact = self.op(op="remember", subject=f"{nickname} team", attribute="official team name", value=dept,
                       sources=[charter], reason="The team's nickname in chat.")
        self.task("alias", split, documented, f"What is the {ATTRIBUTES[a][2]} for the {nickname} team?",
                  [keys[0]], [], values[0], None, [fact])

    def correction(self, n, split, documented):
        a, (dept,) = self.take(1)
        area, area_alt, phrase, phrase_alt = ATTRIBUTES[a][:4]
        old_value, new_value = self.values(a, 2)
        old_date, new_date = date(self.rng, 2023, 2023), date(self.rng, 2025, 2025)
        old_title = f"{dept} {area} Policy (2023)"
        old = self.doc(f"{n}:old", old_title, self.policy_text(old_title, old_date, dept, a, old_value))
        new_title = f"{dept} {area_alt} Standard"
        lines = [f"{new_title}. Effective {new_date}."]
        if documented:
            lines.append(f"This standard replaces the {old_title}.")
        lines.append(f"The {phrase_alt} for {dept} is {new_value}.")
        lines.append(filler(self.rng))
        new = self.doc(f"{n}:new", new_title, "\n".join(lines))
        first = self.op(op="remember", subject=dept, attribute=phrase, value=old_value, validFrom=old_date,
                        sources=[old], reason=f"From the {old_title}.")
        second = self.op(op="correct", target=first, kind="changed", value=new_value, validFrom=new_date,
                         sources=[new], reason=f"Replaced by the {new_title}.")
        self.task("correction", split, documented, f"What is the {phrase} for {dept}?",
                  [new], [old], new_value, old_value, [first, second])

    def location(self, n, split, documented, system, codename, action, other_action):
        number = self.change_numbers.pop()
        keys = []
        for act, is_target in ((action, True), (other_action, False)):
            title = f"{codename} ops notes: {act[1]}"
            change = f"CHG-{number}" if is_target else f"CHG-{self.change_numbers.pop()}"
            steps = self.rng.sample(RUNBOOK_STEPS, 3)
            text = "\n".join([
                f"{title}.",
                f"Procedure for {act[1]} on {codename}. Raise change template {change} before starting.",
                *[f"{i + 1}. {s}" for i, s in enumerate(steps)],
            ])
            keys.append(self.doc(f"{n}:{'target' if is_target else 'other'}", title, text))
        overview_lines = [
            f"{system[0].upper()}{system[1:]} overview.",
            f"The {system} is run by the Platform team and is monitored around the clock.",
        ]
        if documented:
            overview_lines.append(f"Internally the {system} runs under the codename {codename}, and its runbooks use that name.")
        overview_lines.append("Capacity planning for it is reviewed every quarter.")
        overview = self.doc(f"{n}:overview", f"{system[0].upper()}{system[1:]} overview", "\n".join(overview_lines))
        self.allowed[codename] = set(keys) | ({overview} if documented else set())
        self.allowed[system] = {overview}
        codename_fact = self.op(op="remember", subject=system, attribute="codename", value=codename,
                                sources=[overview], reason="The name the runbooks use.")
        runbook_fact = self.op(op="remember", subject=system, attribute=f"runbook for {action[1]}",
                               value=f"{codename} ops notes: {action[1]}", sources=[keys[0]],
                               reason="Where the procedure lives.")
        self.task("location", split, documented, f"Which change template do we raise to {action[0]} on the {system}?",
                  [keys[0]], [], f"CHG-{number}", None, [codename_fact, runbook_fact])

    def control(self, n, split):
        a, depts = self.take(2)
        area = ATTRIBUTES[a][0]
        values = self.values(a, 2)
        keys = []
        for i, d in enumerate(depts):
            title = f"{d} {area} Policy"
            keys.append(self.doc(f"{n}:c{i}", title, self.policy_text(title, date(self.rng, 2024, 2025), d, a, values[i])))
        self.task("control", split, None, f"What is the {ATTRIBUTES[a][2]} for {depts[0]}?",
                  [keys[0]], [], values[0], None, [])

    def stale(self, n, split):
        a, (dept,) = self.take(1)
        area, phrase = ATTRIBUTES[a][0], ATTRIBUTES[a][2]
        old_value, new_value = self.values(a, 2)
        old_date, new_date = date(self.rng, 2023, 2023), date(self.rng, 2025, 2025)
        old_title = f"{dept} {area} Policy"
        old = self.doc(f"{n}:old", old_title, self.policy_text(old_title, old_date, dept, a, old_value))
        new_title = f"{dept} {area} Policy, 2025 update"
        text = "\n".join([
            f"{new_title}. Effective {new_date}.",
            f"This update replaces the {old_title} issued {old_date}.",
            f"The {phrase} for {dept} is now {new_value}.",
            filler(self.rng),
        ])
        new = self.doc(f"{n}:new", new_title, text)
        fact = self.op(op="remember", subject=dept, attribute=phrase, value=old_value, validFrom=old_date,
                       sources=[old], reason=f"From the {old_title}.")
        self.task("stale", split, True, f"What is the {phrase} for {dept}?", [new], [old], new_value, old_value, [fact])

    def charters_and_noise(self):
        leads = self.rng.sample(PEOPLE, len(DEPARTMENTS))
        for dept, lead in zip(DEPARTMENTS, leads):
            channel = "#" + dept.lower().replace(" ", "-")
            lines = [
                f"{dept} team charter.",
                f"The {dept} team owns its department's policies and the services listed in its service catalogue.",
                f"Team lead: {lead}. Chat channel: {channel}.",
            ]
            for nickname in self.charter_aliases[dept]:
                lines.append(f"Within the company the team is also known as the {nickname} team.")
            key = self.doc(self.charter_keys[dept], f"{dept} team charter", "\n".join(lines))
            self.op(op="remember", subject=dept, attribute="team lead", value=lead, sources=[key],
                    reason="From the team charter.")
            self.op(op="remember", subject=dept, attribute="chat channel", value=channel, sources=[key],
                    reason="From the team charter.")


def plan(rng):
    """Which split and documented flag each task gets: half of each memory type's tasks per split are documented."""
    entries = []
    for split, sizes in (("dev", DEV_SIZES), ("test", TEST_SIZES)):
        for kind, size in sizes.items():
            for i in range(size):
                documented = None if kind == "control" else (True if kind == "stale" else i % 2 == 0)
                entries.append((kind, split, documented))
    rng.shuffle(entries)
    return entries


def build(rng):
    b = Builder(rng)
    nicknames = rng.sample(NICKNAMES, len(NICKNAMES))
    systems = rng.sample(range(len(SYSTEMS)), len(SYSTEMS))
    for n, (kind, split, documented) in enumerate(plan(rng)):
        name = f"t{n}"
        if kind == "current":
            b.current(name, split, documented)
        elif kind == "alias":
            b.alias(name, split, documented, nicknames.pop())
        elif kind == "correction":
            b.correction(name, split, documented)
        elif kind == "location":
            s = systems.pop()
            action, other = rng.sample(ACTIONS, 2)
            b.location(name, split, documented, SYSTEMS[s], CODENAMES[s], action, other)
        elif kind == "control":
            b.control(name, split)
        else:
            b.stale(name, split)
    b.charters_and_noise()
    return b


def check(b):
    titles = [d["title"] for d in b.docs]
    assert len(titles) == len(set(titles)), "document titles must be unique"
    for word, allowed in b.allowed.items():
        pattern = re.compile(rf"\b{re.escape(word)}\b", re.IGNORECASE)
        for d in b.docs:
            if pattern.search(d["title"] + "\n" + d["text"]) and d["key"] not in allowed:
                raise AssertionError(f"'{word}' leaks into {d['title']}")
    by_key = {d["key"]: d for d in b.docs}
    for t in b.tasks:
        for key in t["gold"]:
            assert t["answer"] in by_key[key]["text"], f"gold document lacks the answer: {t['question']}"
        if t["staleAnswer"]:
            assert t["staleAnswer"] != t["answer"]
        if t["kind"] == "correction":
            phrase = re.search(r"What is the (.+) for ", t["question"]).group(1)
            assert phrase not in by_key[t["gold"][0]]["text"], "corrected document must not use the old wording"


def write(b, out, rng):
    order = list(range(len(b.docs)))
    rng.shuffle(order)
    ids = {}
    corpus = []
    for i, index in enumerate(order):
        d = b.docs[index]
        ids[d["key"]] = f"ms-{i + 1:04d}"
        corpus.append({"id": ids[d["key"]], "title": d["title"], "text": d["text"]})
    out.mkdir(parents=True, exist_ok=True)

    def dump(name, rows):
        with open(out / name, "w", encoding="utf-8", newline="\n") as f:
            for row in rows:
                f.write(json.dumps(row, ensure_ascii=False) + "\n")

    dump("corpus.jsonl", corpus)
    ops = []
    for o in b.ops:
        o = dict(o)
        o["sources"] = [ids[k] for k in o["sources"]]
        ops.append(o)
    dump("memory.jsonl", ops)
    counters = {}
    tasks = []
    for t in sorted(b.tasks, key=lambda t: (t["split"] != "dev", t["kind"])):
        counters[(t["split"], t["kind"])] = counters.get((t["split"], t["kind"]), 0) + 1
        row = {"id": f"{t['split']}-{t['kind']}-{counters[(t['split'], t['kind'])]:02d}", **t}
        row["gold"] = [ids[k] for k in t["gold"]]
        row["stale"] = [ids[k] for k in t["stale"]]
        tasks.append(row)
    dump("tasks.jsonl", tasks)
    return corpus, ops, tasks


def main():
    out = Path(sys.argv[1])
    rng = random.Random(SEED)
    b = build(rng)
    check(b)
    corpus, ops, tasks = write(b, out, rng)
    kinds = {}
    for t in tasks:
        kinds.setdefault((t["split"], t["kind"]), 0)
        kinds[(t["split"], t["kind"])] += 1
    print(f"{len(corpus)} documents, {len(ops)} memory operations, {len(tasks)} tasks")
    for (split, kind), count in sorted(kinds.items()):
        print(f"  {split:4} {kind:10} {count}")


if __name__ == "__main__":
    main()
