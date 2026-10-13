"""Seed a Jira Cloud test site for the Atlassian connector (#540, #708) with restricted comments.

It adds to the site the Confluence seed already uses (docs/confluence-test-site.md):

  ENG  a company-managed software project with 30 short synthetic issues: a few linked, three
       under one epic
       one comment restricted to the group connapse-restricted-readers (you + the service account),
       with an attachment (a one-page PDF) that the comment links to
       one comment restricted to the project role "Developers"
       one comment restricted to a project role named "QA/Ops Team"
  JSM  a Jira Service Management project with one internal note, if the site has JSM

The attachment is referenced from the group comment as a link to the attachment's content URL
(ADF `link` mark), not as an ADF media node: a media node needs the Media API file id, which the
REST API doesn't return.

    python -I eval/tools/seed_jira.py push --site acme.atlassian.net --service-account-id <id> \
        [--second-user-id <id>] [--dry-run]
    python -I eval/tools/seed_jira.py check

`push` reads JIRA_EMAIL and JIRA_API_TOKEN (an Atlassian API token for your own account; the
CONFLUENCE_ ones are used when those are unset) and never prints them. Only the `requests`
package is needed. It is safe to re-run: the project, group, roles, issues and comments that
already exist are reused. Anything the API refuses is printed as a manual step at the end.
"""

import argparse
import json
import os
import sys
import time
from urllib.parse import quote

PROJECT_KEY = "ENG"
PROJECT_NAME = "Engineering"
# Company-managed (classic) software project; the team-managed templates end in "-simplified-...".
PROJECT_TEMPLATE = "com.pyxis.greenhopper.jira:basic-software-development-template"
JSM_KEY = "SUP"
JSM_NAME = "Support"
JSM_TEMPLATE = "com.atlassian.servicedesk:itil-v2-service-desk-project"
GROUP = "connapse-restricted-readers"
# Where a manual step happens, printed in front of it.
ADMIN = "Atlassian Administration"
JIRA = "Jira"
TERMINAL = "terminal"
ROLE_DEVELOPERS = "Developers"
ROLE_QA = "QA/Ops Team"

GROUP_FACT = "The cobalt failover drill for the billing queue is owned by Dana Whitfield, runbook code COBALT-41."
ROLE_FACT = "The nightly index rebuild moves to 02:15 UTC after the Juniper migration, change ticket JUNIPER-88."
QA_FACT = "Release sign-off for the mobile client needs two QA/Ops approvals, waiver code SIGNOFF-ZEBRA-3."
ATTACHMENT_FACT = "The 2026 burst compute ceiling approved by finance is 733 thousand dollars per quarter."
ATTACHMENT_FILE = "burst-compute-budget-2026.pdf"
INTERNAL_FACT = "Internal note: the vendor escalation contact for the print queue outage is Priya Ramanathan, ticket code PRINT-ECHO-12."

