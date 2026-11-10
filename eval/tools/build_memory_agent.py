"""Builds the memory-agent dataset (CON-313): a project manager's memory space, questions worded differently from
the facts that answer them, and save tasks where the user tells the agent something to record.

Usage: python -I eval/tools/build_memory_agent.py eval/datasets/memory-agent
       python -I eval/tools/build_memory_agent.py eval/datasets/memory-agent-sealed-1 --spec eval/tools/memory-agent-sealed-1.spec.json.gz

Every company, project, person and value is made up. The output is a pure function of this file, or of this file and
the spec. The script fails if a task breaks its kind's wording rule (see check_find).

A spec (CON-321) replaces the facts, filler names and tasks below with another set held to the same rules. A spec's
output is written gzipped, as a sealed set is committed; don't decompress a sealed set or its spec to read it.
"""

import gzip
import json
import re
import sys
from pathlib import Path

# (key, subject, attribute, value, history) where history is (old value, date the old value began, date it changed).
FACTS = [
    ("f001", "Harrow", "release branch", "release/2.4", ("release/2.3", "2026-05-04", "2026-09-14")),
    ("f002", "Harrow", "tech lead", "Ilse Varga", None),
    ("f003", "Harrow", "database", "PostgreSQL 16", None),
    ("f004", "Harrow", "deploy window", "Tuesdays 14:00-16:00 UTC", None),
    ("f005", "Harrow", "repository", "orrin/harrow-billing", None),
    ("f006", "Harrow", "purpose", "generates and emails customer invoices", None),
    ("f007", "Harrow", "CI provider", "Buildkite", None),
    ("f008", "Harrow", "test command", "make check-all", None),
    ("f009", "Quill", "purpose", "public documentation website", None),
    ("f010", "Quill", "hosting", "Netlify", None),
    ("f011", "Quill", "static site generator", "Docusaurus 3", None),
    ("f012", "Quill", "content reviewer", "Rosa Lindqvist", None),
    ("f013", "Quill", "publish schedule", "every Thursday afternoon", ("every Wednesday morning", "2026-02-02", "2026-08-03")),
    ("f014", "Bramble", "purpose", "iOS and Android customer app", None),
    ("f015", "Bramble", "framework", "React Native 0.76", None),
    ("f016", "Bramble", "store submission owner", "Kofi Mensah", None),
    ("f017", "Bramble", "minimum iOS version", "iOS 16", ("iOS 15", "2025-09-15", "2026-09-21")),
    ("f018", "Bramble", "crash reporting", "Sentry", None),
    ("f019", "Bramble", "beta channel", "TestFlight group orrin-insiders", None),
    ("f020", "Lantern", "purpose", "product search API", None),
    ("f021", "Lantern", "index engine", "OpenSearch 2.15", None),
    ("f022", "Lantern", "p95 latency target", "180 ms", ("250 ms", "2026-01-12", "2026-07-06")),
    ("f023", "Lantern", "owner", "Tomas Rheault", None),
    ("f024", "Lantern", "reindex job", "nightly at 02:30 UTC", None),
    ("f025", "Ferro", "purpose", "nightly ETL from the warehouse into analytics", None),
    ("f026", "Ferro", "orchestrator", "Airflow 2.9", None),
    ("f027", "Ferro", "warehouse", "Snowflake", None),
    ("f028", "Ferro", "failure alerts channel", "#ferro-alerts", None),
    ("f029", "Ferro", "owner", "Priya Nandakumar", ("Joel Ashby", "2025-11-03", "2026-10-05")),
    ("f030", "Wicket", "purpose", "login and single sign-on gateway", None),
    ("f031", "Wicket", "token lifetime", "15 minutes", ("30 minutes", "2025-06-01", "2026-06-15")),
    ("f032", "Wicket", "identity provider", "Okta", None),
    ("f033", "Wicket", "security reviewer", "Mireille Sato", None),
    ("f034", "Wicket", "Kubernetes namespace", "auth-prod", None),
    ("f035", "Dana Okafor", "role", "engineering manager", None),
    ("f036", "Dana Okafor", "time zone", "America/Chicago", None),
    ("f037", "Dana Okafor", "preferred contact", "Slack direct message", None),
    ("f038", "Ilse Varga", "role", "staff engineer", None),
    ("f039", "Ilse Varga", "vacation", "out from 2026-12-18 to 2027-01-04", None),
    ("f040", "Tomas Rheault", "time zone", "Europe/Lisbon", None),
    ("f041", "Priya Nandakumar", "role", "data engineer", None),
    ("f042", "Kofi Mensah", "role", "mobile engineer", None),
    ("f043", "Mireille Sato", "role", "security engineer", None),
    ("f044", "Rosa Lindqvist", "role", "technical writer", None),
    ("f045", "Joel Ashby", "role", "contractor, contract ends 2026-12-31", None),
    ("f046", "workers", "parallel limit", "at most 4 agents at once", ("at most 3 agents at once", "2026-04-01", "2026-10-20")),
    ("f047", "workers", "scratch folder", "/var/orrin/scratch", None),
    ("f048", "workers", "branch naming", "feature/<ticket>-<short-description>", None),
    ("f049", "workers", "commit message format", "conventional commits, e.g. \"fix: ...\"", None),
    ("f050", "workers", "pull request size", "under 400 changed lines", None),
    ("f051", "workers", "docker limits", "2 CPUs and 4 GB per container", None),
    ("f052", "workers", "forbidden actions", "never force-push and never merge their own pull request", None),
    ("f053", "workers", "report to", "the PM session named orrin-pm", None),
    ("f054", "workers", "test before push", "run the unit tests locally", None),
    ("f055", "workers", "secrets", "never read .env files", None),
    ("f056", "workers", "review bot", "do not wait for any bot; humans review", None),
    ("f057", "release 3.1", "target date", "2026-12-08", None),
    ("f058", "release 3.1", "branch", "release/3.1", None),
    ("f059", "release 3.1", "freeze starts", "2026-12-01", None),
    ("f060", "release 3.1", "release manager", "Dana Okafor", None),
    ("f061", "release 3.1", "headline feature", "offline mode in Bramble", None),
    ("f062", "release 3.0", "shipped on", "2026-10-14", None),
    ("f063", "CI", "flaky test quarantine label", "flaky-quarantine", None),
    ("f064", "CI", "required checks", "build, unit tests, lint", None),
    ("f065", "CI", "runner image", "ubuntu-24.04", None),
    ("f066", "code review", "approvals required", "2 approvals for main, 1 for feature branches", None),
    ("f067", "code review", "turnaround target", "within one working day", None),
    ("f068", "on-call", "rotation", "weekly, handover Monday 09:00 Chicago time", None),
    ("f069", "on-call", "paging tool", "PagerDuty", None),
    ("f070", "on-call", "current engineer", "Tomas Rheault", ("Kofi Mensah", "2026-10-26", "2026-11-02")),
    ("f071", "staging", "URL", "https://staging.orrin.example", None),
    ("f072", "staging", "reset schedule", "every Sunday night", None),
    ("f073", "project board", "ticket tracker", "Linear, team ORR", None),
    ("f074", "project board", "ticket prefix", "ORR", None),
    ("f075", "incident reviews", "template location", "docs/postmortems/TEMPLATE.md", None),
    ("f076", "incident reviews", "deadline", "within 5 working days of the incident", None),
    ("f077", "user", "preferred report style", "plain sentences, outcome first", None),
    ("f078", "user", "working hours", "08:00-17:00 Central", None),
    ("f079", "user", "approval needed for", "anything that deletes data or touches production", None),
    ("f080", "user", "eval machine", "runs evals at night only", ("runs evals any time", "2026-01-05", "2026-10-12")),
    ("f081", "Harrow", "known bug", "duplicate invoices when a payment is retried (ORR-412)", None),
    ("f082", "Lantern", "known bug", "typo tolerance off for SKUs (ORR-433)", None),
    ("f083", "Bramble", "app store rating", "4.6", None),
    ("f084", "Ferro", "data retention", "13 months", None),
    ("f085", "Wicket", "rate limit", "20 login attempts per minute per IP", None),
    ("f086", "Quill", "analytics", "Plausible", None),
    ("f087", "design system", "name", "Pebble", None),
    ("f088", "design system", "Figma file owner", "Ana Duarte", None),
    ("f089", "budget", "cloud spend cap", "$18,000 per month", ("$15,000 per month", "2026-01-01", "2026-07-01")),
    ("f090", "vendor", "error tracking contract renewal", "2027-03-01", None),
    ("f091", "Lantern", "embedding model", "e5-small", None),
    ("f092", "Harrow", "currency support", "USD, EUR and GBP", None),
    ("f093", "Bramble", "push notifications", "Firebase Cloud Messaging", None),
    ("f094", "Ferro", "schedule", "01:00 UTC daily", None),
    ("f095", "Wicket", "MFA", "required for all admins", None),
]

