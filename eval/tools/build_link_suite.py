"""Builds the eval harness's link-question suite (links-v1): questions whose answer is a record linked to
the one the question describes, one or two links away.

Two datasets, each a snapshot of real records and the links between them:
  links-connapse-github  Destrayon/Connapse issues and pull requests created before CUTOFF, with their
                         comments. Links: a pull request closes an issue (GraphQL closingIssuesReferences),
                         an issue is a sub-issue of another (REST sub_issues).
  links-flink-jira       Apache Flink Jira issues created in 2022 (issues.apache.org, public REST v2), with
                         comments. Links: blocks, duplicates, causes (Problem/Incident), sub-task of.

The answer keys come from those APIs, never from Connapse. Records are stored as the APIs return them;
the harness renders them with Connapse's own GitHub and Jira record renderers, so the indexed text is
what the connectors would index.

Steps, in order:
    python eval/tools/build_link_suite.py links-connapse-github   # snapshot one dataset and pin it
    python eval/tools/build_link_suite.py links-flink-jira
    python eval/tools/build_link_suite.py packets                 # sample link paths, split, write writer packets
    (questions are written from each packet alone; see eval/datasets/links-connapse-github/card.md)
    python eval/tools/build_link_suite.py questions <written.jsonl> ...   # validate and commit questions

Sampling and the tuning/held-out split are fixed by SEED and happen in `packets`, before any question is
written or any run is made. `--repin` replaces existing pins after a deliberate re-snapshot.

Outputs: eval/.cache/datasets/<name>/<version>/records.jsonl and links.jsonl with build.json;
eval/.cache/link-packets/<name>.jsonl; eval/datasets/<name>/questions.jsonl (committed).
"""
import hashlib
import json
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
VERSIONS = {GITHUB[0]: "2026-10-01", JIRA[0]: "2022"}
SUITE = "links-v1"
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


def build_github():
    name, owner, repo, cutoff = GITHUB
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


def jira_search(jql, start):
    q = urllib.parse.urlencode({"jql": jql, "startAt": start, "maxResults": 100, "fields": JIRA_FIELDS})
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


# ---------------------------------------------------------------- pinning

def finish(name, rows, links):
    directory = dataset_dir(name)
    links = sorted({json.dumps(l, sort_keys=True) for l in links})
    write_jsonl(os.path.join(directory, "records.jsonl"), rows)
    write_jsonl(os.path.join(directory, "links.jsonl"), [json.loads(l) for l in links])
    files = {f: sha256(os.path.join(directory, f)) for f in ("records.jsonl", "links.jsonl")}
    with open(os.path.join(directory, "build.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"files": files, "records": len(rows), "links": len(links)}, f, indent=2)
    print(f"{name}: {len(rows)} records, {len(links)} links", flush=True)


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
        "tags": ["shape:record", "source:" + ("github" if "github" in name else "jira")],
        "files": [{"name": f,
                   "url": f"repo:{name}/questions.jsonl" if f == "questions.jsonl" else "build:eval/tools/build_link_suite.py",
                   "sha256": h} for f, h in files.items()],
    }
    suite = manifest["suites"].setdefault(SUITE, [])
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
    if "fields" in record["issue"]:
        f = record["issue"]["fields"]
        return f.get("summary") or "", f.get("description") or ""
    return record["issue"]["title"], record["issue"].get("body") or ""


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


def packets():
    rng = random.Random(SEED)
    for name, source in ((GITHUB[0], "github"), (JIRA[0], "jira")):
        records, links = load(name)
        found = candidates(source, records, links)
        used = set()
        rows = []
        for kind, (kind_source, hops, quota, ask) in KINDS.items():
            if kind_source != source:
                continue
            pool = sorted((start, sorted(answers)) for start, answers in found[kind].items()
                          if start in records and 0 < len(answers) <= MAX_ANSWERS)
            rng.shuffle(pool)
            chosen = [(s, a) for s, a in pool if s not in used][:quota]
            for index, (start, answers) in enumerate(chosen):
                used.add(start)
                title, body = text_of(records[start])
                rows.append({
                    "id": f"{kind}-{len(rows) + 1:04d}", "kind": kind, "hops": hops,
                    # Alternating after the seeded shuffle splits each kind evenly, before anything is written or run.
                    "split": "dev" if index % 2 == 0 else "test",
                    "start": start, "answers": answers,
                    "packet": {"title": title, "body": body[:BODY_CHARS], "ask": ask},
                })
            print(f"{name}: {kind}: {len(chosen)} of {len(pool)} available (quota {quota})")
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
    written = {}
    for path in paths:
        for row in read_jsonl(path):
            written[row["id"]] = row["question"].strip()
    for name in (GITHUB[0], JIRA[0]):
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
    elif args[0] == "packets":
        packets()
    elif args[0] == "questions":
        questions(args[1:])
    else:
        raise SystemExit(__doc__)