# (summary, description). The first is the epic; the next three are its children.
ISSUES = [
    ("Platform reliability programme", "Umbrella epic for the reliability work planned this quarter."),
    ("Add retry budget to the ingest client", "Cap retries at three per request so a slow upstream cannot stall the worker pool."),
    ("Alert when disk usage passes 80 percent", "Page the on-call engineer when any data volume crosses 80 percent used."),
    ("Rotate the staging database credentials", "Staging credentials are older than ninety days; rotate and update the secret store."),
    ("Fix the pagination offset in the audit export", "The export repeats the last row of every page when the page size is 250."),
    ("Document the canary release checklist", "Write the steps for promoting a canary build, including the rollback trigger."),
    ("Upgrade the message broker to the next minor version", "Test the upgrade on the shadow cluster before touching production."),
    ("Reduce cold start time of the thumbnail service", "Lazy-load the image codecs; target under 400 milliseconds."),
    ("Add a health endpoint for the scheduler", "Expose queue depth and last successful tick on a plain HTTP health route."),
    ("Replace the deprecated YAML parser", "The old parser rejects anchors in manifests; switch to the maintained library."),
    ("Investigate memory growth in the report renderer", "Heap grows by roughly 40 MB per hour under the weekend batch."),
    ("Write the on-call handoff template", "A one-page template covering open alerts, pending changes and known flaky tests."),
    ("Enable TLS 1.3 only on the edge proxy", "Drop older protocol versions once the partner checklist is confirmed."),
    ("Tag cloud resources with a cost centre", "Untagged resources cannot be attributed in the monthly cost report."),
    ("Speed up the nightly integration test suite", "Split the suite across four runners; it currently takes seventy minutes."),
    ("Add pagination to the team directory API", "Return a continuation token instead of the whole directory in one response."),
    ("Handle expired refresh tokens in the mobile client", "Show the sign-in screen instead of the generic network error."),
    ("Backfill missing created dates in the legacy tickets table", "About two thousand rows carry a null created date after the 2024 import."),
    ("Review the log retention policy", "Decide whether debug logs need to stay for thirty days or can drop to seven."),
    ("Add a dark theme to the admin console", "Follow the design tokens; no new colours without a design review."),
    ("Stop duplicate webhooks from the payment provider", "Deduplicate on the event id for twenty-four hours."),
    ("Create a runbook for certificate renewal", "Cover the ACME renewal path and the manual fallback for the internal CA."),
    ("Move feature flags to the new evaluation service", "Migrate flags one team at a time, starting with the search team."),
    ("Cap the size of uploaded avatars", "Reject images over two megabytes with a clear message."),
    ("Fix the timezone bug in the weekly digest email", "Digests sent to Auckland users arrive a day late."),
    ("Evaluate a columnar store for the analytics export", "Compare load time and storage cost on the March dataset."),
    ("Harden the SSH bastion configuration", "Disable password logins and require hardware-backed keys."),
    ("Add contract tests for the shipping partner API", "Pin the response shapes we depend on so a partner change fails fast."),
    ("Clean up unused IAM roles", "Remove roles with no activity in the last six months after an owner confirms."),
    ("Publish the architecture overview for new hires", "A short guide to the services, their owners and where each runbook lives."),
]
CHILDREN_OF_EPIC = (1, 2, 3)  # indexes into ISSUES
LINKS = [(5, 6), (13, 14), (20, 21)]  # (blocker index, blocked index), 'Blocks' links


# --- helpers ---------------------------------------------------------------------------------

def adf(*paragraphs):
    """An Atlassian Document Format document. A paragraph is a string or a list of ADF inline nodes."""
    content = []
    for p in paragraphs:
        nodes = [{"type": "text", "text": p}] if isinstance(p, str) else p
        content.append({"type": "paragraph", "content": nodes})
    return {"type": "doc", "version": 1, "content": content}


def link_node(text, href):
    return {"type": "text", "text": text, "marks": [{"type": "link", "attrs": {"href": href}}]}


def tiny_pdf(text_lines):
    """A one-page PDF with Helvetica text, enough for a parser to extract."""
    esc = lambda s: s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    content = "BT /F1 12 Tf 72 720 Td 16 TL " + " ".join(f"({esc(l)}) Tj T*" for l in text_lines) + " ET"
    objects = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        f"<< /Length {len(content)} >>\nstream\n{content}\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    pdf, offsets = "%PDF-1.4\n", []
    for n, body in enumerate(objects, 1):
        offsets.append(len(pdf.encode("latin-1")))
        pdf += f"{n} 0 obj\n{body}\nendobj\n"
    xref = len(pdf.encode("latin-1"))
    pdf += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n" + "".join(f"{o:010d} 00000 n \n" for o in offsets)
    pdf += f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"
    return pdf.encode("latin-1")