# Other internal services, so the space is too large to read whole in one call and most facts are distractors.
SERVICES = [
    "Abacus", "Beacon", "Cinder", "Dovetail", "Ember", "Fathom", "Garnet", "Heron", "Ingot", "Juniper",
    "Kestrel", "Ledger", "Marrow", "Nimbus", "Osprey", "Pylon", "Quarry", "Rivet", "Sable", "Tinder",
    "Umber", "Vellum", "Willow", "Yarrow", "Zephyr", "Anvil", "Bracken", "Cobalt", "Drift", "Ermine",
]
OWNERS = ["Dana Okafor", "Ilse Varga", "Tomas Rheault", "Priya Nandakumar", "Kofi Mensah", "Mireille Sato", "Ana Duarte"]
LANGUAGES = ["Go", "Kotlin", "Python", "TypeScript", "Rust", "Java"]
DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]


def filler(services=SERVICES, owners=OWNERS, languages=LANGUAGES, days=DAYS, org="orrin"):
    facts = []
    n = 101
    for i, name in enumerate(services):
        low = name.lower()
        for attribute, value in [
            ("owner", owners[(i * 3) % len(owners)]),
            ("repository", f"{org}/{low}-svc"),
            ("language", languages[(i * 5) % len(languages)]),
            ("deploy day", days[(i * 2) % len(days)]),
            ("alerts channel", f"#{low}-alerts"),
        ]:
            facts.append((f"g{n:03d}", name, attribute, value, None))
            n += 1
    return facts


