"""Seed a Confluence Cloud test site for the Atlassian connector (#540) from EnterpriseRAG-Bench.

The benchmark (MIT, onyx-dot-app/EnterpriseRAG-Bench) is one fictional company's data. Its
Confluence pages, and the questions answered only by them, become a small site whose layout
exercises every permission case Connapse handles:

  ENG  Engineering         open space: runbooks, design docs, release notes, guides
       Incident reviews    a page tree in ENG readable only by you and the service account;
                           its child pages inherit the restriction
       one hidden page     readable only by you, so the service account never indexes it
  OPS  Company Operations  a private space: you and the service account only

Two synthetic facts check comments and attachments: one lives only in a footer comment, the
other only in a PDF attached to a page.

    python eval/tools/seed_confluence.py build
    python eval/tools/seed_confluence.py push --site acme.atlassian.net --service-account-id <id> [--dry-run]

`build` downloads the benchmark files into eval/.cache the first time (about 1.4 GB).
`push` reads CONFLUENCE_EMAIL and CONFLUENCE_API_TOKEN (an Atlassian API token for your own
account) from the environment and never prints them. It is safe to re-run: spaces and pages
that already exist are reused.
"""

import argparse
import glob
import hashlib
import html
import json
import os
import re
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCE = os.path.join(ROOT, "eval", ".cache", "sources", "enterprise-rag-bench")
OUT = os.path.join(ROOT, "eval", ".cache", "confluence-seed")
DISTRACTORS = 70

SPACES = {
    "ENG": ("Engineering", "Runbooks, design docs, release notes and guides for Redwood's platform.", False),
    "OPS": ("Company Operations", "Policies, playbooks and business operations. Private.", True),
}

# Parent page per bucket; the first matching rule wins, and anything unmatched is a guide.
BUCKETS = [
    ("hidden", "ENG", None, re.compile(r"^Office plant care", re.I)),
    ("incidents", "ENG", "Incident reviews", re.compile(r"postmortem|incident|break-glass|failover test|emergency secret", re.I)),
    ("ops", "OPS", "Policies and playbooks", re.compile(
        r"policy|playbook|lifecycle|offer|gtm|pricing|cost|economics|procurement|vendor|exception record|"
        r"residency|governance|employee|cafeteria|office|travel|onramp|communications", re.I)),
    ("runbooks", "ENG", "Runbooks", re.compile(r"runbook|rollback|roll back|rotation|checklist|procedure|on-call|oncall|hotfix", re.I)),
    ("design", "ENG", "Design docs", re.compile(r"\bADR\b|ADR-|\bPRD\b|spec|design|contract|architecture|standard|manifest|format", re.I)),
    ("releases", "ENG", "Release notes", re.compile(r"release notes", re.I)),
    ("guides", "ENG", "Guides", re.compile(r".")),
]

WHO = {
    "hidden": "nobody: the service account can't read it, so it is never indexed",
    "incidents": "only people who can read the Incident reviews tree (you)",
    "ops": "only people with access to the Company Operations space (you)",
}
EVERYONE = "everyone linked to an Atlassian account with access to Engineering"

COMMENT_FACT = "The fallback pager rotation for perf-canary is owned by the Orchid team, escalation code ORCHID-7."
ATTACHMENT_FACT = "The 2026 GPU burst budget ceiling approved by finance is 412 thousand dollars per quarter."


# --- build -----------------------------------------------------------------------------------

def clean_title(title):
    return re.sub(r"\s+", " ", title.replace("�", "-")).strip()[:250]


def bucket_for(title):
    for name, space, parent, rule in BUCKETS:
        if rule.search(title):
            return name, space, parent
    raise AssertionError("the last rule matches everything")