class Jira:
    def __init__(self, site, email, token, dry_run):
        self.base = "https://" + site.removeprefix("https://").strip("/")
        self.dry_run = dry_run
        self.fake_id = 0
        self.http = None
        if not dry_run:
            import requests
            self.http = requests.Session()
            self.http.auth = (email, token)
            self.http.headers["Accept"] = "application/json"

    def call(self, method, path, ok=(200, 201, 204), **kw):
        for attempt in range(6):
            r = self.http.request(method, self.base + path, timeout=60, **kw)
            if r.status_code == 429 or r.status_code >= 500:
                time.sleep(float(r.headers.get("Retry-After", 2 ** attempt)))
                continue
            if r.status_code not in ok:
                raise RuntimeError(f"{method} {path} -> HTTP {r.status_code}: {r.text[:300]}")
            return r.json() if r.content and "json" in r.headers.get("Content-Type", "") else None
        raise RuntimeError(f"{method} {path} kept failing; Atlassian may be rate limiting, try again later")

    def read(self, method, path, default=None, **kw):
        """A lookup. A dry run makes none and answers `default`, as if nothing exists yet."""
        if self.dry_run:
            return default
        return self.call(method, path, **kw)

    def write(self, method, path, **kw):
        if self.dry_run:
            self.fake_id += 1
            shown = kw.get("json")
            if "files" in kw:
                shown = {"file": kw["files"]["file"][0]}
            print(f"  [dry-run] {method} {path}" + (f" {json.dumps(shown, ensure_ascii=False)[:160]}" if shown is not None else ""))
            return {"id": f"dry-{self.fake_id}", "key": f"{PROJECT_KEY}-{self.fake_id}", "dry": True}
        return self.call(method, path, **kw)


def adf_text(doc):
    """All the text in an ADF document, to recognise a comment this script wrote earlier."""
    if isinstance(doc, dict):
        return (doc.get("text") or "") + "".join(adf_text(c) for c in doc.get("content", []))
    return ""


# --- push --------------------------------------------------------------------------------------

