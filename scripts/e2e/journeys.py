"""The user journeys epic_e2e.py runs, in order. Each takes the shared context and the harness module.

A journey records one row per item with t.record(area, item, fn). An item passes when fn returns; it
fails with the message of the t.Check it raised (the repro). Later journeys use what earlier ones made.
"""
from __future__ import annotations

import json
import os
import subprocess
import time
from pathlib import Path

ADMIN_SCOPES = ["knowledge:read", "knowledge:write", "agent:ingest", "admin:users", "admin:audit",
                "admin:oauth-clients", "admin:settings", "admin:sources"]
AGENT_SCOPES = ["knowledge:read", "knowledge:write", "agent:ingest"]
MISSING = "00000000-0000-0000-0000-00000000e2e0"

FALCON = b"# Falcon runbook\n\nThe falcon service restarts with falconctl bounce. Pager rotation is weekly.\n"
ORCHID = b"Orchid inventory: the greenhouse keeps forty orchid varieties under misting control.\n"


def wait_ready(c, t, container_id: str, file_id: str, timeout=600) -> dict:
    def ready():
        doc = c.admin("GET", "/api/containers/{containerId}/files/{fileId}", containerId=container_id, fileId=file_id).json
        if doc.get("status") in ("FailedRetryable", "FailedPermanent"):
            raise t.Check(f"file {file_id} failed to index: {json.dumps(doc)[:400]}")
        return doc if doc.get("status") == "Ready" else None
    return t.wait_for(f"file {file_id} to be Ready", ready, timeout)


def wait_source(c, t, source_id: str, timeout=600) -> dict:
    def done():
        s = c.admin("GET", "/api/sources/{sourceId}", sourceId=source_id).json
        if s["lastSyncStatus"] == "Failed":
            raise t.Check(f"source sync failed: {s.get('lastSyncError')}")
        busy = int(s.get("processingDocumentCount") or 0)
        return s if s["lastSyncStatus"] == "Succeeded" and int(s["documentCount"]) > 0 and busy == 0 else None
    return t.wait_for(f"source {source_id} to sync and index", done, timeout)


def hits(result: dict, word: str) -> bool:
    return any(word.lower() in h["content"].lower() for h in result.get("hits", []))


# ── Tokens, users, invites ───────────────────────────────────────────────────────────────────────

def tokens(c, t):
    """Tokens: personal access tokens and scope limits"""
    A, r = c.admin, t.record
    pat = r("Tokens", "create a PAT with every admin scope", lambda: A(
        "POST", "/api/v1/auth/pats", 200, body={"name": f"e2e-admin-{c.run}", "scopes": ADMIN_SCOPES, "requiresApproval": False}).json)
    if pat:
        c.pat = A.as_({"X-Api-Key": pat["token"]})
        c.pat_token = pat["token"]
        r("Tokens", "the PAT authenticates (me)", lambda: c.pat("GET", "/api/v1/auth/me"))
    ro = r("Tokens", "create a read-only PAT", lambda: A(
        "POST", "/api/v1/auth/pats", 200, body={"name": f"e2e-read-{c.run}", "scopes": ["knowledge:read"]}).json)
    if ro:
        c.ro = A.as_({"X-Api-Key": ro["token"]})
        r("Tokens", "read-only PAT can list containers", lambda: c.ro("GET", "/api/containers"))
        # Routes that check a scope refuse it at the policy (an RFC 9110 problem); routes that check only the
        # owner's role still admit it, as docs/api.md says.
        r("Errors", "read-only PAT reading settings -> 403 (needs admin:settings)",
          lambda: c.ro("GET", "/api/settings/{category}", 403, category="chunking"))
        r("Errors", "read-only PAT reading the audit log -> 403 (needs admin:audit)",
          lambda: c.ro("GET", "/api/v1/audit", 403))
        r("Errors", "read-only PAT listing connections -> 403 (needs admin:sources)",
          lambda: c.ro("GET", "/api/v1/connections", 403))
    r("Tokens", "list PATs", lambda: t.expect(
        any(p["name"] == f"e2e-admin-{c.run}" for p in A("GET", "/api/v1/auth/pats").json), "new PAT not listed"))
    gone = r("Tokens", "create a PAT to revoke", lambda: A("POST", "/api/v1/auth/pats", 200, body={"name": f"e2e-gone-{c.run}"}).json)
    if gone:
        r("Tokens", "revoke a PAT", lambda: A("DELETE", "/api/v1/auth/pats/{id}", 204, id=gone["id"]))
        r("Tokens", "a revoked PAT is refused", lambda: A.as_({"X-Api-Key": gone["token"]})("GET", "/api/v1/auth/me", 401))
    r("Errors", "revoke a missing PAT -> 404 pat-not-found",
      lambda: A.problem("DELETE", "/api/v1/auth/pats/{id}", 404, "pat-not-found", id=MISSING))
    r("Errors", "PAT without a name -> 400 (request validation)",
      lambda: A("POST", "/api/v1/auth/pats", 400, body={"name": ""}))
    r("Errors", "PAT with an unknown scope -> 400 invalid-pat", lambda: A.problem(
        "POST", "/api/v1/auth/pats", 400, "invalid-pat", body={"name": "x", "scopes": ["admin:everything"]}))
    r("Errors", "a PAT can't make PATs -> 403 (policy)", lambda: c.pat.problem(
        "POST", "/api/v1/auth/pats", 403, None, body={"name": "x"}))


def users(c, t):
    """Users and invites"""
    A, r = c.admin, t.record
    page = r("Users", "list users (paged)", lambda: A("GET", "/api/v1/auth/users", query={"skip": 0, "take": 50}).json)
    me = A("GET", "/api/v1/auth/me").json
    r("Errors", "take=0 -> 400 invalid-page",
      lambda: A.problem("GET", "/api/v1/auth/users", 400, "invalid-page", query={"take": 0}))
    email = f"e2e-invitee-{c.run}@example.com"
    inv = r("Users", "create an invite", lambda: A("POST", "/api/v1/invites", 201, body={"email": email, "role": "Editor"}).json)
    r("Errors", "a second invite for the same email -> 409 invite-pending", lambda: A.problem(
        "POST", "/api/v1/invites", 409, "invite-pending", body={"email": email, "role": "Editor"}))
    r("Errors", "invite an existing user -> 409 user-exists", lambda: A.problem(
        "POST", "/api/v1/invites", 409, "user-exists", body={"email": c.admin_email, "role": "Editor"}))
    r("Errors", "invite with a bad role -> 400 invalid-invite", lambda: A.problem(
        "POST", "/api/v1/invites", 400, "invalid-invite", body={"email": f"x-{c.run}@example.com", "role": "Wizard"}))
    r("Users", "list invites (paged)", lambda: t.expect(
        any(i["email"] == email for i in A("GET", "/api/v1/invites").json["items"]), "invite not listed"))
    if inv:
        r("Users", "revoke an invite", lambda: A("DELETE", "/api/v1/invites/{id}", 204, id=inv["id"]))
    r("Errors", "revoke a missing invite -> 404 invite-not-found",
      lambda: A.problem("DELETE", "/api/v1/invites/{id}", 404, "invite-not-found", id=MISSING))
    r("Errors", "roles for a missing user -> 404 user-not-found", lambda: A.problem(
        "PUT", "/api/v1/auth/users/{id}/roles", 404, "user-not-found", body={"roles": ["Viewer"]}, id=MISSING))
    r("Errors", "changing your own roles -> 403 forbidden", lambda: A.problem(
        "PUT", "/api/v1/auth/users/{id}/roles", 403, "forbidden", body={"roles": ["Viewer"]}, id=me["id"]))
    r("Errors", "delete a missing user -> 404 user-not-found",
      lambda: A.problem("DELETE", "/api/v1/auth/users/{id}", 404, "user-not-found", id=MISSING))

    # A second person, made through the documented identity API, then given a role and removed.
    pw = "E2e-Second-" + c.run + "9a!"
    second = f"e2e-second-{c.run}@example.com"
    reg = r("Identity", "POST /api/v1/identity/register", lambda: c.admin(
        "POST", "/api/v1/identity/register", (200, 400), body={"email": second, "password": pw}))
    if reg and reg.status == 200:
        uid = next((u["id"] for u in A("GET", "/api/v1/auth/users", query={"take": 200}).json["items"] if u["email"] == second), None)
        if uid:
            r("Errors", "unknown role -> 400 invalid-roles", lambda: A.problem(
                "PUT", "/api/v1/auth/users/{id}/roles", 400, "invalid-roles", body={"roles": ["Wizard"]}, id=uid))
            r("Users", "set a user's roles", lambda: A("PUT", "/api/v1/auth/users/{id}/roles", 204, body={"roles": ["Viewer"]}, id=uid))
            r("Users", "delete a user", lambda: A("DELETE", "/api/v1/auth/users/{id}", 204, id=uid))
    del page


