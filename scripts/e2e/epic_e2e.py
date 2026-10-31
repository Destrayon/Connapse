#!/usr/bin/env python3
"""End-to-end test of a Connapse build: REST, MCP, the connapse CLI and the web UI against a real stack.

Builds the checkout's image, starts it under its own Compose project on empty volumes (every
container capped at 2 CPUs and 4 GB, no GPU), runs every journey, checks every response against
docs/api/openapi.json, prints a pass/fail table, and removes everything it started.

    python scripts/e2e/epic_e2e.py                  # build, start, test, tear down
    python scripts/e2e/epic_e2e.py --keep           # leave the stack running afterwards
    python scripts/e2e/epic_e2e.py --no-up          # test a stack already started with --keep
    python scripts/e2e/epic_e2e.py --down           # only tear down

Needs Docker, Python 3.10+, the .NET 10 SDK (for the CLI) and git. Standard library only, except the UI
journey (ui.py), which needs Playwright for Python and its headless Chromium shell; --no-ui skips it.
See scripts/e2e/README.md.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import secrets
import shutil
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request
import uuid
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
COMPOSE_FILES = ["docker-compose.yml", "scripts/e2e/docker-compose.e2e.yml"]
PROJECT = os.environ.get("E2E_PROJECT", "connapse-e2e")
EMBED_MODEL = "nomic-embed-text"
PROBLEM_DOC = "https://github.com/Destrayon/Connapse/blob/main/docs/problems.md#"
CLI_REPO = "https://github.com/Destrayon/connapse-cli.git"


# ── Results ──────────────────────────────────────────────────────────────────────────────────────

class Check(Exception):
    """A failed expectation; the message is the repro detail shown in the table."""


RESULTS: list[dict] = []


def record(area: str, item: str, fn, *args, **kwargs):
    """Run one item, record pass/fail, and return fn's result (None when it failed)."""
    start = time.monotonic()
    try:
        value = fn(*args, **kwargs)
        RESULTS.append({"area": area, "item": item, "ok": True, "detail": "", "secs": time.monotonic() - start})
        return value
    except Check as e:
        detail = str(e)
    except Exception as e:  # an unexpected crash in the harness or a malformed answer is still a failure
        detail = f"{type(e).__name__}: {e}\n{traceback.format_exc(limit=3)}"
    RESULTS.append({"area": area, "item": item, "ok": False, "detail": detail, "secs": time.monotonic() - start})
    print(f"  FAIL {area} / {item}: {detail.splitlines()[0][:200]}", flush=True)
    return None


def expect(cond: bool, msg: str):
    if not cond:
        raise Check(msg)


# ── The committed spec: response conformance and coverage ────────────────────────────────────────