# Find tasks: (id, kind, split, question, gold fact keys, answer). Kinds:
#   exact     the question shares words with the fact's subject and attribute (control);
#   reworded  the subject is named, but no word stem is shared with the attribute or value;
#   indirect  neither the subject nor any stem of the attribute or value appears;
#   partial   like indirect, except for an abbreviation or prefix of a fact word (listed in PARTIAL);
#   absent    a near miss: memory holds nothing that answers it.
FIND = [
    ("find-e01", "exact", "test", "Who is the tech lead of Harrow?", ["f002"], "Ilse Varga"),
    ("find-e02", "exact", "test", "What index engine does Lantern use?", ["f021"], "OpenSearch 2.15"),
    ("find-e03", "exact", "test", "Which warehouse does Ferro read from?", ["f027"], "Snowflake"),
    ("find-e04", "exact", "test", "Who is the security reviewer for Wicket?", ["f033"], "Mireille Sato"),
    ("find-e05", "exact", "test", "What is the target date of release 3.1?", ["f057"], "2026-12-08"),
    ("find-e06", "exact", "test", "What is the CI label for quarantining a flaky test?", ["f063"], "flaky-quarantine"),
    ("find-e07", "exact", "test", "What paging tool does on-call use?", ["f069"], "PagerDuty"),
    ("find-e08", "exact", "test", "What ticket prefix does the project board use?", ["f074"], "ORR"),
    ("find-e09", "exact", "test", "What is Bramble's beta channel?", ["f019"], "TestFlight group orrin-insiders"),
    ("find-e10", "exact", "test", "What is Bramble's app store rating?", ["f083"], "4.6"),
    ("find-e11", "exact", "dev", "Which repository holds Harrow?", ["f005"], "orrin/harrow-billing"),
    ("find-e12", "exact", "dev", "What is the code review turnaround target?", ["f067"], "within one working day"),

    ("find-r01", "reworded", "test", "Which line of code should a fix for Harrow target so it ships next?", ["f001"], "release/2.4"),
    ("find-r02", "reworded", "test", "Who makes the final engineering calls on Harrow?", ["f002"], "Ilse Varga"),
    ("find-r03", "reworded", "test", "Where does Harrow keep its records? What storage engine?", ["f003"], "PostgreSQL 16"),
    ("find-r04", "reworded", "test", "When am I allowed to ship Harrow to production?", ["f004"], "Tuesdays 14:00-16:00 UTC"),
    ("find-r05", "reworded", "test", "Where is Quill served from?", ["f010"], "Netlify"),
    ("find-r06", "reworded", "test", "Who has to sign off on Quill wording edits?", ["f012"], "Rosa Lindqvist"),
    ("find-r07", "reworded", "test", "On which weekday do Quill updates go live?", ["f013"], "Thursday afternoon"),
    ("find-r08", "reworded", "test", "What is Bramble built with?", ["f015"], "React Native 0.76"),
    ("find-r09", "reworded", "test", "Who uploads new Bramble builds for Apple and Google approval?", ["f016"], "Kofi Mensah"),
    ("find-r10", "reworded", "test", "What is the oldest iPhone software Bramble still supports?", ["f017"], "iOS 16"),
    ("find-r11", "reworded", "test", "What technology sits underneath Lantern's queries?", ["f021"], "OpenSearch 2.15"),
    ("find-r12", "reworded", "test", "How fast must Lantern answer for nearly all calls?", ["f022"], "180 ms"),
    ("find-r13", "reworded", "test", "Who is responsible for Lantern?", ["f023"], "Tomas Rheault"),
    ("find-r14", "reworded", "test", "When does Lantern rebuild its catalogue data?", ["f024"], "nightly at 02:30 UTC"),
    ("find-r15", "reworded", "test", "Where are Ferro breakages announced?", ["f028"], "#ferro-alerts"),
    ("find-r16", "reworded", "test", "Who should I ask about Ferro these days?", ["f029"], "Priya Nandakumar"),
    ("find-r17", "reworded", "test", "How long does a Wicket sign-in stay valid?", ["f031"], "15 minutes"),
    ("find-r18", "reworded", "test", "In which cluster area does Wicket run live?", ["f034"], "auth-prod"),
    ("find-r19", "reworded", "test", "Which city's clock does Dana Okafor keep?", ["f036"], "America/Chicago"),
    ("find-r20", "reworded", "test", "When is Ilse Varga away over the holidays?", ["f039"], "2026-12-18 to 2027-01-04"),
    ("find-r21", "reworded", "test", "How many workers may run simultaneously?", ["f046"], "4"),
    ("find-r22", "reworded", "test", "How big may a worker's patch get before it must be split?", ["f050"], "under 400 changed lines"),
    ("find-r23", "reworded", "test", "How much processor and RAM may a worker's sandboxes take?", ["f051"], "2 CPUs and 4 GB"),
    ("find-r24", "reworded", "test", "Are workers allowed to look at credentials on disk?", ["f055"], "no, never read .env files"),
    ("find-r25", "reworded", "test", "When does release 3.1 go out the door?", ["f057"], "2026-12-08"),
    ("find-r26", "reworded", "test", "From what day is release 3.1 locked for new code?", ["f059"], "2026-12-01"),
    ("find-r27", "reworded", "test", "Who is running release 3.1?", ["f060"], "Dana Okafor"),
    ("find-r28", "reworded", "test", "How many sign-offs does code review need for the primary line?", ["f066"], "2"),
    ("find-r29", "reworded", "test", "Who is holding the pager this week for on-call?", ["f070"], "Tomas Rheault"),
    ("find-r30", "reworded", "test", "How soon after an outage must the incident review be written?", ["f076"], "within 5 working days"),
    ("find-r31", "reworded", "dev", "How does the user want my updates worded?", ["f077"], "plain sentences, outcome first"),
    ("find-r32", "reworded", "dev", "What should I confirm with the user before going ahead?", ["f079"], "anything that deletes data or touches production"),
    ("find-r33", "reworded", "dev", "When may I start heavy benchmarks on the user's computer?", ["f080"], "at night only"),
    ("find-r34", "reworded", "test", "How much may the hosting bill reach under the budget?", ["f089"], "$18,000 per month"),

    ("find-i01", "indirect", "test", "The invoicing service has a bug fix ready. Which line of code should it go into?", ["f001"], "release/2.4"),
    ("find-i02", "indirect", "test", "Who is responsible for the product lookup API?", ["f023"], "Tomas Rheault"),
    ("find-i03", "indirect", "test", "How long does a sign-in stay valid at the SSO gateway?", ["f031"], "15 minutes"),
    ("find-i04", "indirect", "test", "What is the customer phone app built with?", ["f015"], "React Native 0.76"),
    ("find-i05", "indirect", "test", "Where is the public docs site served from?", ["f010"], "Netlify"),
    ("find-i06", "indirect", "test", "Which scheduler drives the nightly analytics load?", ["f026"], "Airflow 2.9"),
    ("find-i07", "indirect", "test", "Who makes the final engineering calls on the invoicing service?", ["f002"], "Ilse Varga"),
    ("find-i08", "indirect", "test", "What is the oldest iPhone software the customer phone app still supports?", ["f017"], "iOS 16"),
    ("find-i09", "indirect", "test", "Who is holding the pager this week?", ["f070"], "Tomas Rheault"),
    ("find-i10", "indirect", "test", "Who is running the December ship?", ["f060"], "Dana Okafor"),
    ("find-i11", "indirect", "test", "How many helpers may I have going simultaneously?", ["f046"], "4"),
    ("find-i12", "indirect", "test", "When may I start heavy benchmarks on the computer of the person I work for?", ["f080"], "at night only"),
    ("find-i13", "indirect", "test", "When is the staff engineer away over the holidays?", ["f039"], "2026-12-18 to 2027-01-04"),
    ("find-i14", "indirect", "test", "Where do I hear about it when the warehouse-to-analytics job breaks?", ["f028"], "#ferro-alerts"),
    ("find-i15", "indirect", "dev", "Which outside company holds user accounts for the login gateway?", ["f032"], "Okta"),
    ("find-i16", "indirect", "dev", "When does the product lookup API rebuild its catalogue?", ["f024"], "nightly at 02:30 UTC"),
    ("find-i17", "indirect", "test", "What stats tool counts visits to the docs site?", ["f086"], "Plausible"),

    ("find-p01", "partial", "test", "Where in k8s does the SSO gateway live?", ["f034"], "auth-prod"),
    ("find-p02", "partial", "test", "Which Postgres major does the invoicing service run?", ["f003"], "PostgreSQL 16"),
    ("find-p03", "partial", "test", "What is the address of stg?", ["f071"], "https://staging.orrin.example"),
    ("find-p04", "partial", "test", "What pager app wakes people up at night?", ["f069"], "PagerDuty"),
    ("find-p05", "partial", "test", "Who is our sec eng?", ["f043"], "Mireille Sato"),
    ("find-p06", "partial", "test", "Which eng works on iOS and Android?", ["f042"], "Kofi Mensah"),
    ("find-p07", "partial", "test", "Where is the outage write-up tmpl?", ["f075"], "docs/postmortems/TEMPLATE.md"),
    ("find-p08", "partial", "dev", "What is the ETL ret period?", ["f084"], "13 months"),
    ("find-p09", "partial", "test", "What is our DS called?", ["f087"], "Pebble"),
    ("find-p10", "partial", "test", "Where do we file tix?", ["f073"], "Linear, team ORR"),
    ("find-p11", "partial", "test", "Which ccys can invoices be issued in?", ["f092"], "USD, EUR and GBP"),

    ("find-a01", "absent", "test", "What is Quill's release branch?", [], ""),
    ("find-a02", "absent", "test", "Who is the tech lead for Bramble?", [], ""),
    ("find-a03", "absent", "test", "What database does Lantern use?", [], ""),
    ("find-a04", "absent", "test", "When does release 3.2 ship?", [], ""),
    ("find-a05", "absent", "test", "What is Tomas Rheault's preferred contact method?", [], ""),
    ("find-a06", "absent", "test", "What is Wicket's p95 latency target?", [], ""),
    ("find-a07", "absent", "test", "Which Slack channel gets Harrow failure alerts?", [], ""),
    ("find-a08", "absent", "test", "What is the minimum Android version Bramble supports?", [], ""),
    ("find-a09", "absent", "dev", "Who is the backup for the on-call engineer?", [], ""),
    ("find-a10", "absent", "dev", "Who owns the staging environment?", [], ""),
]