def identity(c, t):
    """Identity endpoints (ASP.NET Identity API under /api/v1/identity)"""
    A, anon, r = c.admin, c.admin.as_({}), t.record
    login = r("Identity", "login (bearer) [known: CON-166]", lambda: anon(
        "POST", "/api/v1/identity/login", 200, body={"email": c.admin_email, "password": c.admin_password}).json)
    if login:
        r("Identity", "refresh", lambda: anon("POST", "/api/v1/identity/refresh", 200, body={"refreshToken": login["refreshToken"]}))
    r("Identity", "manage/info GET", lambda: A("GET", "/api/v1/identity/manage/info", 200))
    r("Identity", "manage/info POST (no change)", lambda: A("POST", "/api/v1/identity/manage/info", 200, body={}))
    r("Identity", "manage/2fa POST (read state)", lambda: A("POST", "/api/v1/identity/manage/2fa", 200, body={}))
    r("Identity", "forgotPassword", lambda: anon("POST", "/api/v1/identity/forgotPassword", 200, body={"email": c.admin_email}))
    r("Identity", "resendConfirmationEmail", lambda: anon(
        "POST", "/api/v1/identity/resendConfirmationEmail", 200, body={"email": c.admin_email}))
    r("Identity", "resetPassword with a bad code -> 400", lambda: anon(
        "POST", "/api/v1/identity/resetPassword", 400, body={"email": c.admin_email, "resetCode": "bad", "newPassword": "Xx1!xxxxxx"}))
    r("Identity", "confirmEmail with a bad code [known: CON-166]", lambda: anon(
        "GET", "/api/v1/identity/confirmEmail", (200, 401), query={"userId": MISSING, "code": "bad"}))


# ── Agents ───────────────────────────────────────────────────────────────────────────────────────

def agents(c, t):
    """Agents and agent keys"""
    A, r = c.admin, t.record
    name = f"e2e-agent-{c.run}"
    agent = r("Agents", "create an agent", lambda: A("POST", "/api/v1/agents", 201, body={"name": name, "description": "e2e"}).json)
    r("Errors", "duplicate agent name -> 409 agent-exists",
      lambda: A.problem("POST", "/api/v1/agents", 409, "agent-exists", body={"name": name}))
    r("Errors", "bad agent name -> 400 invalid-agent",
      lambda: A.problem("POST", "/api/v1/agents", 400, "invalid-agent", body={"name": "no spaces!"}))
    r("Errors", "missing agent -> 404 agent-not-found",
      lambda: A.problem("GET", "/api/v1/agents/{id}", 404, "agent-not-found", id=MISSING))
    if not agent:
        return
    c.agent_id = agent["id"]
    r("Agents", "list agents", lambda: t.expect(any(a["id"] == agent["id"] for a in A("GET", "/api/v1/agents").json), "not listed"))
    r("Agents", "get an agent", lambda: A("GET", "/api/v1/agents/{id}", id=agent["id"]))
    key = r("Agents", "create a key without approval (all agent scopes)", lambda: A(
        "POST", "/api/v1/agents/{id}/keys", 201, id=agent["id"],
        body={"name": "e2e-full", "scopes": AGENT_SCOPES, "requiresApproval": False}).json)
    if key:
        c.agent_key = key["token"]
    held = r("Agents", "create a key that needs approval (default)", lambda: A(
        "POST", "/api/v1/agents/{id}/keys", 201, id=agent["id"], body={"name": "e2e-held", "scopes": AGENT_SCOPES}).json)
    if held:
        c.held_key = held["token"]
    ro = r("Agents", "create a read-only key", lambda: A(
        "POST", "/api/v1/agents/{id}/keys", 201, id=agent["id"],
        body={"name": "e2e-read", "scopes": ["knowledge:read"], "requiresApproval": False}).json)
    if ro:
        c.agent_ro_key = ro["token"]
    r("Errors", "agent key with an admin scope -> 400 invalid-agent-key", lambda: A.problem(
        "POST", "/api/v1/agents/{id}/keys", 400, "invalid-agent-key", id=agent["id"], body={"name": "x", "scopes": ["admin:users"]}))
    keys = r("Agents", "list keys", lambda: A("GET", "/api/v1/agents/{id}/keys", id=agent["id"]).json)
    spare = r("Agents", "create a key to revoke", lambda: A(
        "POST", "/api/v1/agents/{id}/keys", 201, id=agent["id"], body={"name": "e2e-spare"}).json)
    if spare:
        r("Agents", "revoke a key", lambda: A("DELETE", "/api/v1/agents/{id}/keys/{keyId}", 204, id=agent["id"], keyId=spare["keyId"]))
    r("Errors", "revoke a missing key -> 404 agent-key-not-found", lambda: A.problem(
        "DELETE", "/api/v1/agents/{id}/keys/{keyId}", 404, "agent-key-not-found", id=agent["id"], keyId=MISSING))
    # A second agent to disable, re-enable and delete, so the first keeps working for MCP.
    other = r("Agents", "create a second agent", lambda: A("POST", "/api/v1/agents", 201, body={"name": name + "-b"}).json)
    if other:
        r("Agents", "disable an agent", lambda: A("PUT", "/api/v1/agents/{id}/active", 204, body={"isActive": False}, id=other["id"]))
        r("Agents", "disabled agent reads as inactive", lambda: t.expect(
            A("GET", "/api/v1/agents/{id}", id=other["id"]).json["isActive"] is False, "still active"))
        r("Agents", "enable an agent", lambda: A("PUT", "/api/v1/agents/{id}/active", 204, body={"isActive": True}, id=other["id"]))
        r("Agents", "delete an agent", lambda: A("DELETE", "/api/v1/agents/{id}", 204, id=other["id"]))
    del keys