class Spec:
    def __init__(self, path: Path):
        self.doc = json.loads(path.read_text(encoding="utf-8"))
        self.schemas = self.doc["components"]["schemas"]
        self.ops = {(m.upper(), p): o for p, v in self.doc["paths"].items()
                    for m, o in v.items() if m in ("get", "post", "put", "patch", "delete")}
        self.called: dict[tuple[str, str], set[int]] = {}

    def resolve(self, schema):
        while isinstance(schema, dict) and "$ref" in schema:
            schema = self.schemas[schema["$ref"].rsplit("/", 1)[1]]
        return schema

    def errors(self, schema, value, where="$", depth=0) -> list[str]:
        """A small JSON Schema check: $ref, allOf/oneOf/anyOf, type, required, properties, items, enum."""
        schema = self.resolve(schema)
        if not isinstance(schema, dict) or depth > 12:
            return []
        out: list[str] = []
        for sub in schema.get("allOf", []):
            out += self.errors(sub, value, where, depth + 1)
        for key in ("oneOf", "anyOf"):
            if key in schema and all(self.errors(s, value, where, depth + 1) for s in schema[key]):
                out.append(f"{where}: matches none of {key}")
        types = schema.get("type")
        if types is not None:
            types = types if isinstance(types, list) else [types]
            kinds = {"null": value is None, "boolean": isinstance(value, bool),
                     "integer": isinstance(value, int) and not isinstance(value, bool),
                     "number": isinstance(value, (int, float)) and not isinstance(value, bool),
                     "string": isinstance(value, str), "array": isinstance(value, list),
                     "object": isinstance(value, dict)}
            if not any(kinds.get(t, True) for t in types):
                return out + [f"{where}: expected {'/'.join(types)}, got {type(value).__name__}"]
        if "enum" in schema and value not in schema["enum"]:
            out.append(f"{where}: {value!r} not in enum")
        if isinstance(value, dict):
            for req in schema.get("required", []):
                if req not in value:
                    out.append(f"{where}: missing required '{req}'")
            for name, sub in schema.get("properties", {}).items():
                if name in value:
                    out += self.errors(sub, value[name], f"{where}.{name}", depth + 1)
        if isinstance(value, list) and "items" in schema:
            for i, item in enumerate(value[:5]):
                out += self.errors(schema["items"], item, f"{where}[{i}]", depth + 1)
        return out

    def conform(self, method: str, template: str, status: int, ctype: str, body):
        """Raise Check when the answer isn't one the spec declares for this route."""
        op = self.ops.get((method, template))
        expect(op is not None, f"{method} {template} is not in docs/api/openapi.json")
        self.called.setdefault((method, template), set()).add(status)
        responses = op.get("responses", {})
        declared = responses.get(str(status)) or responses.get("default")
        expect(declared is not None,
               f"{method} {template} answered {status}, which the spec doesn't declare (declares {sorted(responses)})")
        # OAuth endpoints answer RFC 6749 {"error": ...} JSON, and the spec declares that, not a problem.
        if status >= 400 and "application/problem+json" in (declared.get("content") or {"application/problem+json": 0}):
            expect(ctype.startswith("application/problem+json"),
                   f"{method} {template} answered {status} as '{ctype}', not application/problem+json: {str(body)[:300]}")
            expect(isinstance(body, dict) and body.get("type") and body.get("title") and body.get("status") == status,
                   f"{method} {template} {status} problem lacks type/title/status: {str(body)[:300]}")
        content = declared.get("content") or {}
        if content and body is not None and ctype.split(";")[0].strip().endswith("json"):
            media = next(iter(content.values()))
            errs = self.errors(media.get("schema"), body)
            expect(not errs, f"{method} {template} {status} body breaks the spec: {errs[:5]}; body {str(body)[:300]}")


# ── HTTP ─────────────────────────────────────────────────────────────────────────────────────────

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


OPENER = urllib.request.build_opener(NoRedirect)


class Response:
    def __init__(self, status: int, headers, raw: bytes):
        self.status, self.headers, self.raw = status, headers, raw
        self.ctype = headers.get("Content-Type", "") or ""
        try:
            self.json = json.loads(raw) if raw and "json" in self.ctype else None
        except ValueError:
            self.json = None

    @property
    def text(self) -> str:
        return self.raw.decode("utf-8", "replace")


def http(method: str, url: str, body=None, headers: dict | None = None, timeout=60) -> Response:
    data = None
    headers = dict(headers or {})
    if isinstance(body, (dict, list)):
        data = json.dumps(body).encode()
        headers.setdefault("Content-Type", "application/json")
    elif isinstance(body, bytes):
        data = body
    elif isinstance(body, str):
        data = body.encode()
        headers.setdefault("Content-Type", "application/json")
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with OPENER.open(req, timeout=timeout) as r:
            return Response(r.status, r.headers, r.read())
    except urllib.error.HTTPError as e:
        return Response(e.code, e.headers, e.read())


def multipart(fields: dict[str, str], files: list[tuple[str, str, bytes]]):
    boundary = "e2e" + secrets.token_hex(12)
    parts = []
    for name, value in fields.items():
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    for name, filename, content in files:
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                     f"Content-Type: application/octet-stream\r\n\r\n".encode() + content + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