# The abbreviation or prefix each partial task relies on, and the fact word it stands for.
PARTIAL = {
    "find-p01": ("k8s", "kubernetes"), "find-p02": ("postgres", "postgresql"), "find-p03": ("stg", "staging"),
    "find-p04": ("pager", "pagerduty"), "find-p05": ("sec", "security"), "find-p06": ("eng", "engineer"),
    "find-p07": ("tmpl", "template"), "find-p08": ("ret", "retention"), "find-p09": ("ds", "design"),
    "find-p10": ("tix", "ticket"), "find-p11": ("ccys", "currency"),
}

# For each absent task, the (subject, attribute word) memory must not hold.
ABSENT = {
    "find-a01": ("Quill", "branch"), "find-a02": ("Bramble", "lead"), "find-a03": ("Lantern", "database"),
    "find-a04": ("release 3.2", ""), "find-a05": ("Tomas Rheault", "contact"), "find-a06": ("Wicket", "latency"),
    "find-a07": ("Harrow", "alert"), "find-a08": ("Bramble", "android"), "find-a09": ("on-call", "backup"),
    "find-a10": ("staging", "owner"),
}

# Save tasks: (id, kind, split, what the user says, target fact key or None, token groups). Each group is a list of
# alternatives; a fact matches when every group has an alternative in its subject, attribute and value (any case).
#   new        memory lacks it: the agent adds 1-2 facts that together match, and changes no seeded fact;
#   update     it replaces the target: the target stops being current, 1-2 new or corrected facts match, and no other
#              seeded fact changes;
#   duplicate  memory already holds it: no new fact, and no seeded fact changes except the target, whose new value
#              still matches.
SAVE = [
    ("save-n01", "new", "test", "Note for later: Lantern's search results are now cached in Redis for 60 seconds.", None, [["lantern"], ["redis"], ["60"]]),
    ("save-n02", "new", "test", "Kofi Mensah's time zone is Africa/Accra. Remember that.", None, [["kofi"], ["accra"]]),
    ("save-n03", "new", "test", "From now on Ferro's dashboards live at https://grafana.orrin.example/d/ferro.", None, [["ferro"], ["grafana"]]),
    ("save-n04", "new", "test", "Wicket now writes every admin login to the audit table auth_audit.", None, [["wicket"], ["auth_audit"]]),
    ("save-n05", "new", "test", "Bramble's Android minimum is now Android 10 (API 29).", None, [["bramble"], ["android"], ["10", "29"]]),
    ("save-n06", "new", "test", "Ilse Varga is taking over as Quill's tech lead as well, starting today.", None, [["quill"], ["ilse", "varga"]]),
    ("save-n07", "new", "test", "Remember: the 3.1 release notes go in docs/releases/3.1.md.", None, [["3.1"], ["docs/releases/3.1.md"]]),
    ("save-n08", "new", "test", "Workers have to post a short comment on the ticket when they open a pull request.", None, [["worker"], ["comment"]]),
    ("save-n09", "new", "test", "Harrow's staging copy uses the fake payment provider PayStub.", None, [["harrow"], ["paystub"]]),
    ("save-n10", "new", "test", "Rosa Lindqvist prefers to be contacted by email.", None, [["rosa", "lindqvist"], ["email"]]),
    ("save-n11", "new", "dev", "Mireille Sato's time zone is Asia/Tokyo.", None, [["mireille", "sato"], ["tokyo"]]),
    ("save-n12", "new", "dev", "Harrow's on-call channel is #harrow-oncall.", None, [["harrow"], ["#harrow-oncall"]]),

    ("save-u01", "update", "test", "Heads up: Harrow fixes should target release/2.5 now; 2.4 is closed.", "f001", [["2.5"]]),
    ("save-u02", "update", "test", "Lantern has a new owner: Joel Ashby took it over this week.", "f023", [["joel", "ashby"]]),
    ("save-u03", "update", "test", "Wicket tokens now expire after 10 minutes instead of 15.", "f031", [["10"]]),
    ("save-u04", "update", "test", "Harrow deploys moved to Wednesdays 15:00-17:00 UTC.", "f004", [["wednesday"]]),
    ("save-u05", "update", "test", "Kofi is on call this week, not Tomas.", "f070", [["kofi"]]),
    ("save-u06", "update", "test", "Raise the worker cap: up to 5 agents may run at once now.", "f046", [["5"]]),
    ("save-u07", "update", "test", "Quill moved off Netlify; it is hosted on Cloudflare Pages now.", "f010", [["cloudflare"]]),
    ("save-u08", "update", "test", "Ferro belongs to Dana Okafor now; she is the owner.", "f029", [["dana", "okafor"]]),
    ("save-u09", "update", "dev", "Quill now publishes on Fridays.", "f013", [["friday"]]),

    ("save-d01", "duplicate", "test", "FYI, Harrow stores everything in Postgres 16.", "f003", [["16"]]),
    ("save-d02", "duplicate", "test", "Bramble crashes get reported to Sentry, just so you know.", "f018", [["sentry"]]),
    ("save-d03", "duplicate", "test", "Reminder: worker containers are capped at 2 CPUs and 4 GB of memory.", "f051", [["2"], ["4"]]),
    ("save-d04", "duplicate", "test", "Wicket's logins go through Okta.", "f032", [["okta"]]),
    ("save-d05", "duplicate", "test", "Lantern re-indexes every night at 2:30 UTC.", "f024", [["2:30", "02:30"]]),
    ("save-d06", "duplicate", "test", "Dana Okafor is running release 3.1.", "f060", [["dana", "okafor"]]),
    ("save-d07", "duplicate", "dev", "Lantern runs on OpenSearch 2.15.", "f021", [["opensearch"]]),
]