# ── Containers, files, search ────────────────────────────────────────────────────────────────────

def containers(c, t):
    """Containers, files, indexing and search"""
    A, r = c.admin, t.record
    name = f"e2e-docs-{c.run}"
    box = r("Containers", "create a container", lambda: A("POST", "/api/containers", 201, body={"name": name, "description": "e2e"}).json)
    r("Errors", "duplicate container -> 409 container-conflict",
      lambda: A.problem("POST", "/api/containers", 409, "container-conflict", body={"name": name}))
    r("Errors", "bad container name -> 400 invalid-container",
      lambda: A.problem("POST", "/api/containers", 400, "invalid-container", body={"name": "Bad Name!"}))
    r("Errors", "missing container -> 404 container-not-found",
      lambda: A.problem("GET", "/api/containers/{containerId}", 404, "container-not-found", containerId=MISSING))
    if not box:
        return
    cid = c.container_id = box["id"]
    c.container_name = name
    r("Containers", "list containers (paged)", lambda: t.expect(
        any(x["id"] == cid for x in A("GET", "/api/containers", query={"skip": 0, "take": 200}).json["items"]), "not listed"))
    r("Containers", "get a container", lambda: A("GET", "/api/containers/{containerId}", containerId=cid))
    r("Containers", "get a container by name", lambda: A("GET", "/api/containers/by-name/{name}", name=name))
    r("Containers", "rename/describe a container", lambda: A(
        "PATCH", "/api/containers/{containerId}", 200, body={"description": "e2e renamed"}, containerId=cid))
    r("Containers", "settings get", lambda: A("GET", "/api/containers/{containerId}/settings", containerId=cid))
    r("Containers", "settings put", lambda: A(
        "PUT", "/api/containers/{containerId}/settings", 200, body={"search": {"topK": 7}}, containerId=cid))
    r("Containers", "settings reset", lambda: A("DELETE", "/api/containers/{containerId}/settings", 204, containerId=cid))

    # Files
    body, ctype = t.multipart({"path": "/guides", "strategy": "Recursive"},
                              [("files", "falcon.md", FALCON), ("files", "orchid.txt", ORCHID)])
    up = r("Files", "upload two files (multipart)", lambda: A(
        "POST", "/api/containers/{containerId}/files", 200, body=body, headers={"Content-Type": ctype}, containerId=cid).json)
    if not up:
        return
    docs = {d["fileName"]: d for d in up["documents"]}
    falcon, orchid = docs["falcon.md"]["documentId"], docs["orchid.txt"]["documentId"]
    if docs["falcon.md"].get("jobId"):
        r("Files", "batch status", lambda: A("GET", "/api/batches/{id}/status", id=docs["falcon.md"]["jobId"]))
    r("Errors", "missing batch -> 404 batch-not-found",
      lambda: A.problem("GET", "/api/batches/{id}/status", 404, "batch-not-found", id=MISSING))
    if not r("Files", "files index to Ready", lambda: (wait_ready(c, t, cid, falcon), wait_ready(c, t, cid, orchid))):
        return
    r("Files", "list files in a folder", lambda: t.expect(
        any(e["name"] == "falcon.md" for e in A("GET", "/api/containers/{containerId}/files", containerId=cid,
                                                  query={"path": "/guides/"}).json["items"]), "falcon.md not listed"))
    r("Files", "get a file", lambda: A("GET", "/api/containers/{containerId}/files/{fileId}", containerId=cid, fileId=falcon))
    r("Files", "get a file's content", lambda: t.expect(
        b"falconctl" in A("GET", "/api/containers/{containerId}/files/{fileId}/content", containerId=cid, fileId=falcon).raw,
        "content lacks the text"))
    r("Files", "reindex-check", lambda: A("GET", "/api/containers/{containerId}/files/{fileId}/reindex-check", containerId=cid, fileId=falcon))
    r("Errors", "missing file -> 404 file-not-found", lambda: A.problem(
        "GET", "/api/containers/{containerId}/files/{fileId}", 404, "file-not-found", containerId=cid, fileId=MISSING))
    r("Files", "create a folder", lambda: A("POST", "/api/containers/{containerId}/folders", 201, body={"path": "/empty/"}, containerId=cid))
    r("Errors", "same folder again -> 409 folder-exists", lambda: A.problem(
        "POST", "/api/containers/{containerId}/folders", 409, "folder-exists", body={"path": "/empty/"}, containerId=cid))
    r("Files", "delete a folder", lambda: A(
        "DELETE", "/api/containers/{containerId}/folders", 204, query={"path": "/empty/", "cascade": "true"}, containerId=cid))
    r("Errors", "delete a missing folder -> 404 folder-not-found", lambda: A.problem(
        "DELETE", "/api/containers/{containerId}/folders", 404, "folder-not-found", query={"path": "/nope/", "cascade": "true"}, containerId=cid))

    # Search
    r("Search", "GET search finds the uploaded text", lambda: t.expect(hits(A(
        "GET", "/api/containers/{containerId}/search", containerId=cid, query={"q": "how do I restart falcon", "mode": "Hybrid"}).json,
        "falconctl"), "no hit with falconctl"))
    for mode in ("Semantic", "Keyword", "Hybrid"):
        r("Search", f"POST search ({mode})", lambda mode=mode: t.expect(hits(A(
            "POST", "/api/containers/{containerId}/search", body={"query": "orchid varieties greenhouse", "mode": mode, "topK": 5},
            containerId=cid).json, "orchid"), f"{mode}: no orchid hit"))
    r("Errors", "empty query -> 400 invalid-search", lambda: A.problem(
        "POST", "/api/containers/{containerId}/search", 400, "invalid-search", body={"query": ""}, containerId=cid))
    r("Errors", "search a missing container -> 404 search-scope-not-found", lambda: A.problem(
        "POST", "/api/containers/{containerId}/search", 404, "search-scope-not-found", body={"query": "x"}, containerId=MISSING))
    r("Search", "embedding models in a container", lambda: A("GET", "/api/containers/{containerId}/search/models", containerId=cid))

    # Indexing operations
    r("Containers", "stats", lambda: t.expect(int(A(
        "GET", "/api/containers/{containerId}/stats", containerId=cid).json.get("documentCount", 2)) >= 2, "stats count < 2"))
    r("Containers", "reindex (dry run)", lambda: A(
        "POST", "/api/containers/{containerId}/reindex", 200, body={"dryRun": True}, containerId=cid))
    r("Errors", "reindex with maxDocuments 0 -> 400 invalid-reindex", lambda: A.problem(
        "POST", "/api/containers/{containerId}/reindex", 400, "invalid-reindex", body={"maxDocuments": 0}, containerId=cid))
    r("Errors", "reindex with an unknown strategy -> 400 (request binding)", lambda: A(
        "POST", "/api/containers/{containerId}/reindex", 400, body={"strategy": "Nope"}, containerId=cid))
    r("Containers", "retry-failed", lambda: A("POST", "/api/containers/{containerId}/retry-failed", 200, containerId=cid))
    r("Containers", "sync (reconcile)", lambda: A("POST", "/api/containers/{containerId}/sync", 200, containerId=cid))
    r("Containers", "summaries regenerate", lambda: A("POST", "/api/containers/{containerId}/summaries", 202, containerId=cid))
    r("Containers", "summaries clear", lambda: A("DELETE", "/api/containers/{containerId}/summaries", 204, containerId=cid))
    r("Files", "retry a file", lambda: A(
        "POST", "/api/containers/{containerId}/files/{fileId}/retry", (202,), containerId=cid, fileId=orchid))
    wait_ready(c, t, cid, orchid)
    r("Errors", "delete a non-empty container -> 400 container-not-empty", lambda: A.problem(
        "DELETE", "/api/containers/{containerId}", 400, "container-not-empty", containerId=cid))
    r("Files", "delete a file", lambda: A("DELETE", "/api/containers/{containerId}/files/{fileId}", 204, containerId=cid, fileId=orchid))
    body2, ctype2 = t.multipart({}, [("files", "spare.txt", b"spare file\n")])
    spare = A("POST", "/api/containers/{containerId}/files", 200, body=body2, headers={"Content-Type": ctype2}, containerId=cid).json
    r("Files", "bulk delete", lambda: A(
        "POST", "/api/containers/{containerId}/files/bulk-delete", 200,
        body={"fileIds": [spare["documents"][0]["documentId"], MISSING]}, containerId=cid))
    c.falcon_id = falcon