class Api:
    """Calls one route by its spec template, checks the answer against the spec, and the status."""

    def __init__(self, base: str, spec: Spec, auth: dict[str, str] | None = None):
        self.base, self.spec, self.auth = base, spec, auth or {}

    def as_(self, auth: dict[str, str]) -> "Api":
        return Api(self.base, self.spec, auth)

    def __call__(self, method: str, template: str, expect_status: int | tuple = (200,), body=None,
                 query: dict | None = None, headers: dict | None = None, conform=True, **params) -> Response:
        path = template
        for k, v in params.items():
            path = path.replace("{" + k + "}", urllib.request.quote(str(v), safe=""))
        expect("{" not in path, f"unfilled path parameter in {template}")
        if query:
            path += "?" + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None})
        h = {**self.auth, **(headers or {})}
        r = http(method, self.base + path, body, h)
        statuses = expect_status if isinstance(expect_status, tuple) else (expect_status,)
        repro = f"{method} {path} -> {r.status} {r.text[:400]}"
        if conform:
            self.spec.conform(method, template, r.status, r.ctype, r.json)
        expect(r.status in statuses, f"expected {statuses}: {repro}")
        return r

    def problem(self, method: str, template: str, status: int, slug: str | None, body=None, **kw) -> Response:
        """Expect a problem+json of this status; with a slug, of that docs/problems.md type."""
        r = self(method, template, status, body=body, **kw)
        if slug:
            expect(r.json.get("type") == PROBLEM_DOC + slug,
                   f"{method} {template}: type {r.json.get('type')!r}, expected {PROBLEM_DOC + slug!r}")
        return r


import urllib.parse  # noqa: E402  (after the class bodies above refer to it lazily)


# ── MCP (Streamable HTTP) ────────────────────────────────────────────────────────────────────────

class Mcp:
    def __init__(self, base: str, auth: dict[str, str], origin: str | None = None):
        self.url, self.auth, self.origin = base + "/mcp", auth, origin
        self.session, self.version, self.next_id = None, None, 1

    def _post(self, payload) -> Response:
        h = {**self.auth, "Accept": "application/json, text/event-stream", "Content-Type": "application/json"}
        if self.session:
            h["Mcp-Session-Id"] = self.session
        if self.version:
            h["MCP-Protocol-Version"] = self.version
        if self.origin:
            h["Origin"] = self.origin
        return http("POST", self.url, json.dumps(payload).encode(), h, timeout=180)

    @staticmethod
    def _message(r: Response):
        if "text/event-stream" in r.ctype:
            datas = [line[5:].strip() for line in r.text.splitlines() if line.startswith("data:")]
            msgs = [json.loads(d) for d in datas if d]
            return next((m for m in reversed(msgs) if "result" in m or "error" in m), msgs[-1] if msgs else None)
        return json.loads(r.raw) if r.raw else None

    def request(self, method: str, params=None):
        msg_id, self.next_id = self.next_id, self.next_id + 1
        r = self._post({"jsonrpc": "2.0", "id": msg_id, "method": method, "params": params or {}})
        expect(r.status == 200, f"MCP {method} -> HTTP {r.status} {r.text[:300]}")
        if method == "initialize":
            self.session = r.headers.get("Mcp-Session-Id")
        msg = self._message(r)
        expect(msg is not None, f"MCP {method}: empty answer")
        expect("error" not in msg, f"MCP {method} -> error {msg.get('error')}")
        return msg["result"]

    def initialize(self):
        result = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                             "clientInfo": {"name": "connapse-e2e", "version": "1"}})
        self.version = result.get("protocolVersion")
        r = self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})
        expect(r.status in (200, 202), f"notifications/initialized -> {r.status}")
        return result

    def tools(self) -> list[str]:
        return sorted(t["name"] for t in self.request("tools/list")["tools"])

    def refused(self, name: str, args: dict) -> bool:
        """True when the server refuses the call, as a JSON-RPC error or a tool error."""
        try:
            return self.call(name, args, ok=False)[0]
        except Check as e:
            return "-> error" in str(e) or "-> HTTP 403" in str(e)

    def call(self, name: str, args: dict, ok=True) -> str:
        result = self.request("tools/call", {"name": name, "arguments": args})
        text = "\n".join(c.get("text", "") for c in result.get("content", []))
        if ok:
            expect(not result.get("isError"), f"tool {name}({json.dumps(args)[:200]}) returned an error: {text[:400]}")
        return text if ok else (bool(result.get("isError")), text)