def push(args):
    email = os.environ.get("JIRA_EMAIL") or os.environ.get("CONFLUENCE_EMAIL")
    token = os.environ.get("JIRA_API_TOKEN") or os.environ.get("CONFLUENCE_API_TOKEN")
    if not args.dry_run and (not email or not token):
        sys.exit("Set JIRA_EMAIL and JIRA_API_TOKEN (an API token for your own Atlassian account).")
    j = Jira(args.site, email or "", token or "", args.dry_run)
    me = "dry-me" if args.dry_run else j.call("GET", "/rest/api/3/myself")["accountId"]
    sa, second = args.service_account_id, args.second_user_id
    manual = []  # (where it happens, what to do)

    def attempt(label, fn, manual_text, where=JIRA):
        try:
            return fn()
        except RuntimeError as e:
            if "already" in str(e).lower():
                print(f"{label}: already done")
                return None
            manual.append((where, f"{manual_text} ({e})"))
            print(f"{label}: could not do through the API")
            return None

    # Project ----------------------------------------------------------------------------------
    project = j.read("GET", f"/rest/api/3/project/{PROJECT_KEY}", ok=(200, 404))
    if project and project.get("key") == PROJECT_KEY:
        print(f"project {PROJECT_KEY}: exists")
    else:
        project = j.write("POST", "/rest/api/3/project", json={
            "key": PROJECT_KEY, "name": PROJECT_NAME, "projectTypeKey": "software", "projectTemplateKey": PROJECT_TEMPLATE,
            "leadAccountId": me, "assigneeType": "PROJECT_LEAD", "description": "Synthetic issues for the Connapse Jira connector tests."})
        print(f"project {PROJECT_KEY}: created")
        if not args.dry_run:
            project = j.call("GET", f"/rest/api/3/project/{PROJECT_KEY}")
    types = {t["name"].lower(): t["name"] for t in (project or {}).get("issueTypes", []) if not t.get("subtask")}
    task_type = types.get("task") or types.get("story") or next(iter(types.values()), "Task")
    epic_type = types.get("epic")
    if not args.dry_run and not epic_type:
        manual.append((JIRA, f"Project {PROJECT_KEY} has no Epic issue type; the three issues meant to sit under the epic are created without a parent."))

    # Group -------------------------------------------------------------------------------------
    group_id = None
    found = j.read("GET", f"/rest/api/3/group/bulk?groupName={quote(GROUP)}", default={"values": []})
    for g in found.get("values", []):
        if g["name"] == GROUP:
            group_id = g["groupId"]
    if group_id:
        print(f"group {GROUP}: exists")
    else:
        def make_group():
            return j.write("POST", "/rest/api/3/group", json={"name": GROUP})["groupId"] if not args.dry_run else \
                j.write("POST", "/rest/api/3/group", json={"name": GROUP}) and "dry-group"
        group_id = attempt(f"group {GROUP}", make_group,
                           f"Create the group {GROUP} (Directory > Groups) and add yourself and the service account "
                           "(Directory > Service accounts > ... > Add to group)", where=ADMIN)
        if group_id:
            print(f"group {GROUP}: created")
    if group_id:
        members = j.read("GET", f"/rest/api/3/group/member?groupId={quote(group_id)}&maxResults=200", default={"values": []})
        have = {m["accountId"] for m in members.get("values", [])}
        for who, label in ((me, "you"), (sa, "the service account")):
            if who and who not in have:
                attempt(f"group member ({label})",
                        lambda who=who: j.write("POST", f"/rest/api/3/group/user?groupId={quote(group_id)}", json={"accountId": who}),
                        f"Add {label} to the group {GROUP}"
                        + (" (Directory > Service accounts > ... > Add to group)" if who == sa else " (Directory > Groups)"),
                        where=ADMIN)
        if second and second in have:
            manual.append((ADMIN, f"The second user is in {GROUP}; remove them (Directory > Groups), or the group-restricted comment will not be hidden from them."))

    # Project roles ----------------------------------------------------------------------------
    global_roles = {r["name"]: r["id"] for r in j.read("GET", "/rest/api/3/role", default=[])}

    def ensure_role(name, hint):
        if name in global_roles:
            print(f"role {name}: exists")
            return global_roles[name]
        made = attempt(f"role {name}", lambda: j.write("POST", "/rest/api/3/role", json={
            "name": name, "description": "Connapse test role"})["id"], hint)
        if made:
            print(f"role {name}: created")
        return made

    role_ids = {
        ROLE_DEVELOPERS: ensure_role(ROLE_DEVELOPERS, f"Create the project role {ROLE_DEVELOPERS} (Settings > System > Project roles, or Space roles)"),
        ROLE_QA: ensure_role(ROLE_QA, f"Create the project role {ROLE_QA} (Settings > System > Project roles, or Space roles)"),
    }
    for name, role_id in role_ids.items():
        if not role_id:
            continue
        for who, label in ((me, "you"), (sa, "the service account")):
            if who:
                attempt(f"role {name} ({label})",
                        lambda who=who, role_id=role_id: j.write("POST", f"/rest/api/3/project/{PROJECT_KEY}/role/{role_id}", json={"user": [who]}),
                        f"Add {label} to the {name} role of project {PROJECT_KEY} (Project settings > Access, or Space settings > Access)")
        if second:
            manual.append((JIRA, f"Make sure the second user is not in the {name} role in {PROJECT_KEY} (Project settings > Access)."))

    # Issues -----------------------------------------------------------------------------------
    existing = {}
    token_page = None
    while not args.dry_run:
        page = j.call("GET", "/rest/api/3/search/jql", params={
            "jql": f"project = {PROJECT_KEY}", "fields": "summary,attachment", "maxResults": 100,
            **({"nextPageToken": token_page} if token_page else {})})
        for i in page.get("issues", []):
            existing[i["fields"]["summary"]] = i["key"]
        token_page = page.get("nextPageToken")
        if not token_page:
            break

    keys, fresh = {}, set()
    for n, (summary, text) in enumerate(ISSUES):
        if summary in existing:
            keys[n] = existing[summary]
            continue
        is_epic = n == 0 and epic_type
        fields = {"project": {"key": PROJECT_KEY}, "summary": summary, "description": adf(text),
                  "issuetype": {"name": epic_type if is_epic else task_type}}
        if n in CHILDREN_OF_EPIC and epic_type:
            if 0 in keys:
                fields["parent"] = {"key": keys[0]}
            else:
                manual.append((JIRA, f"Once the epic '{ISSUES[0][0]}' exists, set it as the parent of '{summary}'."))
        try:
            made = j.write("POST", "/rest/api/3/issue", json={"fields": fields})
        except RuntimeError as e:
            manual.append((JIRA, f"Create issue '{summary}' in {PROJECT_KEY}: {e}"))
            continue
        keys[n] = made["key"]
        fresh.add(n)
    print(f"issues: {len(fresh)} created, {len(ISSUES) - len(fresh)} already there")

    for blocker, blocked in LINKS:
        if blocker in fresh and blocked in fresh:
            attempt(f"link {keys[blocker]} blocks {keys[blocked]}",
                    lambda b=blocker, d=blocked: j.write("POST", "/rest/api/3/issueLink", json={
                        "type": {"name": "Blocks"}, "outwardIssue": {"key": keys[d]}, "inwardIssue": {"key": keys[b]}}),
                    f"Link {ISSUES[blocker][0]} as blocking {ISSUES[blocked][0]}")

    # Comments ---------------------------------------------------------------------------------
    def comment_exists(issue_key, marker):
        if args.dry_run:
            return False
        got = j.call("GET", f"/rest/api/3/issue/{issue_key}/comment?maxResults=100")
        return any(marker in adf_text(c.get("body")) for c in got.get("comments", []))

    def add_comment(issue_key, label, body, visibility=None, marker="", properties=None):
        if marker and comment_exists(issue_key, marker):
            print(f"comment ({label}) on {issue_key}: exists")
            return True
        payload = {"body": body}
        if visibility:
            payload["visibility"] = visibility
        if properties:
            payload["properties"] = properties
        try:
            j.write("POST", f"/rest/api/3/issue/{issue_key}/comment", json=payload)
        except RuntimeError as e:
            manual.append((JIRA, f"Add this comment to {issue_key}, visible only to {label}: \"{marker}\" ({e})"))
            return False
        print(f"comment ({label}) on {issue_key}: added")
        return True

    def add_internal_note(issue_key):
        """A JSM internal note through the documented request comment call; the comment property as a fallback."""
        marker = "PRINT-ECHO-12"
        if comment_exists(issue_key, marker):
            print(f"internal note on {issue_key}: exists")
            return True
        try:
            j.write("POST", f"/rest/servicedeskapi/request/{issue_key}/comment", json={"body": INTERNAL_FACT, "public": False})
            print(f"internal note on {issue_key}: added")
            return True
        except RuntimeError as e:
            print(f"internal note on {issue_key}: the Service Management call failed ({str(e)[:120]}); trying a comment property")
        return add_comment(issue_key, "agents only (internal)", adf(INTERNAL_FACT), marker=marker,
                           properties=[{"key": "sd.public.comment", "value": {"internal": True}}])

    def key_of(n):
        return keys.get(n, f"{PROJECT_KEY}-?")

    # The attachment goes on the issue first so the group comment can link to it.
    attach_issue = key_of(7)
    content_url = f"{j.base}/rest/api/3/attachment/content/<id>"
    have_attachment = None
    if not args.dry_run and 7 in keys:
        fields = j.call("GET", f"/rest/api/3/issue/{attach_issue}?fields=attachment")["fields"]
        have_attachment = next((a for a in fields.get("attachment", []) if a["filename"] == ATTACHMENT_FILE), None)
    if have_attachment:
        content_url = have_attachment["content"]
        print(f"attachment {ATTACHMENT_FILE} on {attach_issue}: exists")
    else:
        pdf = tiny_pdf(["Burst compute budget 2026", "", ATTACHMENT_FACT])
        try:
            up = j.write("POST", f"/rest/api/3/issue/{attach_issue}/attachments",
                         headers={"X-Atlassian-Token": "no-check"}, files={"file": (ATTACHMENT_FILE, pdf, "application/pdf")})
            if not args.dry_run:
                content_url = up[0]["content"]
            print(f"attachment {ATTACHMENT_FILE} on {attach_issue}: uploaded")
        except RuntimeError as e:
            manual.append((JIRA, f"Attach {ATTACHMENT_FILE} (a PDF containing: \"{ATTACHMENT_FACT}\") to {attach_issue}, then link it in the group-restricted comment ({e})"))

    if group_id:
        add_comment(attach_issue, f"the group {GROUP}",
                    adf(GROUP_FACT, [{"type": "text", "text": "Budget details: "}, link_node(ATTACHMENT_FILE, content_url)]),
                    {"type": "group", "identifier": group_id}, marker="COBALT-41")
    else:
        manual.append((JIRA, f"Once {GROUP} exists, comment on {attach_issue} restricted to it: \"{GROUP_FACT}\" with a link to {ATTACHMENT_FILE}"))
    if role_ids[ROLE_DEVELOPERS]:
        add_comment(key_of(8), f"the role {ROLE_DEVELOPERS}", adf(ROLE_FACT),
                    {"type": "role", "value": ROLE_DEVELOPERS}, marker="JUNIPER-88")
    else:
        manual.append((JIRA, f"Comment on {key_of(8)} restricted to the {ROLE_DEVELOPERS} role: \"{ROLE_FACT}\""))
    if role_ids[ROLE_QA]:
        add_comment(key_of(9), f"the role {ROLE_QA}", adf(QA_FACT),
                    {"type": "role", "value": ROLE_QA}, marker="SIGNOFF-ZEBRA-3")
    else:
        manual.append((JIRA, f"Comment on {key_of(9)} restricted to the {ROLE_QA} role: \"{QA_FACT}\""))

    # Jira Service Management -------------------------------------------------------------------
    jsm_done = False
    try:
        desks = j.read("GET", "/rest/servicedeskapi/servicedesk", default={"values": []})
        if not args.dry_run and not desks.get("values"):
            project_jsm = j.write("POST", "/rest/api/3/project", json={
                "key": JSM_KEY, "name": JSM_NAME, "projectTypeKey": "service_desk", "projectTemplateKey": JSM_TEMPLATE,
                "leadAccountId": me, "assigneeType": "PROJECT_LEAD"})
            print(f"project {JSM_KEY}: created")
        elif args.dry_run:
            j.write("POST", "/rest/api/3/project", json={
                "key": JSM_KEY, "name": JSM_NAME, "projectTypeKey": "service_desk", "projectTemplateKey": JSM_TEMPLATE,
                "leadAccountId": me, "assigneeType": "PROJECT_LEAD"})
        jsm_key = JSM_KEY
        if not args.dry_run:
            jsm_key = next((d["projectKey"] for d in desks.get("values", [])), JSM_KEY)
            print(f"service project {jsm_key}: ready")
        jsm_project = j.read("GET", f"/rest/api/3/project/{jsm_key}", default={"issueTypes": []})
        jsm_types = [t["name"] for t in jsm_project.get("issueTypes", []) if not t.get("subtask")]
        jsm_issue = None if args.dry_run else next(iter(
            (i["key"] for i in j.call("GET", "/rest/api/3/search/jql", params={
                "jql": f'project = {jsm_key} AND summary ~ "print queue outage"', "fields": "summary", "maxResults": 1}).get("issues", []))), None)
        if not jsm_issue:
            jsm_issue = j.write("POST", "/rest/api/3/issue", json={"fields": {
                "project": {"key": jsm_key}, "summary": "Print queue outage on the third floor",
                "issuetype": {"name": jsm_types[0] if jsm_types else "Service request"},
                "description": adf("Several people report that nothing prints from the third floor.")}})["key"]
            if args.dry_run:
                jsm_issue = f"{jsm_key}-1"
        jsm_done = add_internal_note(jsm_issue)
        if not args.dry_run:
            manual.append((JIRA, f"In the {jsm_key} service project, choose Invite team and invite the service account, so it is an agent "
                                 "and can read the internal note. If the invite fails, skip the internal note question."))
    except RuntimeError as e:
        print("service project: not created through the API")
        manual.append((JIRA, "This site may not have Jira Service Management. If it does: create a service project "
                             f"({JSM_NAME}, key {JSM_KEY}) from Projects > Create project (or Spaces > Create space), invite the service "
                             f"account with Invite team, create a request titled \"Print queue outage on the third floor\", and add an "
                             f"internal note: \"{INTERNAL_FACT}\" (API said: {e})"))

    if not sa:
        manual.append((TERMINAL, "Re-run with --service-account-id so the account joins the group and the project roles."))

    if manual:
        print("\nDo these by hand, in the place each one names:")
        for where, m in manual:
            print(f" - [{where}] {m}")
    print_expectations(attach_issue, jsm_done)