# ── Settings and providers ───────────────────────────────────────────────────────────────────────

def settings(c, t):
    """Settings, with ETag concurrency"""
    A, r = c.admin, t.record
    for cat in ("embedding", "chunking", "search", "llm", "upload", "summary", "samlsignin", "identitycenter", "azure", "azuread"):
        r("Settings", f"get {cat}", lambda cat=cat: A("GET", "/api/settings/{category}", category=cat))
    r("Errors", "unknown category -> 404 settings-category-not-found", lambda: A.problem(
        "GET", "/api/settings/{category}", 404, "settings-category-not-found", category="nope"))
    got = A("GET", "/api/settings/{category}", category="chunking")
    etag = got.headers.get("ETag")
    r("Settings", "GET returns an ETag", lambda: t.expect(bool(etag), "no ETag header"))
    if etag:
        r("Settings", "PUT with the matching If-Match", lambda: A(
            "PUT", "/api/settings/{category}", 200, body=got.json, headers={"If-Match": etag}, category="chunking"))
        r("Errors", "PUT with a stale If-Match -> 412 settings-changed", lambda: A.problem(
            "PUT", "/api/settings/{category}", 412, "settings-changed", body=got.json, headers={"If-Match": etag}, category="chunking"))
    r("Errors", "PUT an unknown property -> 400 invalid-settings", lambda: A.problem(
        "PUT", "/api/settings/{category}", 400, "invalid-settings", body={**got.json, "bogus": 1}, category="chunking"))
    r("Errors", "PUT provider-owned settings -> 409 provider-owned-settings", lambda: A.problem(
        "PUT", "/api/settings/{category}", 409, "provider-owned-settings", body={}, category="samlsignin"))
    r("Settings", "reset a category", lambda: A("DELETE", "/api/v1/settings/{category}", 204, category="chunking"))
    r("Settings", "test the embedding connection", lambda: t.expect(A(
        "POST", "/api/settings/test-connection", 200, body={"category": "embedding",
                                                              "settings": A("GET", "/api/settings/{category}", category="embedding").json}
    ).json.get("success") is True, "embedding test did not succeed"))
    r("Settings", "embedding models", lambda: A("GET", "/api/settings/embedding-models"))
    r("Settings", "reindex (all)", lambda: A("POST", "/api/settings/reindex", 200, body={"force": False}))
    r("Settings", "reindex status", lambda: A("GET", "/api/settings/reindex/status"))
    r("Settings", "clear summaries", lambda: A("POST", "/api/v1/settings/summaries/clear", 200))


