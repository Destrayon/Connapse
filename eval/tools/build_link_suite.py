"""Builds the eval harness's link-question suite (links-v1): questions whose answer is a record linked to
the one the question describes, one or two links away.

Two datasets, each a snapshot of real records and the links between them:
  links-connapse-github  Destrayon/Connapse issues and pull requests created before CUTOFF, with their
                         comments. Links: a pull request closes an issue (GraphQL closingIssuesReferences),
                         an issue is a sub-issue of another (REST sub_issues).
  links-flink-jira       Apache Flink Jira issues created in 2022 (issues.apache.org, public REST v2), with
                         comments. Links: blocks, duplicates, causes (Problem/Incident), sub-task of.
  links-connapse-github-2  The same repository's records created before CUTOFF2 (suite links-v2, CON-33's
                         retest). Questions start only from records created on or after STARTS2, which
                         links-connapse-github never used, and are sampled and split with SEED2.
  links-flink-atlassian  Suite links-v3 (CON-34): Apache Flink Jira issues created in 2023 and the FLINK space's
                         current Confluence pages (cwiki.apache.org), in one dataset so links can cross between
                         them. Links: the Jira ones plus relates, epic and a description's wiki page links; a
                         page's parent, the pages it links and the issues it names. Sampled with SEED3 and KINDS3.

The answer keys come from those APIs, never from Connapse. Records are stored as the APIs return them;
the harness renders them with Connapse's own GitHub and Jira record renderers, so the indexed text is
what the connectors would index.

Steps, in order:
    python eval/tools/build_link_suite.py links-connapse-github   # snapshot one dataset and pin it
    python eval/tools/build_link_suite.py links-flink-jira
    python eval/tools/build_link_suite.py packets                 # sample link paths, split, write writer packets
    (questions are written from each packet alone; see eval/datasets/links-connapse-github/card.md)
    python eval/tools/build_link_suite.py questions <written.jsonl> ...   # validate and commit questions
    python eval/tools/build_link_suite.py links-connapse-github-2         # links-v2: snapshot, then
    python eval/tools/build_link_suite.py packets links-connapse-github-2 # packets and
    python eval/tools/build_link_suite.py questions links-connapse-github-2 <written.jsonl>   # questions

Sampling and the tuning/held-out split are fixed by SEED and happen in `packets`, before any question is
written or any run is made. `--repin` replaces existing pins after a deliberate re-snapshot.

links-flink-atlassian rebuilds without the network from a saved snapshot, kept out of the repository:
    python eval/tools/build_link_suite.py links-flink-atlassian --snapshot=<path>/records.jsonl.xz
With --repin it fetches again and replaces that file. Without --snapshot it fetches and checks the pins.

  links-company          Suite links-company (CON-130): a company-sized set. links-flink-atlassian's records, every
                         other FLINK issue created before COMPANY_CUTOFF, and links-connapse-github-2's records in one
                         dataset. Its questions are links-v3's and links-v2's, with their answers widened to the
                         linked records the bigger corpus now holds. It rebuilds from saved snapshots only:
    python eval/tools/build_link_suite.py links-company --snapshots=<dir> [--fetch]
<dir> holds links-flink-atlassian/records.jsonl.xz, links-connapse-github-2/records.jsonl.xz and links.jsonl (each
must match its own pins) and links-company/fill.jsonl.xz (must match COMPANY_FILL_SHA). --fetch fetches the fill
again and saves it there; set COMPANY_FILL_SHA to what it prints.

Outputs: eval/.cache/datasets/<name>/<version>/records.jsonl and links.jsonl with build.json;
eval/.cache/link-packets/<name>.jsonl; eval/datasets/<name>/questions.jsonl (committed).
"""
import hashlib
import heapq
import html
import json
import lzma
import os
import random
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request
from collections import defaultdict

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SEED = 20261024
GITHUB = ("links-connapse-github", "Destrayon", "Connapse", "2026-10-01T00:00:00Z")
JIRA = ("links-flink-jira", "FLINK", '2022-01-01', '2023-01-01')
GITHUB2 = ("links-connapse-github-2", "Destrayon", "Connapse", "2026-10-27T00:00:00Z")
STARTS2 = "2026-10-01T00:00:00Z"
SEED2 = 20261027
ATLASSIAN = ("links-flink-atlassian", "FLINK", '2023-01-01', '2024-01-01', "FLINK")
SEED3 = 20261028
# links-v3's second question set: the first one's test split held no cf-parent question and only 32 Confluence
# questions (CON-34 audit). Sampled with SEED4, never from a record the first set's test split used.
SEED4 = 20261029
PER_ANSWER = 2
RESTATES = 0.5
COMPANY = "links-company"
COMPANY_CUTOFF = "2026-10-01"
# sha256 of the uncompressed fill snapshot: FLINK issues created before COMPANY_CUTOFF outside links-v3's 2023.
COMPANY_FILL_SHA = None
VERSIONS = {GITHUB[0]: "2026-10-01", JIRA[0]: "2022", GITHUB2[0]: "2026-10-27", ATLASSIAN[0]: "2023",
            COMPANY: COMPANY_CUTOFF}
SUITES = {GITHUB[0]: "links-v1", JIRA[0]: "links-v1", GITHUB2[0]: "links-v2", ATLASSIAN[0]: "links-v3",
          COMPANY: "links-company"}
BODY_CHARS = 1500
MAX_ANSWERS = 10