# ── Stack lifecycle ──────────────────────────────────────────────────────────────────────────────

def compose(env: dict, *args, check=True, capture=False):
    cmd = ["docker", "compose", "-p", PROJECT, "--project-directory", str(REPO)]
    for f in COMPOSE_FILES:
        cmd += ["-f", str(REPO / f)]
    cmd += ["--profile", "with-ollama", *args]
    return subprocess.run(cmd, env=env, check=check, text=True, capture_output=capture)


def prepare_work(work: Path):
    """Fixtures the connectors read: a folder, an SFTP account with a fresh key, the custom source's data."""
    for sub in ("fs", "sftp", "custom", "custom-perms", "clihome"):
        shutil.rmtree(work / sub, ignore_errors=True)
    (work / "fs" / "guides").mkdir(parents=True)
    (work / "fs" / "guides" / "falcon.md").write_text(
        "# Falcon runbook\n\nThe falcon service restarts with the command `falconctl bounce`. "
        "Its on-call rotation is weekly.\n", encoding="utf-8")
    (work / "fs" / "notes.txt").write_text("Filesystem connector note about quartz tiles.\n", encoding="utf-8")
    (work / "sftp" / "upload" / "docs").mkdir(parents=True)
    (work / "sftp" / "upload" / "docs" / "heron.md").write_text(
        "# Heron\n\nThe heron pipeline ships nightly builds to the mirror.\n", encoding="utf-8")
    key = work / "sftp" / "id_ed25519"
    subprocess.run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "connapse-e2e", "-f", str(key)], check=True)
    shutil.copytree(REPO / "samples" / "Connapse.CustomSource.Example" / "sample-data", work / "custom")
    shutil.copytree(REPO / "samples" / "Connapse.CustomSource.Example" / "sample-data", work / "custom-perms")
    cfg = json.loads((work / "custom" / "example.json").read_text(encoding="utf-8"))
    cfg["permissions_enabled"] = False  # a content-only source: everyone who reads the source sees all of it
    (work / "custom" / "example.json").write_text(json.dumps(cfg, indent=2), encoding="utf-8")
    (work / "clihome").mkdir()


def wait_for(what: str, fn, timeout: float):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        try:
            value = fn()
            if value:
                return value
        except Exception as e:  # not up yet
            last = e
        time.sleep(2)
    raise Check(f"timed out after {timeout:.0f}s waiting for {what} (last: {last})")


def up(env: dict, base: str, build: bool):
    compose(env, "down", "-v", "--remove-orphans", check=False)
    if build:
        compose(env, "build", "web", "custom-source", "custom-perms")
    compose(env, "up", "-d", "--wait", "postgres", "minio", "ollama", "sftp", "custom-source", "custom-perms")
    print("Pulling the embedding model into the stack's Ollama (CPU only)...", flush=True)
    compose(env, "exec", "-T", "ollama", "ollama", "pull", EMBED_MODEL)
    compose(env, "up", "-d", "web")
    wait_for("/health", lambda: http("GET", base + "/health", timeout=5).status == 200, 300)


def down(env: dict):
    compose(env, "down", "-v", "--remove-orphans", "--rmi", "local", check=False)


# ── The journeys ─────────────────────────────────────────────────────────────────────────────────

class Ctx:
    """Everything the journeys create, so later ones can use it."""