def providers(c, t):
    """Providers (cloud setup checked for status and problem type only)"""
    A, r = c.admin, t.record
    r("Providers", "status", lambda: A("GET", "/api/v1/providers"))
    r("Providers", "AWS access read", lambda: A("GET", "/api/v1/providers/aws/access"))
    r("Providers", "GitHub App read", lambda: A("GET", "/api/v1/providers/github/app"))
    r("Providers", "custom providers list", lambda: A("GET", "/api/v1/providers/custom"))
    r("Errors", "AWS access with bad values -> 400 invalid-aws-setup", lambda: A.problem(
        "PUT", "/api/v1/providers/aws/access", 400, "invalid-aws-setup",
        body={"certificatePem": "x", "trustAnchorArn": "x", "profileArn": "x", "roleArn": "x", "region": "x"}))
    r("Providers", "AWS access reset (nothing stored)", lambda: A("DELETE", "/api/v1/providers/aws/access", (200,)))
    r("Errors", "Identity Center with bad values -> 400 invalid-aws-setup", lambda: A.problem(
        "PUT", "/api/v1/providers/aws/identity-center", 400, "invalid-aws-setup",
        body={"region": "x", "instanceArn": "x", "identityStoreId": "x"}))
    r("Providers", "Identity Center reset", lambda: A("DELETE", "/api/v1/providers/aws/identity-center", 200))
    r("Errors", "AWS permissions with bad values -> 400 invalid-aws-setup", lambda: A.problem(
        "PUT", "/api/v1/providers/aws/permissions", 400, "invalid-aws-setup", body={"idpSingleSignOnUrl": "not a url"}))
    r("Providers", "AWS permissions reset", lambda: A("DELETE", "/api/v1/providers/aws/permissions", 200))
    r("Errors", "Azure access without a tenant -> 400 invalid-azure-setup", lambda: A.problem(
        "PUT", "/api/v1/providers/azure/access", 400, "invalid-azure-setup", body={"tenantId": ""}))
    r("Errors", "Azure access this host can't reach -> 400 azure-credential-refused", lambda: A.problem(
        "PUT", "/api/v1/providers/azure/access", 400, "azure-credential-refused", body={"tenantId": "not-a-guid"}))
    r("Providers", "Azure access reset", lambda: A("DELETE", "/api/v1/providers/azure/access", 200))
    r("Errors", "Azure permissions with a bad redirect address -> 400 invalid-azure-setup", lambda: A.problem(
        "PUT", "/api/v1/providers/azure/permissions", 400, "invalid-azure-setup", body={"redirectUri": "not a url"}))
    r("Providers", "Azure permissions reset", lambda: A("DELETE", "/api/v1/providers/azure/permissions", 200))
    r("Errors", "GitHub App with bad values -> 400 invalid-github-app", lambda: A.problem(
        "PUT", "/api/v1/providers/github/app", 400, "invalid-github-app", body={"appId": 0}))
    r("Providers", "GitHub App remove (nothing stored)", lambda: A("DELETE", "/api/v1/providers/github/app", 200))
    r("Providers", "GitHub manifest", lambda: A("POST", "/api/v1/providers/github/manifest", 200, body={}))
    r("Providers", "GitHub manifest callback without a code redirects", lambda: A(
        "GET", "/api/v1/providers/github/manifest/callback", 302, query={"state": "x"}))
    r("Providers", "GitHub installed redirects", lambda: A(
        "GET", "/api/v1/providers/github/installed", 302, query={"installation_id": "1", "setup_action": "install"}))

    # Custom provider: the sample server answers the provider protocol.
    base = "http://custom-perms:8080"
    prov = r("Providers", "create a custom provider", lambda: A(
        "POST", "/api/v1/providers/custom", (201,), body={"baseUrl": base, "token": "change-me-example-token", "name": f"e2e-{c.run}"}).json)
    r("Errors", "custom provider with a bad URL -> 400 invalid-custom-provider", lambda: A.problem(
        "POST", "/api/v1/providers/custom", 400, "invalid-custom-provider", body={"baseUrl": "not a url", "token": "x"}))
    r("Errors", "missing custom provider -> 404 custom-provider-not-found", lambda: A.problem(
        "PATCH", "/api/v1/providers/custom/{providerId}", 404, "custom-provider-not-found", body={"name": "x"}, providerId=MISSING))
    if prov:
        pid = prov.get("id") or prov.get("providerId")
        r("Providers", "check a custom provider", lambda: A("POST", "/api/v1/providers/custom/{providerId}/check", 200, body={}, providerId=pid))
        r("Providers", "rename a custom provider", lambda: A(
            "PATCH", "/api/v1/providers/custom/{providerId}", 200, body={"name": f"e2e-{c.run}-b"}, providerId=pid))
        r("Providers", "link secret", lambda: A("PUT", "/api/v1/providers/custom/{providerId}/link-secret", 200, providerId=pid))
        r("Links", "custom link connect", lambda: A("GET", "/api/v1/auth/custom/{providerId}/connect", (200, 302), providerId=pid))
        r("Links", "custom link return without a code", lambda: A(
            "GET", "/api/v1/auth/custom/{providerId}/return", 302, providerId=pid))
        r("Links", "unlink a custom provider that isn't linked -> 204 (idempotent)", lambda: A(
            "DELETE", "/api/v1/auth/custom/{providerId}", 204, providerId=pid))
        r("Errors", "unlink an unknown custom provider -> 404 link-not-found", lambda: A.problem(
            "DELETE", "/api/v1/auth/custom/{providerId}", 404, "link-not-found", providerId=MISSING))
        r("Providers", "delete a custom provider", lambda: A("DELETE", "/api/v1/providers/custom/{providerId}", 204, providerId=pid))

    # Atlassian (cloud): reads and validation only.
    r("Atlassian", "sites list", lambda: A("GET", "/api/v1/atlassian/sites"))
    r("Atlassian", "resolve a bad site URL -> 400", lambda: A("POST", "/api/v1/atlassian/sites/resolve", 400, body={"siteUrl": "not a url"}))
    r("Atlassian", "create a site with a bad URL -> 422", lambda: A(
        "POST", "/api/v1/atlassian/sites", 422, body={"siteUrl": "not a url", "clientId": "x", "clientSecret": "x"}))
    for m, tail, st, b in (("GET", "", 404, None), ("PATCH", "", 404, {"name": "x"}), ("POST", "/pause", 404, None),
                           ("POST", "/resume", 404, {}), ("POST", "/test", 404, None),
                           ("PUT", "/credentials", 404, {"clientId": "x", "clientSecret": "x"}),
                           ("GET", "/spaces", 404, None), ("GET", "/projects", 404, None)):
        r("Atlassian", f"{m} missing site{tail} -> 404", lambda m=m, tail=tail, st=st, b=b: A(
            m, "/api/v1/atlassian/sites/{connectionId}" + tail, st, body=b, connectionId=MISSING))
    r("Atlassian", "delete a missing site -> 404", lambda: A(
        "DELETE", "/api/v1/atlassian/sites/{connectionId}", 404, query={"confirmName": "x"}, connectionId=MISSING))
    r("Atlassian", "linking app read (none) -> 404", lambda: A("GET", "/api/v1/atlassian/linking-app", 404))
    r("Atlassian", "linking app save without values -> 400", lambda: A(
        "PUT", "/api/v1/atlassian/linking-app", 400, body={"clientId": "", "clientSecret": ""}))
    r("Atlassian", "linking app check (none) -> 404", lambda: A("POST", "/api/v1/atlassian/linking-app/check", 404))
    r("Atlassian", "linking app sign-in (none) -> 404", lambda: A(
        "PUT", "/api/v1/atlassian/linking-app/sign-in", 404, body={"signInEnabled": True}))
    r("Atlassian", "linking app delete (none) -> 404", lambda: A(
        "DELETE", "/api/v1/atlassian/linking-app", 404, query={"confirmClientId": "x"}))

    # Cloud sign-in links: each redirects, or refuses when the provider isn't set up.
    for prov_name in ("aws", "azure", "github", "atlassian"):
        for step in ("connect", "confirm"):
            r("Links", f"{prov_name} {step}", lambda p=prov_name, s=step: A(
                "GET", f"/api/v1/auth/cloud/{p}/{s}", (302, 401, 409)))
    for prov_name in ("azure", "github", "atlassian"):
        r("Links", f"{prov_name} callback without a code", lambda p=prov_name: A(
            "GET", f"/api/v1/auth/cloud/{p}/callback", 302, query={"state": "x"}))
    r("Links", "aws acs without a SAML response", lambda: A("POST", "/api/v1/auth/cloud/aws/acs", (302, 400), body=b""))
    r("Links", "list account links", lambda: A("GET", "/api/v1/auth/links"))
    r("Errors", "unlink an unknown provider -> 400 invalid-link-provider", lambda: A.problem(
        "DELETE", "/api/v1/auth/cloud/{provider}", 400, "invalid-link-provider", provider="nope"))
    r("Errors", "unlink a provider not linked -> 404 link-not-found", lambda: A.problem(
        "DELETE", "/api/v1/auth/cloud/{provider}", 404, "link-not-found", provider="github"))


# ── Connections and sources ──────────────────────────────────────────────────────────────────────

def connectors(c, t):
    """Connections and sources: Filesystem, SFTP and Custom, synced and searched"""
    A, r = c.admin, t.record
    for p in ("Filesystem", "Sftp", "Custom", "S3", "AzureBlob", "GitHub", "Atlassian"):
        r("Sources", f"scope schema {p}", lambda p=p: A("GET", "/api/v1/sources/scope-schema/{provider}", provider=p))
    r("Errors", "scope schema for an unknown provider -> 404", lambda: A(
        "GET", "/api/v1/sources/scope-schema/{provider}", 404, provider="Nope"))
    r("Errors", "connection with no provider config -> 400 invalid-connection", lambda: A.problem(
        "POST", "/api/v1/connections", 400, "invalid-connection", body={"name": f"bad-{c.run}", "provider": "Filesystem", "config": {}}))
    r("Errors", "missing connection -> 404 connection-not-found", lambda: A.problem(
        "GET", "/api/v1/connections/{connectionId}", 404, "connection-not-found", connectionId=MISSING))
    r("Errors", "missing source -> 404 source-not-found", lambda: A.problem(
        "GET", "/api/sources/{sourceId}", 404, "source-not-found", sourceId=MISSING))

    key = (c.work / "sftp" / "id_ed25519").read_text()
    plans = [
        ("Filesystem", {"allowedRoot": "/data/fs"}, {}, {"type": "filesystem", "subPath": "guides"}, "falconctl"),
        ("Sftp", {"host": "sftp", "port": 22, "username": "e2e", "allowedRoot": "/upload"}, {"privateKey": key},
         {"type": "sftp", "subPath": "docs"}, "heron"),
        ("Custom", {"baseUrl": "http://custom-source:8080"}, {"token": "change-me-example-token"},
         {"type": "custom", "baseUrl": "http://custom-source:8080", "scopeId": "eng"}, None),
    ]
    for provider, config, secret, scope, word in plans:
        connector(c, t, provider, config, secret, scope, word)
    r("Sources", "list sources (paged)", lambda: A("GET", "/api/sources", query={"skip": 0, "take": 50}))
    r("Sources", "list connections filtered by provider", lambda: A("GET", "/api/v1/connections", query={"provider": "Sftp"}))