# kind: (source, hops, how many to sample, what the question asks for). Sampled in this order, and a record
# starts at most one question, so the scarce two-step kinds come first.
KINDS = {
    "gh-epic-delivery": ("github", 2, 8, "Ask which pull requests delivered the parts this effort was broken into."),
    "gh-siblings": ("github", 2, 15, "Ask which other pieces of work belong to the same larger effort as this one."),
    "gh-closed-by": ("github", 1, 45, "Ask which pull request fixed or implemented what this issue describes."),
    "gh-closes": ("github", 1, 25, "Ask which issue the change described here was made to resolve."),
    "gh-parent": ("github", 1, 15, "Ask which larger issue or epic this piece of work is part of."),
    "gh-subissues": ("github", 1, 8, "Ask which smaller issues this piece of work was broken into."),
    "jira-siblings": ("jira", 2, 50, "Ask which other sub-tasks belong to the same parent ticket as this one."),
    "jira-duplicate": ("jira", 1, 30, "Ask which other ticket reports the same problem as this one."),
    "jira-blocked-by": ("jira", 1, 25, "Ask which ticket is blocking this one."),
    "jira-caused-by": ("jira", 1, 20, "Ask which change or ticket caused the problem this ticket describes."),
    "jira-parent": ("jira", 1, 25, "Ask which parent ticket this sub-task belongs to."),
    "jira-subtasks": ("jira", 1, 20, "Ask which sub-tasks this ticket was broken into."),
}

# links-v3 (CON-34): Jira issues and Confluence pages in one dataset, so a link can cross from one to the other.
KINDS3 = {
    "jira-siblings": ("jira", 2, 30, "Ask which other sub-tasks belong to the same parent ticket as this one."),
    "jira-page": ("jira", 1, 30, "Ask which wiki page holds the design or proposal behind this ticket."),
    "jira-duplicate": ("jira", 1, 25, "Ask which other ticket reports the same problem as this one."),
    "jira-blocked-by": ("jira", 1, 25, "Ask which ticket is blocking this one."),
    "jira-caused-by": ("jira", 1, 15, "Ask which change or ticket caused the problem this ticket describes."),
    "jira-relates": ("jira", 1, 25, "Ask which other ticket this one is related to."),
    "jira-parent": ("jira", 1, 25, "Ask which parent ticket this sub-task belongs to."),
    "jira-subtasks": ("jira", 1, 20, "Ask which sub-tasks this ticket was broken into."),
    "cf-issues": ("confluence", 1, 30, "Ask which tickets track the work this wiki page describes."),
    "cf-links-to": ("confluence", 1, 30, "Ask which other wiki page this page refers its readers to."),
    "cf-parent": ("confluence", 1, 30, "Ask which page this wiki page sits under in the page tree."),
    "cf-children": ("confluence", 1, 20, "Ask which pages sit directly under this wiki page in the page tree."),
}

# The second set asks more of the Confluence kinds, so its held-out half can show a +0.05 gain (about 58 needed).
KINDS3B = {**KINDS3,
           "cf-issues": KINDS3["cf-issues"][:2] + (45,) + KINDS3["cf-issues"][3:],
           "cf-links-to": KINDS3["cf-links-to"][:2] + (45,) + KINDS3["cf-links-to"][3:],
           "cf-parent": KINDS3["cf-parent"][:2] + (45,) + KINDS3["cf-parent"][3:],
           "cf-children": KINDS3["cf-children"][:2] + (35,) + KINDS3["cf-children"][3:]}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def write_jsonl(path, rows):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path + ".part", "w", encoding="utf-8", newline="\n") as f:
        for row in rows:
            f.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")
    os.replace(path + ".part", path)


def read_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def dataset_dir(name):
    return os.path.join(ROOT, "eval", ".cache", "datasets", name, VERSIONS[name])


# ---------------------------------------------------------------- GitHub

def gh(path):
    for attempt in range(5):
        r = subprocess.run(["gh", "api", path], capture_output=True, text=True, encoding="utf-8")
        if r.returncode == 0:
            return json.loads(r.stdout)
        time.sleep(2 ** attempt)
    raise SystemExit(f"gh api {path} failed: {r.stderr.strip()}")


def gh_pages(path):
    page = 1
    while True:
        sep = "&" if "?" in path else "?"
        items = gh(f"{path}{sep}per_page=100&page={page}")
        if not items:
            return
        yield from items
        page += 1


def gh_graphql(query):
    r = subprocess.run(["gh", "api", "graphql", "-f", f"query={query}"], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"gh graphql failed: {r.stderr.strip()}")
    return json.loads(r.stdout)["data"]


def user(u):
    return None if u is None else {"login": u["login"], "type": u.get("type")}


def keep_issue(i):
    """Only the fields Connapse's GitHubIssue reads, so unrelated API changes don't move the snapshot."""
    return {
        "id": i["id"], "number": i["number"], "title": i["title"], "body": i.get("body"), "state": i["state"],
        "user": user(i.get("user")), "labels": [{"name": l["name"]} for l in i.get("labels") or []],
        "milestone": {"title": i["milestone"]["title"]} if i.get("milestone") else None,
        "html_url": i["html_url"], "comments": i.get("comments", 0),
        "created_at": i["created_at"], "updated_at": i["updated_at"], "closed_at": i.get("closed_at"),
        "closed_by": user(i.get("closed_by")),
        "pull_request": {"merged_at": i["pull_request"].get("merged_at")} if i.get("pull_request") else None,
        "sub_issues_summary": {"total": (i.get("sub_issues_summary") or {}).get("total", 0)},
    }


def keep_comment(c):
    return {
        "id": c["id"], "body": c.get("body"), "user": user(c.get("user")),
        "created_at": c["created_at"], "updated_at": c["updated_at"],
        "issue_url": c.get("issue_url"), "pull_request_url": c.get("pull_request_url"), "path": c.get("path"),
    }