def print_expectations(attach_issue, jsm_done):
    rows = [
        ("How many retries per request does the ingest client allow?", f"{PROJECT_KEY} issue 'Add retry budget to the ingest client' (three retries)", "found", "found", "not found"),
        ("Who owns the cobalt failover drill for the billing queue, and what is the runbook code?", f"a comment restricted to {GROUP} on {attach_issue} (COBALT-41)", "found", "not found", "not found"),
        ("When does the nightly index rebuild run after the Juniper migration?", f"a comment restricted to the {ROLE_DEVELOPERS} role (02:15 UTC, JUNIPER-88)", "found", "not found", "not found"),
        ("How many QA/Ops approvals does the mobile client release sign-off need?", f"a comment restricted to the {ROLE_QA} role (two, SIGNOFF-ZEBRA-3)", "found", "not found", "not found"),
        ("What is the 2026 burst compute ceiling per quarter?", f"{ATTACHMENT_FILE}, linked from the {GROUP} comment (733 thousand dollars)", "found, if attachments are indexed", "not found", "not found"),
        ("Who is the vendor escalation contact for the print queue outage?", f"internal note on the {JSM_KEY} request (Priya Ramanathan, PRINT-ECHO-12)" + ("" if jsm_done else " (needs the manual steps above)"), "found, if internal comments are indexed", "not found", "not found"),
    ]
    print("\nSearch in Connapse as each person once the sources finish syncing. \"You\" is the account that ran this script.\n")
    print("| Question | Answer | You | Second person | Unlinked user |")
    print("|---|---|---|---|---|")
    for r in rows:
        print("| " + " | ".join(r) + " |")
    print("\nThe service account sees everything above except the internal note, unless it is a JSM agent.")