def connector(c, t, provider, config, secret, scope, word):
    A, r = c.admin, t.record
    area = f"Connector {provider}"
    name = f"e2e-{provider.lower()}-{c.run}"
    created = r(area, "create a connection", lambda: A(
        "POST", "/api/v1/connections", 201, body={"name": name, "provider": provider, "config": config, **secret}).json)
    if not created:
        return
    conn = created["connection"]
    cid = conn["id"]
    r(area, "get the connection", lambda: A("GET", "/api/v1/connections/{connectionId}", connectionId=cid))
    if provider == "Filesystem":
        r(area, "test the connection -> 400 invalid-connection (nothing to test)", lambda: A.problem(
            "POST", "/api/v1/connections/{connectionId}/test", 400, "invalid-connection", body={}, connectionId=cid))
    else:
        r(area, "test the connection", lambda: t.expect(A(
            "POST", "/api/v1/connections/{connectionId}/test", 200, body={}, connectionId=cid).json["success"], "test did not succeed"))
    r(area, "discover", lambda: A("GET", "/api/v1/connections/{connectionId}/discover", (200, 400), connectionId=cid))
    r(area, "rename the connection", lambda: A(
        "PATCH", "/api/v1/connections/{connectionId}", 200, body={"name": name + "-r", "config": config}, connectionId=cid))
    r("Errors", f"{provider}: connection with no sources has nothing to pause -> 409", lambda: A(
        "POST", "/api/v1/connections/{connectionId}/pause", (200, 409), connectionId=cid))
    if provider == "Sftp":
        r(area, "replace the secret (same key)", lambda: A(
            "PUT", "/api/v1/connections/{connectionId}/secret", 200, body=secret, connectionId=cid))
    src = r(area, "create a source", lambda: A(
        "POST", "/api/sources", 201, body={"name": f"{name}-src", "connectionId": cid, "scope": scope}).json)
    if not src:
        return
    sid = src["id"]
    r(area, "sync the source", lambda: A("POST", "/api/sources/{sourceId}/sync", (200, 409), sourceId=sid))
    synced = r(area, "source syncs and indexes", lambda: wait_source(c, t, sid))
    r(area, "source stats", lambda: A("GET", "/api/sources/{sourceId}/stats", sourceId=sid))
    if synced:
        if word:
            r(area, "search the source finds its text", lambda: t.expect(hits(A(
                "POST", "/api/sources/{sourceId}/search", 200, body={"query": word, "mode": "Hybrid"}, sourceId=sid).json, word),
                f"no hit for {word}"))
        else:
            r(area, "search the source", lambda: t.expect(A(
                "POST", "/api/sources/{sourceId}/search", 200, body={"query": "deploy", "mode": "Hybrid"}, sourceId=sid).json["hits"],
                "no hits"))
    r(area, "update the source", lambda: A("PATCH", "/api/sources/{sourceId}", 200, body={"description": "e2e"}, sourceId=sid))
    r(area, "retry failed", lambda: A("POST", "/api/sources/{sourceId}/retry-failed", 200, sourceId=sid))
    r(area, "pause the connection", lambda: A("POST", "/api/v1/connections/{connectionId}/pause", 200, connectionId=cid))
    r(area, "resume the connection", lambda: A("POST", "/api/v1/connections/{connectionId}/resume", 200, body={}, connectionId=cid))
    if provider == "Sftp":
        r(area, "forget the host key", lambda: A("DELETE", "/api/v1/connections/{connectionId}/host-key", 200, connectionId=cid))
    r("Errors", f"{provider}: delete a connection in use -> 409 connection-in-use", lambda: A.problem(
        "DELETE", "/api/v1/connections/{connectionId}", 409, "connection-in-use", query={"confirmName": name + "-r"}, connectionId=cid))
    r("Errors", f"{provider}: delete a source with the wrong name -> 400", lambda: A(
        "DELETE", "/api/sources/{sourceId}", 400, query={"confirmName": "wrong"}, sourceId=sid))
    c.sources = getattr(c, "sources", []) + [(sid, f"{name}-src", cid, name + "-r")]


def audit(c, t):
    """Audit log and OAuth clients"""
    A, r = c.admin, t.record
    r("Audit", "audit log", lambda: t.expect(A("GET", "/api/v1/audit", query={"take": 20}).json is not None, "no body"))
    r("Errors", "audit filter since after until -> 400 invalid-audit-filter", lambda: A.problem(
        "GET", "/api/v1/audit", 400, "invalid-audit-filter",
        query={"since": "2026-02-01T00:00:00Z", "until": "2026-01-01T00:00:00Z"}))
    r("Errors", "audit filter that isn't a date -> plain 400 (binding)", lambda: A.problem(
        "GET", "/api/v1/audit", 400, None, query={"since": "not-a-date"}))
    reg = r("OAuth", "dynamic client registration", lambda: A.as_({})(
        "POST", "/oauth/register", 201,
        body={"client_name": f"e2e-{c.run}", "application_type": "native", "redirect_uris": ["http://127.0.0.1:7777/callback"],
              "grant_types": ["authorization_code"], "response_types": ["code"], "token_endpoint_auth_method": "none"}).json)
    r("OAuth", "token endpoint refuses a bad grant -> 400", lambda: A.as_({})(
        "POST", "/oauth/token", 400, body=b"grant_type=authorization_code&code=bad",
        headers={"Content-Type": "application/x-www-form-urlencoded"}))
    r("OAuth", "list OAuth clients", lambda: A("GET", "/api/v1/oauth-clients"))
    if reg:
        r("OAuth", "revoke an OAuth client", lambda: A("DELETE", "/api/v1/oauth-clients/{clientId}", 200, clientId=reg["client_id"]))
    r("Errors", "revoke a missing client -> 404 oauth-client-not-found", lambda: A.problem(
        "DELETE", "/api/v1/oauth-clients/{clientId}", 404, "oauth-client-not-found", clientId="nope"))


# ── MCP ──────────────────────────────────────────────────────────────────────────────────────────