STOP = set("""a an the of to in on at for by with from as is are was were be been being do does did doing done have
has had i me my we our us you your it its it's this that these those what which who whom whose when where why how
and or but if so not no can could may might must should would will shall get gets got there their them they he she
him her his hers am up out over into about after before than then just also any all each every some much many more
most own same such very too new now still""".split())

SUFFIXES = ["ations", "ation", "ings", "ing", "edly", "ers", "er", "ed", "es", "s", "ly", "ions", "ion", "ment"]


def stem(word):
    for suffix in SUFFIXES:
        if word.endswith(suffix) and len(word) - len(suffix) >= 3:
            word = word[: -len(suffix)]
            break
    return word[:-1] if word.endswith("e") and len(word) > 3 else word


def stems(text):
    return {stem(w) for w in re.findall(r"[a-z0-9]+", text.lower()) if w not in STOP}


def overlap(a, b):
    """Stems the two sets share, counting a shared 5-letter prefix as shared."""
    return {x for x in a for y in b if x == y or (len(x) >= 5 and len(y) >= 5 and x[:5] == y[:5])}


def check_find(task, facts, partial=PARTIAL, absent=ABSENT):
    tid, kind, _, question, gold, _ = task
    q = stems(question)
    for key in gold:
        _, subject, attribute, value, _ = facts[key]
        subj = stems(subject)
        attr_value = stems(f"{attribute} {value}") - subj
        if kind == "exact":
            assert overlap(q, subj) and overlap(q, stems(attribute)), f"{tid}: an exact question must name the subject and attribute"
        elif kind == "reworded":
            assert overlap(q, subj), f"{tid}: a reworded question must name the subject"
            assert not overlap(q, attr_value), f"{tid}: shares {overlap(q, attr_value)} with the attribute or value"
        elif kind in ("indirect", "partial"):
            # A partial question's abbreviation is its one allowed link to the fact.
            shared = overlap(q - ({stem(partial[tid][0])} if kind == "partial" else set()), subj | attr_value)
            assert not shared, f"{tid}: shares {shared} with the fact"
    if kind == "partial":
        short, full = partial[tid]
        assert short in re.findall(r"[a-z0-9]+", question.lower()), f"{tid}: '{short}' is not in the question"
        words = [w for key in gold for w in re.findall(r"[a-z0-9]+", " ".join(facts[key][1:4]).lower())]
        assert full in words, f"{tid}: '{full}' is not in the fact"
    if kind == "absent":
        subject, word = absent[tid]
        held = [k for k, f in facts.items() if f[1].lower() == subject.lower() and word in f[2].lower()]
        assert not held, f"{tid}: memory holds {held}"