def build_github(spec=GITHUB):
    name, owner, repo, cutoff = spec
    base = f"repos/{owner}/{repo}"
    records = {}
    for i in gh_pages(f"{base}/issues?state=all&direction=asc"):
        if i["created_at"] < cutoff:
            records[i["number"]] = {"issue": keep_issue(i), "comments": [], "parent": None, "children": []}
    print(f"{name}: {len(records)} issues and pull requests before {cutoff}", flush=True)

    for review, endpoint, url_key in ((False, "issues/comments", "issue_url"), (True, "pulls/comments", "pull_request_url")):
        for c in gh_pages(f"{base}/{endpoint}?sort=created&direction=asc"):
            if c["created_at"] >= cutoff:
                continue
            number = int(c[url_key].rstrip("/").rsplit("/", 1)[1])
            if number in records:
                records[number]["comments"].append({"review": review, "comment": keep_comment(c)})

    links = []
    for number, r in records.items():
        if r["issue"]["sub_issues_summary"]["total"] > 0:
            children = sorted({c["number"] for c in gh_pages(f"{base}/issues/{number}/sub_issues") if c["number"] in records})
            r["children"] = children
            for child in children:
                records[child]["parent"] = number
                links.append({"from": f"issues/{child}", "to": f"issues/{number}", "type": "parent"})

    cursor = None
    while True:
        after = f', after:"{cursor}"' if cursor else ""
        data = gh_graphql(
            f'{{repository(owner:"{owner}",name:"{repo}"){{pullRequests(first:100{after}){{pageInfo{{hasNextPage endCursor}}'
            f' nodes{{number closingIssuesReferences(first:50){{nodes{{number repository{{nameWithOwner}}}}}}}}}}}}}}')
        conn = data["repository"]["pullRequests"]
        for pr in conn["nodes"]:
            for target in pr["closingIssuesReferences"]["nodes"]:
                if (target["repository"]["nameWithOwner"] == f"{owner}/{repo}"
                        and pr["number"] in records and target["number"] in records):
                    links.append({"from": f"pulls/{pr['number']}", "to": f"issues/{target['number']}", "type": "closes"})
        if not conn["pageInfo"]["hasNextPage"]:
            break
        cursor = conn["pageInfo"]["endCursor"]

    rows = []
    for number in sorted(records):
        r = records[number]
        r["comments"].sort(key=lambda c: (c["comment"]["created_at"], c["review"], c["comment"]["id"]))
        rows.append({"id": record_id(r["issue"]), **r})
    finish(name, rows, links)


def record_id(issue):
    return f"{'pulls' if issue.get('pull_request') else 'issues'}/{issue['number']}"


# ---------------------------------------------------------------- Jira

JIRA_FIELDS = ("summary,description,issuetype,status,priority,assignee,reporter,labels,components,fixVersions,"
               "parent,issuelinks,subtasks,created,updated,comment")


def jira_search(jql, start, fields=None):
    q = urllib.parse.urlencode({"jql": jql, "startAt": start, "maxResults": 100, "fields": fields or JIRA_FIELDS})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(f"https://issues.apache.org/jira/rest/api/2/search?{q}", timeout=120) as r:
                return json.load(r)
        except OSError:
            time.sleep(2 ** attempt)
    raise SystemExit(f"Jira search failed at {start}")


def build_jira():
    name, project, start_date, end_date = JIRA
    jql = f'project={project} AND created >= "{start_date}" AND created < "{end_date}" ORDER BY key ASC'
    issues, start = [], 0
    while True:
        page = jira_search(jql, start)
        issues += page["issues"]
        start += len(page["issues"])
        if start >= page["total"] or not page["issues"]:
            break
    print(f"{name}: {len(issues)} issues", flush=True)
    keys = {i["key"] for i in issues}

    links = []
    for i in issues:
        f = i["fields"]
        for l in f.get("issuelinks") or []:
            outward = l.get("outwardIssue")
            if not outward or outward["key"] not in keys:
                continue  # each link is recorded once, from its outward side
            kind = {"Blocker": "blocks", "Duplicate": "duplicates", "Problem/Incident": "causes"}.get(l["type"]["name"])
            if kind:
                links.append({"from": i["key"], "to": outward["key"], "type": kind})
        if f.get("parent") and f["parent"]["key"] in keys:
            links.append({"from": i["key"], "to": f["parent"]["key"], "type": "parent"})

    rows = [{"id": i["key"], "issue": {"id": i["id"], "key": i["key"], "fields": i["fields"]}}
            for i in sorted(issues, key=lambda i: int(i["key"].split("-")[1]))]
    finish(name, rows, links)


# ---------------------------------------------------------------- Jira and Confluence (links-v3)

EPIC_LINK = "customfield_12311120"  # issues.apache.org's "Epic Link"
JIRA_LINK_KINDS = {"Blocker": "blocks", "Duplicate": "duplicates", "Problem/Incident": "causes", "Reference": "relates"}
WIKI_PAGE_ID = re.compile(r"(?:pageId=|/pages/)(\d+)")
WIKI_TITLE = re.compile(r"/display/([A-Za-z0-9_~]+)/([^\s\]|)\"'<>#?]+)")
RI_PAGE = re.compile(r"<ri:page\b([^>]*)>")
JIRA_MACRO = re.compile(r'<ac:structured-macro\b[^>]*ac:name="jira"[^>]*>(?:(?!</ac:structured-macro>).)*?'
                        r'<ac:parameter\b[^>]*ac:name="key"[^>]*>\s*([A-Za-z][A-Za-z0-9_]*-\d+)\s*</ac:parameter>', re.S)
BROWSE = re.compile(r"/browse/([A-Z][A-Z0-9_]+-\d+)")


def confluence_pages(space):
    pages, start = [], 0
    while True:
        q = urllib.parse.urlencode({"spaceKey": space, "type": "page", "start": start, "limit": 25,
                                    "expand": "ancestors,metadata.labels,body.storage,version,history"})
        for attempt in range(5):
            try:
                with urllib.request.urlopen(f"https://cwiki.apache.org/confluence/rest/api/content?{q}", timeout=120) as r:
                    page = json.load(r)
                break
            except OSError:
                time.sleep(2 ** attempt)
        else:
            raise SystemExit(f"Confluence listing failed at {start}")
        pages += page["results"]
        start += len(page["results"])
        if not page["results"] or "next" not in page.get("_links", {}):
            return pages