EXPECTED_TOOLS = {"container_create", "container_list", "container_delete", "container_describe", "container_stats",
                  "upload_file", "bulk_upload", "list_files", "get_document", "delete_file", "bulk_delete",
                  "search_knowledge", "connapse_api_search", "connapse_api_read", "connapse_api_write"}


def mcp(c, t):
    """MCP tools over Streamable HTTP"""
    r = t.record
    if not getattr(c, "agent_key", None):
        r("MCP", "agent key available", lambda: t.expect(False, "no agent key was created"))
        return
    m = t.Mcp(c.base, {"X-Api-Key": c.agent_key})
    if not r("MCP", "initialize", m.initialize):
        return
    tools = r("MCP", "tools/list", m.tools)
    if tools is not None:
        r("MCP", "tools/list names the documented tools", lambda: t.expect(
            set(tools) >= EXPECTED_TOOLS - {"connapse_api_read", "connapse_api_write", "connapse_api_search"},
            f"missing {sorted(EXPECTED_TOOLS - set(tools))}"))
    box = f"e2e-mcp-{c.run}"
    r("MCP", "container_create", lambda: m.call("container_create", {"name": box, "description": "mcp"}))
    r("MCP", "container_list", lambda: t.expect(box in m.call("container_list", {}), "container not listed"))
    r("MCP", "container_describe", lambda: m.call("container_describe", {"containerId": box}))
    r("MCP", "upload_file", lambda: m.call("upload_file", {"containerId": box, "fileName": "kestrel.md",
                                                           "textContent": "# Kestrel\n\nThe kestrel batch job reruns at midnight UTC.\n",
                                                           "path": "/notes/"}))
    r("MCP", "bulk_upload", lambda: m.call("bulk_upload", {"containerId": box, "files": json.dumps([
        {"filename": "a.txt", "content": "alpha note about lanterns", "encoding": "text"},
        {"filename": "b.txt", "content": "YnJhdm8gbm90ZQ==", "encoding": "base64", "folderPath": "/b/"}])}))
    listing = r("MCP", "list_files", lambda: m.call("list_files", {"containerId": box, "path": "/notes/"}))
    r("MCP", "list_files shows the upload", lambda: t.expect(listing and "kestrel.md" in listing, f"kestrel.md missing: {listing}"))
    A = c.admin
    bx = A("GET", "/api/containers/by-name/{name}", name=box).json
    files = A("GET", "/api/containers/{containerId}/files", containerId=bx["id"], query={"path": "/notes/"}).json["items"]
    kid = next((f["id"] for f in files if f["name"] == "kestrel.md"), None)
    if kid:
        r("MCP", "uploaded file indexes", lambda: wait_ready(c, t, bx["id"], kid))
        r("MCP", "search_knowledge finds it", lambda: t.expect(
            "midnight" in m.call("search_knowledge", {"containerId": box, "query": "when does the kestrel job rerun"}), "no hit"))
        r("MCP", "get_document", lambda: t.expect("Kestrel" in m.call("get_document", {"containerId": box, "fileId": kid}), "no text"))
    r("MCP", "container_stats", lambda: m.call("container_stats", {"containerId": box}))
    all_files = A("GET", "/api/containers/{containerId}/files", containerId=bx["id"], query={"path": "/"}).json["items"]
    a_id = next((f["id"] for f in all_files if f["name"] == "a.txt"), None)
    if a_id:
        held = t.Mcp(c.base, {"X-Api-Key": c.held_key}) if getattr(c, "held_key", None) else None
        if held:
            held.initialize()
            r("MCP", "a key that needs approval gets 'pending approval' for delete_file", lambda: t.expect(
                "pending approval" in held.call("delete_file", {"containerId": box, "fileId": a_id}), "not held"))
        r("MCP", "delete_file", lambda: m.call("delete_file", {"containerId": box, "fileId": a_id}))
    b_files = A("GET", "/api/containers/{containerId}/files", containerId=bx["id"], query={"path": "/b/"}).json["items"]
    r("MCP", "bulk_delete", lambda: m.call("bulk_delete", {"containerId": box, "fileIds": json.dumps([f["id"] for f in b_files if not f["isFolder"]])}))
    if kid:
        m.call("delete_file", {"containerId": box, "fileId": kid})
    r("MCP", "container_delete", lambda: m.call("container_delete", {"containerId": box}))

    # Generic API tools, through an admin PAT (agent keys can't hold admin scopes).
    if getattr(c, "pat_token", None):
        g = t.Mcp(c.base, {"X-Api-Key": c.pat_token})
        g.initialize()
        r("MCP", "connapse_api_search", lambda: t.expect("agents.list" in g.call("connapse_api_search", {"query": "agents"}), "no agents.list"))
        r("MCP", "connapse_api_read", lambda: t.expect("200" in g.call("connapse_api_read", {"operation": "agents.list"}), "no 200"))
        r("MCP", "connapse_api_write", lambda: t.expect("201" in g.call("connapse_api_write", {
            "operation": "invites.create", "body": {"email": f"mcp-{c.run}@example.com", "role": "Viewer"}}), "no 201"))

    # Scope and transport rules.
    if getattr(c, "agent_ro_key", None):
        ro = t.Mcp(c.base, {"X-Api-Key": c.agent_ro_key})
        ro.initialize()
        names = ro.tools()
        r("MCP", "a read-only key doesn't see write tools", lambda: t.expect("upload_file" not in names, f"listed: {names}"))
        r("MCP", "a read-only key calling a write tool is refused", lambda: t.expect(
            ro.refused("upload_file", {"containerId": c.container_name, "fileName": "x.txt", "textContent": "x"}),
            "not refused"))
    r("MCP", "a foreign Origin is refused with 403", lambda: t.expect(
        t.http("POST", c.base + "/mcp", json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {}}).encode(),
               {"X-Api-Key": c.agent_key, "Origin": "https://evil.example", "Content-Type": "application/json",
                "Accept": "application/json, text/event-stream"}).status == 403, "foreign Origin was accepted"))
    r("MCP", "no credentials -> 401", lambda: t.expect(
        t.http("POST", c.base + "/mcp", b"{}", {"Content-Type": "application/json",
                                                 "Accept": "application/json, text/event-stream"}).status == 401, "not 401"))


# ── CLI ──────────────────────────────────────────────────────────────────────────────────────────