def run_all(base: str, admin_email: str, admin_password: str, cli_dir: Path | None, work: Path, spec: Spec,
            ui_shots: Path | None = None):
    anon = Api(base, spec)
    c = Ctx()
    c.base, c.work, c.spec, c.admin_email, c.admin_password = base, work, spec, admin_email, admin_password
    c.run = uuid.uuid4().hex[:6]

    print("Journey: start and sign in", flush=True)
    record("Start", "GET /health", lambda: anon("GET", "/health"))
    record("Start", "GET /api/v1/info", lambda: anon("GET", "/api/v1/info"))
    record("Start", "OAuth metadata", lambda: (anon("GET", "/.well-known/oauth-authorization-server"),
                                               anon("GET", "/.well-known/oauth-protected-resource")))
    record("Start", "OAuth protected-resource for /mcp",
           lambda: anon("GET", "/.well-known/oauth-protected-resource/{resourcePath}", resourcePath="mcp"))
    record("Start", "CLI OAuth client metadata", lambda: anon("GET", "/oauth/clients/cli.json"))

    tokens = record("Auth", "POST /api/v1/auth/token (sign in)",
                    lambda: anon("POST", "/api/v1/auth/token", body={"email": admin_email, "password": admin_password}).json)
    if not tokens:
        return c
    c.admin = anon.as_({"Authorization": f"Bearer {tokens['accessToken']}"})
    c.access_token = tokens["accessToken"]
    record("Auth", "POST /api/v1/auth/token/refresh",
           lambda: anon("POST", "/api/v1/auth/token/refresh", body={"refreshToken": tokens["refreshToken"]}))
    record("Auth", "GET /api/v1/auth/me", lambda: expect(
        c.admin("GET", "/api/v1/auth/me").json.get("email", "").lower() == admin_email.lower(), "me is not the admin"))
    record("Errors", "wrong password -> 401 problem",
           lambda: anon("POST", "/api/v1/auth/token", 401, body={"email": admin_email, "password": "wrong-password-1A!"}))
    record("Errors", "no credentials -> 401 problem", lambda: anon("GET", "/api/containers", 401, query={"skip": 0, "take": 10}))

    import journeys  # noqa: E402  (journeys.py sits beside this file)
    c.cli_dir, c.ui_shots = cli_dir, ui_shots
    for journey in journeys.ALL:
        print(f"Journey: {journey.__doc__.strip().splitlines()[0]}", flush=True)
        try:
            journey(c, sys.modules[__name__])
        except Exception as e:
            RESULTS.append({"area": journey.__name__, "item": "journey crashed", "ok": False,
                            "detail": f"{type(e).__name__}: {e}\n{traceback.format_exc(limit=4)}", "secs": 0})
    return c


# ── Report ───────────────────────────────────────────────────────────────────────────────────────

def report(spec: Spec, out: Path, header: str) -> int:
    lines = [header, "", "| # | Area | Item | Result | Detail |", "|---|---|---|---|---|"]
    for i, r in enumerate(RESULTS, 1):
        detail = r["detail"].splitlines()[0][:300].replace("|", "\\|") if r["detail"] else ""
        lines.append(f"| {i} | {r['area']} | {r['item'].replace('|', '/')} | {'pass' if r['ok'] else '**FAIL**'} | {detail} |")
    uncovered = sorted(set(spec.ops) - set(spec.called))
    lines += ["", f"Routes in docs/api/openapi.json called: {len(spec.called)} of {len(spec.ops)}."]
    if uncovered:
        lines += ["", "Routes never called:", ""] + [f"- `{m} {p}`" for m, p in uncovered]
    failed = [r for r in RESULTS if not r["ok"]]
    lines += ["", f"**{len(RESULTS) - len(failed)} passed, {len(failed)} failed.**"]
    if failed:
        lines += ["", "## Failure details", ""]
        for r in failed:
            lines += [f"### {r['area']} / {r['item']}", "", "```", r["detail"].strip()[:3000], "```", ""]
    text = "\n".join(lines) + "\n"
    out.write_text(text, encoding="utf-8")
    print(text)
    return 1 if failed or uncovered else 0