def wiki_targets(text, space, by_title):
    """The snapshot pages a text links to, by page id or by space and title, as the API names them."""
    out = set(WIKI_PAGE_ID.findall(text))
    for s, title in WIKI_TITLE.findall(text):
        found = by_title.get((s.upper(), urllib.parse.unquote_plus(title).strip()))
        if found:
            out.add(found)
    for attrs in RI_PAGE.findall(text):
        title = re.search(r'ri:content-title="([^"]*)"', attrs)
        other = re.search(r'ri:space-key="([^"]*)"', attrs)
        if title:
            found = by_title.get(((other.group(1) if other else space).upper(), html.unescape(title.group(1)).strip()))
            if found:
                out.add(found)
    return out


def pinned_sha(name, file):
    with open(os.path.join(ROOT, "eval", "MANIFEST.json"), encoding="utf-8") as f:
        pinned = json.load(f)["datasets"].get(name)
    return next((x["sha256"] for x in pinned["files"] if x["name"] == file), None) if pinned else None


def read_snapshot(name, path):
    """The records saved by an earlier build (CON-132), kept out of the repository because they hold
    contributors' names and comments. The APIs keep changing records after they are pinned, so a re-fetch
    cannot reproduce a pin; this copy can. Its contents must be the pinned records.jsonl byte for byte."""
    with lzma.open(path, "rb") as f:
        data = f.read()
    expected = pinned_sha(name, "records.jsonl")
    if hashlib.sha256(data).hexdigest() != expected:
        raise SystemExit(f"{name}: {path} does not hold the pinned records.jsonl (sha256 {expected})")
    return [json.loads(line) for line in data.decode("utf-8").splitlines() if line.strip()]


def build_atlassian(snapshot=None, refetch=False):
    name, project, start_date, end_date, space = ATLASSIAN
    if snapshot and not refetch:
        rows = read_snapshot(name, snapshot)
        issues = [r["issue"] for r in rows if "issue" in r]
        pages = [r["page"] for r in rows if "page" in r]
        print(f"{name}: read {len(issues)} issues, {len(pages)} pages from {snapshot}", flush=True)
    else:
        jql = f'project={project} AND created >= "{start_date}" AND created < "{end_date}" ORDER BY key ASC'
        global JIRA_FIELDS
        JIRA_FIELDS = JIRA_FIELDS + "," + EPIC_LINK
        issues, start = [], 0
        while True:
            page = jira_search(jql, start)
            issues += page["issues"]
            start += len(page["issues"])
            if start >= page["total"] or not page["issues"]:
                break
        pages = [p for p in confluence_pages(space) if p.get("status") == "current"]
        print(f"{name}: {len(issues)} issues, {len(pages)} pages", flush=True)
    keys = {i["key"] for i in issues}
    page_ids = {p["id"] for p in pages}
    by_title = {(space, p["title"].strip()): p["id"] for p in pages}

    links = []
    for i in issues:
        links += issue_links(i, keys, page_ids, by_title, space)
    for p in pages:
        links += page_links(p, keys, page_ids, by_title, space)

    rows = [{"id": i["key"], "issue": {"id": i["id"], "key": i["key"], "fields": i["fields"]}}
            for i in sorted(issues, key=lambda i: int(i["key"].split("-")[1]))]
    rows += [{"id": "pages/" + p["id"], "space": space, "page": p} for p in sorted(pages, key=lambda p: int(p["id"]))]
    finish(name, rows, links)


def issue_links(i, keys, page_ids, by_title, space):
    """A Jira issue's links to the snapshot's issues (keys) and pages, as the API states them."""
    links = []
    f = i["fields"]
    for l in f.get("issuelinks") or []:
        outward = l.get("outwardIssue")
        if not outward or outward["key"] not in keys:
            continue  # each link is recorded once, from its outward side
        kind = JIRA_LINK_KINDS.get(l["type"]["name"])
        if kind:
            links.append({"from": i["key"], "to": outward["key"], "type": kind})
    if f.get("parent") and f["parent"]["key"] in keys:
        links.append({"from": i["key"], "to": f["parent"]["key"], "type": "parent"})
    if f.get(EPIC_LINK) in keys:
        links.append({"from": i["key"], "to": f[EPIC_LINK], "type": "epic"})
    for target in wiki_targets(f.get("description") or "", space, by_title) & page_ids:
        links.append({"from": i["key"], "to": "pages/" + target, "type": "page"})
    return links


def page_links(p, keys, page_ids, by_title, space):
    """A Confluence page's links: its parent, the pages it links and the snapshot issues it names."""
    links = []
    body = p["body"]["storage"]["value"]
    if p.get("ancestors") and p["ancestors"][-1]["id"] in page_ids:
        links.append({"from": "pages/" + p["id"], "to": "pages/" + p["ancestors"][-1]["id"], "type": "parent"})
    for target in wiki_targets(body, space, by_title) & page_ids - {p["id"]}:
        links.append({"from": "pages/" + p["id"], "to": "pages/" + target, "type": "links-to"})
    for key in set(JIRA_MACRO.findall(body)) | set(BROWSE.findall(body)):
        if key.upper() in keys:
            links.append({"from": "pages/" + p["id"], "to": key.upper(), "type": "issue"})
    return links


# ---------------------------------------------------------------- company-sized (links-company, CON-130)