def cli(c, t):
    """connapse CLI against the stack"""
    r = t.record
    if not c.cli_dir or not getattr(c, "pat_token", None):
        return
    dll = next(Path(c.cli_dir).glob("connapse.dll"), None) or next(Path(c.cli_dir).glob("Connapse.Cli.dll"))
    home = c.work / "clihome"
    env = {**os.environ, "USERPROFILE": str(home), "HOME": str(home), "MSYS_NO_PATHCONV": "1", "NO_COLOR": "1",
           "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}

    def run(*args, stdin=None, code=0, want=None):
        p = subprocess.run(["dotnet", str(dll), *args], input=stdin, capture_output=True, text=True, env=env, timeout=300)
        out = p.stdout + p.stderr
        t.expect(p.returncode == code, f"connapse {' '.join(args)} exited {p.returncode} (wanted {code}): {out[:600]}")
        if want:
            t.expect(want.lower() in out.lower(), f"connapse {' '.join(args)}: '{want}' not in output: {out[:600]}")
        return out

    box = f"e2e-cli-{c.run}"
    r("CLI", "version", lambda: run("version"))
    # The CLI sends a pasted token as Bearer, and the server reads a PAT only from X-Api-Key, so a pasted
    # PAT is refused (CON-167). The access token from signing in carries the rest of the journey.
    r("CLI", "auth login --with-token (a PAT) [known: CON-167]", lambda: run("auth", "login", "--server", c.base, "--with-token", stdin=c.pat_token + "\n"))
    if not r("CLI", "auth login --with-token (an access token)",
             lambda: run("auth", "login", "--server", c.base, "--with-token", stdin=c.access_token + "\n")):
        return
    r("CLI", "auth whoami", lambda: run("auth", "whoami", want=c.admin_email))
    r("CLI", "auth pat list", lambda: run("auth", "pat", "list"))
    r("CLI", "container create", lambda: run("container", "create", box, "--description", "cli"))
    r("CLI", "container list", lambda: run("container", "list", want=box))
    upload_dir = c.work / "cli-upload"
    upload_dir.mkdir(exist_ok=True)
    (upload_dir / "plover.md").write_text("# Plover\n\nThe plover cache warms every six hours.\n", encoding="utf-8")
    r("CLI", "upload", lambda: run("upload", str(upload_dir / "plover.md"), "--container", box, "--destination", "/cli/"))
    r("CLI", "files list", lambda: run("files", "list", "--container", box, "--path", "/cli/", want="plover.md"))
    bx = c.admin("GET", "/api/containers/by-name/{name}", name=box).json
    items = c.admin("GET", "/api/containers/{containerId}/files", containerId=bx["id"], query={"path": "/cli/"}).json["items"]
    fid = next((f["id"] for f in items if f["name"] == "plover.md"), None)
    if fid:
        r("CLI", "uploaded file indexes", lambda: wait_ready(c, t, bx["id"], fid))
        r("CLI", "files get", lambda: run("files", "get", "--container", box, "--file", fid))
        r("CLI", "search", lambda: run("search", "how often does the plover cache warm", "--container", box, want="six hours"))
        r("CLI", "reindex", lambda: run("reindex", "--container", box))
        r("CLI", "container stats", lambda: run("container", "stats", box))
        r("CLI", "files delete", lambda: run("files", "delete", "--container", box, "--file", fid))
    r("CLI", "container delete", lambda: run("container", "delete", box))
    r("CLI", "settings get", lambda: run("settings", "get", "chunking"))
    r("CLI", "settings set", lambda: run("settings", "set", "chunking", "maxChunkSize=900"))
    r("CLI", "settings reset", lambda: run("settings", "reset", "chunking"))
    r("CLI", "settings test", lambda: run("settings", "test", "embedding"))
    r("CLI", "settings embedding-models", lambda: run("settings", "embedding-models"))
    r("CLI", "settings reindex-status", lambda: run("settings", "reindex-status"))
    r("CLI", "agent list", lambda: run("agent", "list"))
    ag = f"e2e-cli-agent-{c.run}"
    r("CLI", "agent create", lambda: run("agent", "create", ag, "--description", "cli"))
    r("CLI", "agent get", lambda: run("agent", "get", ag))
    r("CLI", "agent disable", lambda: run("agent", "disable", ag))
    r("CLI", "agent enable", lambda: run("agent", "enable", ag))
    r("CLI", "agent key create", lambda: run("agent", "key", "create", ag, "--name", "e2e-key", "--scope", "knowledge:read",
                                             "--requires-approval", "false"))
    r("CLI", "agent key list", lambda: run("agent", "key", "list", ag))
    r("CLI", "agent delete --yes", lambda: run("agent", "delete", ag, "--yes"))
    r("CLI", "connection list", lambda: run("connection", "list"))
    r("CLI", "source list", lambda: run("source", "list"))
    r("CLI", "source scope-schema", lambda: run("source", "scope-schema", "Filesystem"))
    cn = f"e2e-cli-fs-{c.run}"
    r("CLI", "connection create", lambda: run("connection", "create", cn, "--provider", "Filesystem",
                                              "--config", json.dumps({"allowedRoot": "/data/fs"})))
    r("CLI", "connection get", lambda: run("connection", "get", cn))
    # A Filesystem connection has nothing to test, so the CLI should show the 400 problem and exit 1.
    r("CLI", "connection test shows the problem (exit 1)", lambda: run("connection", "test", cn, code=1, want="nothing to test"))
    r("CLI", "connection update", lambda: run("connection", "update", cn, "--name", cn))
    sn = f"{cn}-src"
    r("CLI", "source create", lambda: run("source", "create", sn, "--connection", cn, "--scope", json.dumps({"type": "filesystem"})))
    r("CLI", "source get", lambda: run("source", "get", sn))
    r("CLI", "source sync", lambda: run("source", "sync", sn))
    r("CLI", "source stats", lambda: run("source", "stats", sn))
    r("CLI", "source update", lambda: run("source", "update", sn, "--description", "cli"))
    r("CLI", "connection pause", lambda: run("connection", "pause", cn))
    r("CLI", "connection resume", lambda: run("connection", "resume", cn))
    r("CLI", "connection delete while in use is refused (exit 1)", lambda: run("connection", "delete", cn, code=1))
    r("CLI", "source delete", lambda: run("source", "delete", sn))
    r("CLI", "connection delete", lambda: run("connection", "delete", cn))
    r("CLI", "api GET with --paginate", lambda: run("api", "GET", "/api/containers", "--paginate"))
    r("CLI", "api POST problem -> exit 1", lambda: run("api", "POST", "/api/containers", "--data", '{"name":"Bad Name!"}', code=1))
    r("CLI", "a missing container -> exit 1", lambda: run("container", "stats", "e2e-no-such-container", code=1))
    r("CLI", "auth logout", lambda: run("auth", "logout"))
    # After logout the CLI calls a hard-coded https://localhost:5001 instead of saying it is signed out (exits 99).
    r("CLI", "signed out -> exit 2 [known: CON-168]", lambda: run("container", "list", code=2))


# ── Cleanup through the API (the stack is torn down anyway; this exercises the delete paths) ─────

def cleanup(c, t):
    """Deletes: sources, connections, containers"""
    A, r = c.admin, t.record
    for sid, sname, cid, cname in getattr(c, "sources", []):
        r("Sources", f"delete source {sname}", lambda sid=sid, sname=sname: A(
            "DELETE", "/api/sources/{sourceId}", 204, query={"confirmName": sname}, sourceId=sid))
        r("Sources", f"delete connection {cname}", lambda cid=cid, cname=cname: A(
            "DELETE", "/api/v1/connections/{connectionId}", 204, query={"confirmName": cname}, connectionId=cid))
    if getattr(c, "container_id", None):
        if getattr(c, "falcon_id", None):
            A("DELETE", "/api/containers/{containerId}/files/{fileId}", (204, 404), containerId=c.container_id, fileId=c.falcon_id)
        r("Containers", "delete an empty container", lambda: A(
            "DELETE", "/api/containers/{containerId}", (204,), containerId=c.container_id))


ALL = [tokens, users, identity, agents, containers, settings, providers, connectors, audit, mcp, cli, cleanup]