def build_cli(work: Path, ref: str, local: str | None) -> Path:
    src = Path(local) if local else work / "connapse-cli"
    if not local:
        if not (src / ".git").exists():
            subprocess.run(["git", "clone", "-q", CLI_REPO, str(src)], check=True)
        subprocess.run(["git", "-C", str(src), "fetch", "-q", "origin"], check=True)
        subprocess.run(["git", "-C", str(src), "checkout", "-q", "--detach", ref], check=True)
    out = work / "cli-bin"
    subprocess.run(["dotnet", "build", str(src / "src" / "Connapse.Cli"), "-c", "Release", "-o", str(out),
                    "-nologo", "-v", "q", "-p:MinVerSkip=true"], check=True)
    return out


def lower_priority():
    """Run below normal priority, so the browser and dotnet children it starts don't crowd the desktop."""
    try:
        if os.name == "nt":
            import ctypes
            below_normal = 0x4000
            ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), below_normal)
        else:
            os.nice(10)
    except Exception:
        pass


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--work", default=os.environ.get("E2E_WORK") or str(REPO / "artifacts" / "e2e"),
                    help="scratch folder for fixtures, the CLI build and the report")
    ap.add_argument("--port", type=int, default=int(os.environ.get("E2E_PORT", "5163")))
    ap.add_argument("--keep", action="store_true", help="leave the stack running afterwards")
    ap.add_argument("--no-up", action="store_true", help="test a stack started earlier with --keep")
    ap.add_argument("--no-build", action="store_true", help="reuse the images built last time")
    ap.add_argument("--down", action="store_true", help="only tear the stack down")
    ap.add_argument("--cli-ref", default="origin/main", help="connapse-cli ref to build (default origin/main)")
    ap.add_argument("--cli-src", help="a local connapse-cli checkout to build instead of cloning")
    ap.add_argument("--no-cli", action="store_true", help="skip the CLI journeys")
    ap.add_argument("--no-ui", action="store_true", help="skip the web UI journey")
    ap.add_argument("--shots", help="folder for the UI journey's screenshots (default <work>/ui-shots)")
    args = ap.parse_args()

    work = Path(args.work).resolve()
    work.mkdir(parents=True, exist_ok=True)
    state = work / "stack.json"
    if args.no_up and state.exists():
        creds = json.loads(state.read_text())
    else:
        creds = {"email": "e2e-admin@example.com", "password": "E2e-" + secrets.token_urlsafe(12) + "9a!",
                 "jwt": secrets.token_urlsafe(48)}
    env = {**os.environ, "E2E_PORT": str(args.port), "E2E_WORK": work.as_posix(),
           "E2E_ADMIN_EMAIL": creds["email"], "E2E_ADMIN_PASSWORD": creds["password"], "E2E_JWT_SECRET": creds["jwt"]}
    if args.down:
        down(env)
        return 0

    base = f"http://127.0.0.1:{args.port}"
    lower_priority()
    shots = None if args.no_ui else Path(args.shots or work / "ui-shots").resolve()
    sys.path.insert(0, str(Path(__file__).parent))
    commit = subprocess.run(["git", "-C", str(REPO), "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    try:
        if not args.no_up:
            prepare_work(work)
            up(env, base, build=not args.no_build)
            state.write_text(json.dumps(creds))
        cli_dir = None if args.no_cli else build_cli(work, args.cli_ref, args.cli_src)
        spec = Spec(REPO / "docs" / "api" / "openapi.json")
        started = time.monotonic()
        run_all(base, creds["email"], creds["password"], cli_dir, work, spec, shots)
        header = (f"# Connapse end-to-end run\n\nServer: `{commit}` built from this checkout. "
                  f"CLI: {'skipped' if args.no_cli else (args.cli_src or 'connapse-cli ' + args.cli_ref)}. "
                  f"UI: {'skipped' if shots is None else f'headless Chromium, screenshots in `{shots}`'}. "
                  f"Took {time.monotonic() - started:.0f}s.")
        return report(spec, work / "report.md", header)
    finally:
        if not args.keep:
            down(env)
            state.unlink(missing_ok=True)


if __name__ == "__main__":
    sys.exit(main())