def company_snapshots(directory):
    return (os.path.join(directory, ATLASSIAN[0], "records.jsonl.xz"),
            os.path.join(directory, COMPANY, "fill.jsonl.xz"),
            os.path.join(directory, GITHUB2[0], "records.jsonl.xz"),
            os.path.join(directory, GITHUB2[0], "links.jsonl"))


def snapshot_lines(path, expected, what):
    """A saved snapshot's lines, streamed so a company-sized one never sits in memory whole. The pin is checked
    once the last line is read, so a caller writes to a .part file and keeps it only after reading to the end."""
    h = hashlib.sha256()
    with lzma.open(path, "rb") as f:
        for line in f:
            h.update(line)
            yield line.decode("utf-8")
    if h.hexdigest() != expected:
        raise SystemExit(f"{what}: {path} does not hold the pinned records (sha256 {expected}, read {h.hexdigest()})")


def fetch_company_fill(directory):
    """Fetches every FLINK issue created before COMPANY_CUTOFF that links-flink-atlassian's 2023 window leaves out,
    in links-flink-atlassian's row shape, and saves it sorted by issue number. Issues are spooled to disk as they
    arrive and only their offsets are kept, so the fetch holds one page of results at a time."""
    _, path, _, _ = company_snapshots(directory)
    project, start_date, end_date = ATLASSIAN[1], ATLASSIAN[2], ATLASSIAN[3]
    jql = (f'project={project} AND created < "{COMPANY_CUTOFF}" AND (created < "{start_date}" OR created >= "{end_date}")'
           ' ORDER BY key ASC')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    spool, offsets, start, total = path + ".spool", {}, 0, None
    with open(spool, "wb") as out:
        while total is None or start < total:
            page = jira_search(jql, start, JIRA_FIELDS + "," + EPIC_LINK)
            total = page["total"]
            if not page["issues"]:
                break
            for i in page["issues"]:
                row = {"id": i["key"], "issue": {"id": i["id"], "key": i["key"], "fields": i["fields"]}}
                data = (json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")
                number = int(i["key"].split("-")[1])
                if number in offsets:
                    raise SystemExit(f"{COMPANY}: {i['key']} came back twice; fetch again")
                offsets[number] = (out.tell(), len(data))
                out.write(data)
            start += len(page["issues"])
            print(f"{COMPANY}: fetched {start} of {total}", flush=True)
            time.sleep(1)  # the ASF publishes no rate limit; stay gentle
    h = hashlib.sha256()
    with open(spool, "rb") as f, lzma.open(path + ".part", "wb", preset=9) as xz:
        for number in sorted(offsets):
            f.seek(offsets[number][0])
            data = f.read(offsets[number][1])
            h.update(data)
            xz.write(data)
    os.replace(path + ".part", path)
    os.remove(spool)
    print(f"{COMPANY}: {len(offsets)} issues saved to {path}; pin COMPANY_FILL_SHA = \"{h.hexdigest()}\"")


def build_company(directory):
    """Merges the three snapshots into one dataset, streamed: pass 1 checks every snapshot against its pin and
    collects what links need (issue keys, page titles); pass 2 writes records.jsonl, Jira issues by number, then
    pages, then GitHub records, each line exactly as its snapshot holds it, and collects the links."""
    if COMPANY_FILL_SHA is None:
        raise SystemExit(f"{COMPANY}: no fill is pinned; run with --fetch and set COMPANY_FILL_SHA")
    v3, fill, gh_records, gh_links = company_snapshots(directory)
    if sha256(gh_links) != pinned_sha(GITHUB2[0], "links.jsonl"):
        raise SystemExit(f"{GITHUB2[0]}: {gh_links} does not hold the pinned links.jsonl")
    space = ATLASSIAN[4]
    sources = {"v3": (v3, pinned_sha(ATLASSIAN[0], "records.jsonl"), ATLASSIAN[0]),
               "fill": (fill, COMPANY_FILL_SHA, COMPANY + " fill"),
               "github": (gh_records, pinned_sha(GITHUB2[0], "records.jsonl"), GITHUB2[0])}

    keys, pages = set(), []
    for source in ("v3", "fill"):
        for line in snapshot_lines(*sources[source]):
            row = json.loads(line)
            if "page" in row:
                pages.append((row["page"]["id"], row["page"]["title"].strip()))
            elif row["id"] in keys:
                raise SystemExit(f"{COMPANY}: {row['id']} is in more than one snapshot")
            else:
                keys.add(row["id"])
    for _ in snapshot_lines(*sources["github"]):
        pass
    page_ids = {p for p, _ in pages}
    by_title = {(space, title): p for p, title in pages}

    def issues(source, held):
        for line in snapshot_lines(*sources[source]):
            row = json.loads(line)
            if "page" in row:
                held.append(line)  # pages come after every issue; 815 of them fit in memory
                continue
            yield int(row["id"].split("-")[1]), line, row

    directory_out = dataset_dir(COMPANY)
    os.makedirs(directory_out, exist_ok=True)
    records, links, written, held = os.path.join(directory_out, "records.jsonl"), [], 0, []
    with open(records + ".part", "w", encoding="utf-8", newline="\n") as out:
        for _, line, row in heapq.merge(issues("v3", held), issues("fill", []), key=lambda x: x[0]):
            links += issue_links(row["issue"], keys, page_ids, by_title, space)
            out.write(line)
            written += 1
        for line in held:
            links += page_links(json.loads(line)["page"], keys, page_ids, by_title, space)
            out.write(line)
            written += 1
        for line in snapshot_lines(*sources["github"]):
            out.write(line)
            written += 1
    os.replace(records + ".part", records)
    links += read_jsonl(gh_links)
    print(f"{COMPANY}: {len(keys)} issues, {len(pages)} pages, {written - len(keys) - len(pages)} GitHub records")
    finish(COMPANY, None, links, written)
    company_questions(read_jsonl(os.path.join(directory_out, "links.jsonl")))


def company_questions(links):
    """links-v3's and links-v2's questions, each answered by every record its start links to the same way in
    the bigger corpus. An answer the smaller snapshot gave always stays; a linked record that was outside it
    joins, since a retriever that finds it has found a right answer."""
    github = [l for l in links if l["from"].startswith(("issues/", "pulls/"))]
    atlassian = [l for l in links if not l["from"].startswith(("issues/", "pulls/"))]
    found = {"atlassian": candidates("atlassian", None, atlassian), "github": candidates("github", None, github)}
    out, widened = [], 0
    for name, source in ((ATLASSIAN[0], "atlassian"), (GITHUB2[0], "github")):
        for q in read_jsonl(os.path.join(ROOT, "eval", "datasets", name, "questions.jsonl")):
            answers = sorted(found[source][q["kind"]].get(q["start"], set()))
            if not set(q["answers"]) <= set(answers):
                raise SystemExit(f"{COMPANY}: {q['id']} lost answers {sorted(set(q['answers']) - set(answers))}")
            widened += len(answers) > len(q["answers"])
            out.append({**q, "answers": answers})
    if len({q["id"] for q in out}) != len(out):
        raise SystemExit(f"{COMPANY}: two source suites share a question id")
    seen = {"dev": set(), "test": set()}
    for q in out:
        seen[q["split"]].update([q["start"], *q["answers"]])
    write_jsonl(os.path.join(ROOT, "eval", "datasets", COMPANY, "questions.jsonl"), out)
    print(f"{COMPANY}: {len(out)} questions ({sum(q['split'] == 'test' for q in out)} held out), {widened} with "
          f"answers the bigger corpus added, most answers {max(len(q['answers']) for q in out)}, "
          f"{len(seen['dev'] & seen['test'])} records now in both halves")


# ---------------------------------------------------------------- pinning

def finish(name, rows, links, written=None):
    """rows is None when the caller has already streamed records.jsonl, written rows of it."""
    directory = dataset_dir(name)
    links = sorted({json.dumps(l, sort_keys=True) for l in links})
    if rows is not None:
        write_jsonl(os.path.join(directory, "records.jsonl"), rows)
        written = len(rows)
    write_jsonl(os.path.join(directory, "links.jsonl"), [json.loads(l) for l in links])
    files = {f: sha256(os.path.join(directory, f)) for f in ("records.jsonl", "links.jsonl")}
    with open(os.path.join(directory, "build.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"files": files, "records": written, "links": len(links)}, f, indent=2)
    print(f"{name}: {written} records, {len(links)} links", flush=True)


def save_snapshot(name, path):
    """Saves the pinned records as the suite's snapshot, once register has accepted them."""
    records = os.path.join(dataset_dir(name), "records.jsonl")
    if os.path.exists(path):
        try:
            with lzma.open(path, "rb") as f:
                if hashlib.sha256(f.read()).hexdigest() == sha256(records):
                    return
        except (lzma.LZMAError, EOFError):
            pass  # a damaged snapshot is replaced, so --repin can recover from it
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(records, "rb") as f, lzma.open(path + ".part", "wb", preset=9) as out:
        out.write(f.read())
    os.replace(path + ".part", path)
    print(f"{name}: snapshot saved to {path}; keep it, the pins can only be rebuilt from it")


def register(name, repin):
    manifest_path = os.path.join(ROOT, "eval", "MANIFEST.json")
    with open(manifest_path, encoding="utf-8") as f:
        manifest = json.load(f)
    with open(os.path.join(dataset_dir(name), "build.json"), encoding="utf-8") as f:
        files = json.load(f)["files"]
    questions = os.path.join(ROOT, "eval", "datasets", name, "questions.jsonl")
    if os.path.exists(questions):
        files["questions.jsonl"] = sha256(questions)
    pinned = manifest["datasets"].get(name)
    if pinned and pinned["version"] == VERSIONS[name] and not repin:
        expected = {f["name"]: f["sha256"] for f in pinned["files"]}
        changed = sorted(k for k in files if expected.get(k) != files[k])
        if changed and set(changed) != {"questions.jsonl"}:
            raise SystemExit(f"{name}: {', '.join(changed)} differ from the pins (the source changed since it was "
                             "pinned); pass --repin to replace them, which makes this a new snapshot")
    manifest["datasets"][name] = {
        "adapter": "link-questions",
        "version": VERSIONS[name],
        "tags": ["shape:record"] + (["source:jira", "source:confluence"] if name == ATLASSIAN[0]
                                    else ["source:jira", "source:confluence", "source:github"] if name == COMPANY
                                    else ["source:" + ("github" if "github" in name else "jira")]),
        "files": [{"name": f,
                   "url": f"repo:{name}/questions.jsonl" if f == "questions.jsonl" else "build:eval/tools/build_link_suite.py",
                   "sha256": h} for f, h in files.items()],
    }
    suite = manifest["suites"].setdefault(SUITES[name], [])
    if name not in suite:
        suite.append(name)
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"{name}: eval/MANIFEST.json up to date")


# ---------------------------------------------------------------- packets

def load(name):
    directory = dataset_dir(name)
    return ({r["id"]: r for r in read_jsonl(os.path.join(directory, "records.jsonl"))},
            read_jsonl(os.path.join(directory, "links.jsonl")))


def text_of(record):
    if "page" in record:
        body = re.sub(r"<[^>]+>", " ", record["page"]["body"]["storage"]["value"])
        return record["page"]["title"], re.sub(r"\s+", " ", html.unescape(body)).strip()
    if "fields" in record["issue"]:
        f = record["issue"]["fields"]
        return f.get("summary") or "", f.get("description") or ""
    return record["issue"]["title"], record["issue"].get("body") or ""


def created(record):
    return record["issue"]["created_at"]


def candidates(source, records, links):
    """Every (kind, start, answers) the snapshot supports. Answers are what the API linked, nothing else."""
    out = defaultdict(dict)
    by = defaultdict(lambda: defaultdict(set))  # by[type][from] = {to}
    rev = defaultdict(lambda: defaultdict(set))
    for l in links:
        by[l["type"]][l["from"]].add(l["to"])
        rev[l["type"]][l["to"]].add(l["from"])
    parent_of = {child: next(iter(p)) for child, p in by["parent"].items()}
    children = rev["parent"]
    if source == "github":
        for issue, prs in rev["closes"].items():
            out["gh-closed-by"][issue] = prs
        for pr, issues in by["closes"].items():
            out["gh-closes"][pr] = issues
        for child, parent in parent_of.items():
            out["gh-parent"][child] = {parent}
            siblings = children[parent] - {child}
            if siblings:
                out["gh-siblings"][child] = siblings
        for parent, kids in children.items():
            out["gh-subissues"][parent] = kids
            delivered = set().union(*(rev["closes"].get(k, set()) for k in kids))
            if delivered:
                out["gh-epic-delivery"][parent] = delivered
    elif source == "atlassian":
        for a, bs in by["duplicates"].items():
            for b in bs:
                out["jira-duplicate"].setdefault(a, set()).add(b)
                out["jira-duplicate"].setdefault(b, set()).add(a)
        for a, bs in by["relates"].items():
            for b in bs:
                out["jira-relates"].setdefault(a, set()).add(b)
                out["jira-relates"].setdefault(b, set()).add(a)
        for blocked, blockers in rev["blocks"].items():
            out["jira-blocked-by"][blocked] = blockers
        for effect, causes in rev["causes"].items():
            out["jira-caused-by"][effect] = causes
        for issue, pages in by["page"].items():
            out["jira-page"][issue] = pages
        for page, issues in by["issue"].items():
            out["cf-issues"][page] = issues
        for page, targets in by["links-to"].items():
            out["cf-links-to"][page] = targets
        for child, parent in parent_of.items():
            prefix = "cf" if child.startswith("pages/") else "jira"
            out[prefix + "-parent"][child] = {parent}
            siblings = children[parent] - {child}
            if siblings and prefix == "jira":
                out["jira-siblings"][child] = siblings
        for parent, kids in children.items():
            out[("cf-children" if parent.startswith("pages/") else "jira-subtasks")][parent] = kids
    else:
        for a, bs in by["duplicates"].items():
            for b in bs:
                out["jira-duplicate"].setdefault(a, set()).add(b)
                out["jira-duplicate"].setdefault(b, set()).add(a)
        for blocked, blockers in rev["blocks"].items():
            out["jira-blocked-by"][blocked] = blockers
        for effect, causes in rev["causes"].items():
            out["jira-caused-by"][effect] = causes
        for child, parent in parent_of.items():
            out["jira-parent"][child] = {parent}
            siblings = children[parent] - {child}
            if siblings:
                out["jira-siblings"][child] = siblings
        for parent, kids in children.items():
            out["jira-subtasks"][parent] = kids
    return out


def split_by_component(rows, seed=SEED, stratified=False):
    """Splits the questions into tuning and held-out halves so that no record is in both. Questions that share
    a record, as start or answer, form one group, and a whole group goes to whichever half is smaller, in a
    seeded order. Splitting question by question let a held-out answer be a tuning question's start or answer."""
    parent = {}

    def find(x):
        parent.setdefault(x, x)
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for r in rows:
        for record in r["answers"]:
            parent[find(record)] = find(r["start"])
    groups = {}
    for r in rows:
        groups.setdefault(find(r["start"]), []).append(r)
    order = sorted(groups)
    random.Random(seed + 1).shuffle(order)
    size = {"dev": 0, "test": 0}
    by_kind = {"dev": defaultdict(int), "test": defaultdict(int)}
    for root in order:
        if stratified:
            # The half with fewer questions of the group's commonest kind, so every kind reaches both halves.
            kind = max(sorted({r["kind"] for r in groups[root]}), key=lambda k: sum(r["kind"] == k for r in groups[root]))
            half = "dev" if (by_kind["dev"][kind], size["dev"]) <= (by_kind["test"][kind], size["test"]) else "test"
        else:
            half = "dev" if size["dev"] <= size["test"] else "test"
        for r in groups[root]:
            r["split"] = half
            by_kind[half][r["kind"]] += 1
        size[half] += len(groups[root])
    seen = {"dev": set(), "test": set()}
    for r in rows:
        seen[r["split"]].update([r["start"], *r["answers"]])
    if seen["dev"] & seen["test"]:
        raise SystemExit(f"records in both halves: {sorted(seen['dev'] & seen['test'])[:5]}")
    print(f"split: {size['dev']} tuning, {size['test']} held-out, in {len(groups)} groups")


def title_words(record):
    """A title's content words, without the proposal and ticket numbers every FLIP page and its ticket share."""
    return set(words(text_of(record)[0])) - {"flip", "a", "an", "the", "of", "to", "for", "in", "on", "and", "with"} \
        - {w for w in words(text_of(record)[0]) if w.isdigit()}


def restates(records, start, answer):
    """True when the answer's title says what the start's does, so plain search finds it without the link."""
    a, b = title_words(records[start]), title_words(records[answer])
    return bool(a and b) and len(a & b) / len(a | b) >= RESTATES


def first_set_test_records():
    """The records the first links-v3 question set's held-out half used, read from the commit that added it."""
    shown = subprocess.run(["git", "show", "8dc6f463:eval/datasets/links-flink-atlassian/questions.jsonl"],
                           cwd=ROOT, capture_output=True, text=True, encoding="utf-8", check=True).stdout
    rows = [json.loads(line) for line in shown.splitlines() if line.strip()]
    return {r for q in rows if q["split"] == "test" for r in (q["start"], *q["answers"])}


def packets(only=None, second=False):
    datasets = ((GITHUB2[0], "github", SEED2, STARTS2),) if only == GITHUB2[0] else \
        ((ATLASSIAN[0], "atlassian", SEED4 if second else SEED3, None),) if only == ATLASSIAN[0] else (
        (GITHUB[0], "github", SEED, None), (JIRA[0], "jira", SEED, None))
    for name, source, seed, starts in datasets:
        kinds = {k: (source, *v[1:]) for k, v in (KINDS3B if second else KINDS3).items()} if source == "atlassian" else KINDS
        rng = random.Random(seed)
        records, links = load(name)
        found = candidates(source, records, links)
        used = set()
        excluded = first_set_test_records() if second else set()
        rows = []
        for kind, (kind_source, hops, quota, ask) in kinds.items():
            if kind_source != source:
                continue
            pool = sorted((start, sorted(answers)) for start, answers in found[kind].items()
                          if start in records and 0 < len(answers) <= MAX_ANSWERS
                          and (starts is None or created(records[start]) >= starts))
            if second:
                pool = [(s, a) for s, a in pool if not ({s, *a} & excluded) and not any(restates(records, s, x) for x in a)]
            rng.shuffle(pool)
            per_answer = defaultdict(int)
            chosen = []
            for s, a in pool:
                if len(chosen) == quota:
                    break
                if s in used or (second and any(per_answer[x] >= PER_ANSWER for x in a)):
                    continue
                chosen.append((s, a))
                for x in a:
                    per_answer[x] += 1
            for start, answers in chosen:
                used.add(start)
                title, body = text_of(records[start])
                rows.append({
                    "id": f"{kind}-{len(rows) + 1:04d}", "kind": kind, "hops": hops,
                    "split": None,
                    "start": start, "answers": answers,
                    "packet": {"title": title, "body": body[:BODY_CHARS], "ask": ask},
                })
            print(f"{name}: {kind}: {len(chosen)} of {len(pool)} available (quota {quota})")
        split_by_component(rows, seed, stratified=second)
        path = os.path.join(ROOT, "eval", ".cache", "link-packets", f"{name}.jsonl")
        write_jsonl(path, rows)
        # What a question writer gets: the start record's text and the ask, never the answers.
        write_jsonl(path.replace(".jsonl", ".writer.jsonl"), [{"id": r["id"], **r["packet"]} for r in rows])
        print(f"{name}: {len(rows)} packets -> {path}")


# ---------------------------------------------------------------- questions

KEY = re.compile(r"(#\d+|\b[A-Z][A-Z0-9]+-\d+\b|https?://|\bPR\s*\d+|\bissue\s+\d+)", re.IGNORECASE)


def words(text):
    return re.findall(r"[a-z0-9]+", text.lower())


def copied_run(question, title, n=5):
    """True when the question repeats n or more consecutive words of the title."""
    q = " ".join(words(question))
    t = words(title)
    return any(" ".join(t[i:i + n]) in q for i in range(len(t) - n + 1))


def questions(paths):
    names = (GITHUB[0], JIRA[0])
    if paths and paths[0] in (GITHUB2[0], ATLASSIAN[0]):
        names, paths = (paths[0],), paths[1:]
    written = {}
    for path in paths:
        for row in read_jsonl(path):
            written[row["id"]] = row["question"].strip()
    for name in names:
        packets_path = os.path.join(ROOT, "eval", ".cache", "link-packets", f"{name}.jsonl")
        out, rejected = [], []
        for p in read_jsonl(packets_path):
            q = written.get(p["id"])
            reason = None
            if not q:
                reason = "missing"
            elif KEY.search(q):
                reason = "names a record number, key or URL"
            elif copied_run(q, p["packet"]["title"]):
                reason = "copies five or more consecutive title words"
            elif not 6 <= len(words(q)) <= 45:
                reason = "length outside 6-45 words"
            if reason:
                rejected.append((p["id"], reason, q))
                continue
            out.append({"id": p["id"], "question": q, "kind": p["kind"], "hops": p["hops"], "split": p["split"],
                        "start": p["start"], "answers": p["answers"]})
        write_jsonl(os.path.join(ROOT, "eval", "datasets", name, "questions.jsonl"), out)
        for r in rejected:
            print(f"{name}: rejected {r[0]} ({r[1]}): {r[2]}")
        print(f"{name}: {len(out)} questions kept, {len(rejected)} rejected")
        register(name, repin=False)


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args:
        raise SystemExit(__doc__)
    if args[0] == GITHUB[0]:
        build_github()
        register(GITHUB[0], "--repin" in sys.argv)
    elif args[0] == JIRA[0]:
        build_jira()
        register(JIRA[0], "--repin" in sys.argv)
    elif args[0] == GITHUB2[0]:
        build_github(GITHUB2)
        register(GITHUB2[0], "--repin" in sys.argv)
    elif args[0] == ATLASSIAN[0]:
        snapshot = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--snapshot=")), None)
        build_atlassian(snapshot, refetch="--repin" in sys.argv)
        register(ATLASSIAN[0], "--repin" in sys.argv)
        if snapshot:
            save_snapshot(ATLASSIAN[0], snapshot)
    elif args[0] == COMPANY:
        snapshots = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--snapshots=")), None)
        if not snapshots:
            raise SystemExit(__doc__)
        if "--fetch" in sys.argv:
            fetch_company_fill(snapshots)
        else:
            build_company(snapshots)
            register(COMPANY, "--repin" in sys.argv)
    elif args[0] == "packets":
        packets(args[1] if len(args) > 1 else None, second="--second" in sys.argv)
    elif args[0] == "questions":
        questions(args[1:])
    else:
        raise SystemExit(__doc__)