def load_spec(path):
    """A spec's facts, filler names and tasks, in the same shapes as the constants above."""
    with gzip.open(path, "rt", encoding="utf-8") as f:
        s = json.load(f)
    return {
        "facts": [(k, subj, attr, val, tuple(h) if h else None) for k, subj, attr, val, h in s["facts"]],
        "filler": filler(s["services"], s["owners"], s["languages"], s["days"], s["org"]),
        "find": [tuple(t) for t in s["find"]],
        "partial": {k: tuple(v) for k, v in s["partial"].items()},
        "absent": {k: tuple(v) for k, v in s["absent"].items()},
        "save": [tuple(t) for t in s["save"]],
    }


def write_lines(path, rows, compress):
    text = "".join(json.dumps(row, ensure_ascii=False) + "\n" for row in rows).encode("utf-8")
    if compress:
        # No name and a zero time in the header, so the same input always gives the same bytes.
        with open(str(path) + ".gz", "wb") as raw, gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
            f.write(text)
    else:
        with open(path, "wb") as f:
            f.write(text)


def main(out, spec=None):
    FACTS, FIND, PARTIAL, ABSENT, SAVE = (spec["facts"], spec["find"], spec["partial"], spec["absent"], spec["save"]) if spec \
        else (globals()["FACTS"], globals()["FIND"], globals()["PARTIAL"], globals()["ABSENT"], globals()["SAVE"])
    extra = spec["filler"] if spec else filler()
    hand = {f[0]: f for f in FACTS}
    facts = {f[0]: f for f in FACTS + extra}
    assert len(hand) == len(FACTS) and len(facts) == len(FACTS) + len(extra), "fact keys must be unique"
    pairs = [(f[1].lower(), f[2].lower()) for f in facts.values()]
    assert len(set(pairs)) == len(pairs), "two facts share a subject and attribute"
    for task in FIND:
        assert all(k in hand for k in task[4]), f"{task[0]}: unknown gold key"
        check_find(task, facts, PARTIAL, ABSENT)
    assert {t[0] for t in FIND if t[1] == "partial"} == set(PARTIAL), "every partial task needs one PARTIAL entry"
    assert {t[0] for t in FIND if t[1] == "absent"} == set(ABSENT), "every absent task needs one ABSENT entry"
    for tid, kind, _, _, target, groups in SAVE:
        if kind != "new":
            assert target in hand, f"{tid}: unknown target"
        if kind == "duplicate":
            text = " ".join(hand[target][1:4]).lower()
            assert all(any(alt in text for alt in group) for group in groups), f"{tid}: the target does not match its groups"
    ids = [t[0] for t in FIND] + [t[0] for t in SAVE]
    assert len(set(ids)) == len(ids), "task ids must be unique"

    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    fact_rows = []
    for key, subject, attribute, value, history in facts.values():
        row = {"key": key, "subject": subject, "attribute": attribute, "value": value}
        if history:
            row["history"] = {"value": history[0], "validFrom": history[1], "changedOn": history[2]}
        fact_rows.append(row)
    write_lines(out / "facts.jsonl", fact_rows, spec is not None)
    task_rows = [{"id": tid, "type": "find", "kind": kind, "split": split, "question": question, "gold": gold, "answer": answer}
                 for tid, kind, split, question, gold, answer in FIND]
    task_rows += [{"id": tid, "type": "save", "kind": kind, "split": split, "question": said, "target": target, "expect": groups}
                  for tid, kind, split, said, target, groups in SAVE]
    write_lines(out / "tasks.jsonl", task_rows, spec is not None)
    counts = {}
    for t in FIND + SAVE:
        counts[(t[1], t[2])] = counts.get((t[1], t[2]), 0) + 1
    print(f"{len(facts)} facts ({len(FACTS)} hand-written); tasks by kind and split: "
          + ", ".join(f"{k}/{s} {n}" for (k, s), n in sorted(counts.items())))


if __name__ == "__main__":
    args = sys.argv[1:]
    main(args[0], load_spec(args[args.index("--spec") + 1]) if "--spec" in args else None)