def build():
    import pyarrow.parquet as pq

    # The enterprise suite's downloader fetches the pinned revision and checks its hashes.
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import build_enterprise_suite
    docs_path = [build_enterprise_suite.source("documents.parquet")]
    questions_path = [build_enterprise_suite.source("questions.parquet")]

    conf = {r["doc_id"]: r for r in pq.read_table(docs_path[0], filters=[("source_type", "=", "confluence")]).to_pylist()}
    questions = [q for q in pq.read_table(questions_path[0]).to_pylist()
                 if q["expected_doc_ids"] and all(i in conf for i in q["expected_doc_ids"])]
    gold = sorted({i for q in questions for i in q["expected_doc_ids"]})
    rest = sorted((i for i in conf if i not in set(gold)), key=lambda i: hashlib.sha1(i.encode()).hexdigest())

    pages, titles = [], set()
    for doc_id in gold + rest[:DISTRACTORS]:
        title = clean_title(conf[doc_id]["title"])
        base, n = title, 2
        while title.lower() in titles:
            title, n = f"{base} ({n})", n + 1
        titles.add(title.lower())
        bucket, space, parent = bucket_for(title)
        pages.append({"doc_id": doc_id, "title": title, "space": space, "parent": parent,
                      "bucket": bucket, "gold": doc_id in gold, "markdown": conf[doc_id]["content"]})

    by_id = {p["doc_id"]: p for p in pages}
    host = next(p for p in pages if p["bucket"] == "runbooks")
    attach_to = next(p for p in pages if p["bucket"] == "guides")
    seed = {
        "spaces": SPACES,
        "pages": pages,
        "comment": {"page": host["title"], "fact": COMMENT_FACT},
        "attachment": {"page": attach_to["title"], "file": "gpu-burst-budget-2026.pdf", "fact": ATTACHMENT_FACT},
    }
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "seed.json"), "w", encoding="utf-8") as f:
        json.dump(seed, f, ensure_ascii=False)

    lines = ["# Confluence test site: questions and who should find the answer", "",
             "Search in Connapse as each person. \"You\" is the account that ran the seeding script.", ""]
    for q in questions:
        targets = [by_id[i] for i in q["expected_doc_ids"]]
        lines.append(f"- **{q['question']}**")
        for t in targets:
            where = f"{t['space']} / {t['parent']}" if t["parent"] else f"{t['space']} (top level)"
            lines.append(f"  - Page: {t['title']} ({where}). Found by: {WHO.get(t['bucket'], EVERYONE)}.")
    lines += ["", "## Comment and attachment checks", "",
              "- **Who owns the fallback pager rotation for perf-canary, and what is the escalation code?**",
              f"  - Only in a comment on: {host['title']}. Found by: {EVERYONE}, if comments are indexed.",
              "- **What is the 2026 GPU burst budget ceiling per quarter?**",
              f"  - Only in the PDF attached to: {attach_to['title']}. Found by: {EVERYONE}, if attachments are indexed."]
    with open(os.path.join(OUT, "questions.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    counts = {}
    for p in pages:
        counts[p["bucket"]] = counts.get(p["bucket"], 0) + 1
    print(f"{len(pages)} pages ({len(gold)} answer pages, {len(pages) - len(gold)} noise), {len(questions)} questions")
    print("by bucket:", ", ".join(f"{k} {v}" for k, v in sorted(counts.items())))
    print("wrote", os.path.relpath(OUT, ROOT))


# --- Markdown to Confluence storage format ----------------------------------------------------

def inline(text):
    text = html.escape(text, quote=False)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"(?<![\w*])\*([^*\n]+)\*(?![\w*])", r"<em>\1</em>", text)
    return text


def to_storage(markdown):
    out, para, lists = [], [], []
    lines = markdown.replace("\r\n", "\n").split("\n")

    def flush():
        if para:
            out.append("<p>" + inline(" ".join(para)) + "</p>")
            para.clear()

    def close_lists(depth=0):
        while len(lists) > depth:
            out.append(f"</li></{lists.pop()}>")

    i = 0
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        if stripped.startswith("```"):
            flush(); close_lists()
            body = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                body.append(lines[i]); i += 1
            code = "\n".join(body).replace("]]>", "]]]]><![CDATA[>")
            out.append('<ac:structured-macro ac:name="code"><ac:plain-text-body><![CDATA['
                       + code + "]]></ac:plain-text-body></ac:structured-macro>")
        elif not stripped:
            flush(); close_lists()
        elif re.match(r"^#{1,6}\s", stripped):
            flush(); close_lists()
            level = min(len(stripped) - len(stripped.lstrip("#")), 6)
            out.append(f"<h{level}>{inline(stripped[level:].strip())}</h{level}>")
        elif re.match(r"^(-{3,}|\*{3,}|_{3,})$", stripped):
            flush(); close_lists()
            out.append("<hr />")
        elif stripped.startswith("|") and i + 1 < len(lines) and re.match(r"^\|?\s*:?-{2,}", lines[i + 1].strip()):
            flush(); close_lists()
            cells = lambda row: [c.strip() for c in row.strip().strip("|").split("|")]
            rows = ["<tr>" + "".join(f"<th>{inline(c)}</th>" for c in cells(line)) + "</tr>"]
            i += 2
            while i < len(lines) and lines[i].strip().startswith("|"):
                rows.append("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in cells(lines[i])) + "</tr>")
                i += 1
            out.append("<table><tbody>" + "".join(rows) + "</tbody></table>")
            continue
        elif re.match(r"^\s*([-*+]|\d+[.)])\s+", line):
            flush()
            indent = len(line) - len(line.lstrip())
            depth = indent // 2 + 1
            kind = "ol" if re.match(r"^\s*\d", line) else "ul"
            item = re.sub(r"^\s*([-*+]|\d+[.)])\s+", "", line)
            if len(lists) < depth:
                while len(lists) < depth:
                    out.append(f"<{kind}><li>"); lists.append(kind)
            else:
                close_lists(depth)
                out.append("</li><li>")
            out.append(inline(item))
        else:
            close_lists()
            para.append(stripped)
        i += 1
    flush(); close_lists()
    return "\n".join(out)


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


# --- push --------------------------------------------------------------------------------------

class Confluence:
    def __init__(self, site, email, token, dry_run):
        import requests
        self.base = "https://" + site.removeprefix("https://").strip("/") + "/wiki"
        self.http = requests.Session()
        self.http.auth = (email, token)
        self.http.headers["Accept"] = "application/json"
        self.dry_run = dry_run
        self.fake_id = 0

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

    def write(self, method, path, **kw):
        if self.dry_run:
            self.fake_id += 1
            return {"id": f"dry-{self.fake_id}", "key": kw.get("json", {}).get("key") if isinstance(kw.get("json"), dict) else None}
        return self.call(method, path, **kw)


def grant_view(c, key, space_id, account_id, dry_run):
    """Lets the account view the space, through space roles on sites that use them."""
    if dry_run:
        return
    mode = (c.call("GET", "/api/v2/space-role-mode") or {}).get("mode", "")
    if "ROLES" not in str(mode).upper():
        c.call("POST", f"/rest/api/space/{key}/permission",
               json={"subject": {"type": "user", "identifier": account_id}, "operation": {"key": "read", "target": "space"}})
        return
    roles = c.call("GET", f"/api/v2/space-roles?space-id={space_id}&limit=250")["results"]
    viewer = next((r for r in roles if r.get("name", "").strip().lower() == "viewer"), None)
    if viewer is None:
        raise RuntimeError("this site has no space role named Viewer; roles found: " + ", ".join(r.get("name", "?") for r in roles))
    # Only the entries sent are changed; everyone else's roles are left as they are.
    c.call("POST", f"/api/v2/spaces/{space_id}/role-assignments",
           json=[{"principal": {"principalType": "USER", "principalId": account_id}, "roleId": viewer["id"]}])


def push(args):
    with open(os.path.join(OUT, "seed.json"), encoding="utf-8") as f:
        seed = json.load(f)
    email, token = os.environ.get("CONFLUENCE_EMAIL"), os.environ.get("CONFLUENCE_API_TOKEN")
    if not args.dry_run and (not email or not token):
        sys.exit("Set CONFLUENCE_EMAIL and CONFLUENCE_API_TOKEN (an API token for your own Atlassian account).")
    c = Confluence(args.site, email or "", token or "", args.dry_run)
    me = "dry-me" if args.dry_run else c.call("GET", "/rest/api/user/current")["accountId"]
    sa = args.service_account_id
    manual = []

    spaces = {}
    for key, (name, description, private) in seed["spaces"].items():
        found = None if args.dry_run else c.call("GET", f"/api/v2/spaces?keys={key}")["results"]
        if found:
            spaces[key] = found[0]["id"]
            print(f"space {key}: exists")
        else:
            body = {"key": key, "name": name, "description": {"value": description, "representation": "plain"}}
            if private:
                body["createPrivateSpace"] = True
            spaces[key] = c.write("POST", "/api/v2/spaces", json=body)["id"]
            print(f"space {key}: created{' (private)' if private else ''}")
        if sa:
            try:
                grant_view(c, key, spaces[key], sa, args.dry_run)
                print(f"space {key}: service account can view")
            except RuntimeError as e:
                if "already" in str(e).lower():
                    print(f"space {key}: service account already can view")
                else:
                    manual.append(f"Give the service account the Viewer role in {name} (Space settings > Permissions): {e}")

    def find_page(space_id, title):
        if args.dry_run:
            return None
        from urllib.parse import quote
        found = c.call("GET", f"/api/v2/pages?space-id={space_id}&title={quote(title)}")["results"]
        return found[0]["id"] if found else None

    def ensure_page(space, title, storage, parent_id=None):
        existing = find_page(spaces[space], title)
        if existing:
            return existing, False
        body = {"spaceId": spaces[space], "status": "current", "title": title,
                "body": {"representation": "storage", "value": storage}}
        if parent_id:
            body["parentId"] = parent_id
        return c.write("POST", "/api/v2/pages", json=body)["id"], True

    def restrict(page_id, account_ids, label):
        users = [{"type": "known", "accountId": a} for a in account_ids]
        try:
            c.write("PUT", f"/rest/api/content/{page_id}/restriction",
                    json=[{"operation": "read", "restrictions": {"user": users, "group": []}}])
            print(f"restricted: {label}")
        except RuntimeError as e:
            manual.append(f"Restrict '{label}' to {'you and the service account' if len(account_ids) > 1 else 'only you'}: {e}")

    parents = {}
    for p in seed["pages"]:
        key = (p["space"], p["parent"])
        if p["parent"] and key not in parents:
            parents[key], created = ensure_page(p["space"], p["parent"], f"<p>{html.escape(p['parent'])}.</p>")
            if p["bucket"] == "incidents":
                if sa:
                    restrict(parents[key], [me, sa], p["parent"])
                else:
                    manual.append("Restrict 'Incident reviews' to you and the service account (lock icon > Specific people)")

    ids, fresh, made = {}, set(), 0
    for n, p in enumerate(seed["pages"], 1):
        parent_id = parents.get((p["space"], p["parent"])) if p["parent"] else None
        ids[p["title"]], created = ensure_page(p["space"], p["title"], to_storage(p["markdown"]), parent_id)
        made += created
        if created:
            fresh.add(p["title"])
        if p["bucket"] == "hidden" and created:
            restrict(ids[p["title"]], [me], p["title"])
        if n % 25 == 0:
            print(f"pages: {n}/{len(seed['pages'])}")
    print(f"pages: {made} created, {len(seed['pages']) - made} already there")

    comment = seed["comment"]
    if comment["page"] in fresh:
        c.write("POST", "/api/v2/footer-comments",
                json={"pageId": ids[comment["page"]], "body": {"representation": "storage", "value": f"<p>{html.escape(comment['fact'])}</p>"}})
        print(f"comment added on: {comment['page']}")

    att = seed["attachment"]
    pdf = tiny_pdf(["GPU burst budget 2026", "", att["fact"]])
    c.write("PUT", f"/rest/api/content/{ids[att['page']]}/child/attachment",
            headers={"X-Atlassian-Token": "no-check"}, files={"file": (att["file"], pdf, "application/pdf")})
    print(f"attachment {att['file']} on: {att['page']}")

    if manual:
        print("\nDo these by hand in Confluence:")
        for m in manual:
            print(" -", m)
    print("\nDone. Expected results per person are in", os.path.relpath(os.path.join(OUT, "questions.md"), ROOT))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("build", help="pick the pages and questions from the benchmark")
    p = sub.add_parser("push", help="create the spaces and pages on your site")
    p.add_argument("--site", required=True, help="your site, e.g. acme.atlassian.net")
    p.add_argument("--service-account-id", help="the service account's Atlassian account ID, for space access and restrictions")
    p.add_argument("--dry-run", action="store_true", help="print what would be created without calling Atlassian")
    args = ap.parse_args()
    build() if args.cmd == "build" else push(args)


if __name__ == "__main__":
    main()