def check(_args):
    """A quick self-check of the data and the document builder; makes no calls."""
    assert len(ISSUES) == 30 and len({s for s, _ in ISSUES}) == 30, "30 distinct issue summaries"
    assert all(0 <= i < len(ISSUES) for pair in LINKS for i in pair) and all(i in range(1, 30) for i in CHILDREN_OF_EPIC)
    doc = adf("one", [link_node("two", "https://x/y")])
    assert adf_text(doc) == "onetwo" and doc["version"] == 1
    for fact, marker in ((GROUP_FACT, "COBALT-41"), (ROLE_FACT, "JUNIPER-88"), (QA_FACT, "SIGNOFF-ZEBRA-3"), (INTERNAL_FACT, "PRINT-ECHO-12")):
        assert marker in fact
    assert tiny_pdf(["a"]).startswith(b"%PDF-1.4")
    print("ok")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("check", help="run a self-check of the seed data; makes no calls")
    p = sub.add_parser("push", help="create the project, group, roles, issues and restricted comments on your site")
    p.add_argument("--site", required=True, help="your site, e.g. acme.atlassian.net")
    p.add_argument("--service-account-id", help="the service account's Atlassian account ID, for the group and the project roles")
    p.add_argument("--second-user-id", help="the second person's account ID; the script warns if they would see a restricted comment")
    p.add_argument("--dry-run", action="store_true", help="print the requests that would be made without calling Atlassian")
    args = ap.parse_args()
    check(args) if args.cmd == "check" else push(args)


if __name__ == "__main__":
    main()
