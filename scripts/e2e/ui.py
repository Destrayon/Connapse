"""The web UI journey: the API and CLI journeys again, through the Blazor pages in a headless browser.

Laid out the way established Playwright suites are: one small page object per page holding its
locators and actions, assertions in the journey, one browser context per role (anonymous, the admin,
an invited Editor), unique names per run, and a screenshot of every page tested (and of every
failure) as proof. Sign-in is itself under test, so each context signs in through the form instead of
loading saved state.

Needs Playwright for Python and its headless Chromium shell (no Chrome, no full browser):

    pip install playwright
    playwright install --only-shell chromium

Blazor Server renders each page twice: once as static HTML, then again when its SignalR circuit
connects. A click on the static copy does nothing, so actions that depend on the circuit go through
`click_until`, which clicks again until the expected result appears.
"""
from __future__ import annotations

import re
import time
from pathlib import Path

TIMEOUT_MS = 20_000
INGEST_TIMEOUT = 600


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:60]


class Shots:
    """Numbers and saves one full-page screenshot per item, and one more when an item fails."""

    def __init__(self, folder: Path):
        self.folder, self.n = folder, 0
        folder.mkdir(parents=True, exist_ok=True)

    def take(self, page, area: str, item: str, failed=False) -> Path | None:
        self.n += 1
        path = self.folder / f"{self.n:02d}-{slug(area)}-{slug(item)}{'-FAIL' if failed else ''}.png"
        try:
            page.screenshot(path=str(path), full_page=True, timeout=10_000)
            return path
        except Exception:  # a crashed page or closed context: the row already says what failed
            return None


class Ui:
    """One browser shared by the journey; a context and page per role."""

    def __init__(self, c, t, browser, shots: Shots):
        self.c, self.t, self.browser, self.shots = c, t, browser, shots
        self.base = c.base.rstrip("/")

    def page(self):
        ctx = self.browser.new_context(base_url=self.base, viewport={"width": 1366, "height": 900})
        ctx.set_default_timeout(TIMEOUT_MS)
        return ctx.new_page()

    def item(self, page, area: str, item: str, fn):
        """Record one UI item, screenshot the page it ends on, and fail it if Blazor's error bar showed."""
        def run():
            value = fn()
            error_bar = page.locator("#blazor-error-ui")
            self.t.expect(not (error_bar.count() and error_bar.is_visible()),
                          f"Blazor's unhandled-error bar is showing on {page.url}")
            return value
        value = self.t.record(area, item, run)
        ok = self.t.RESULTS[-1]["ok"]
        shot = self.shots.take(page, area, item, failed=not ok)
        if not ok and shot:
            self.t.RESULTS[-1]["detail"] += f" (screenshot {shot.name})"
        return value


# ── Page objects ─────────────────────────────────────────────────────────────────────────────────

def goto(page, path: str):
    page.goto(path, wait_until="networkidle")


def click_until(page, target, done, tries=6, wait_ms=4000):
    """Click `target` until the locator `done` is visible: the first click can land before the circuit is up."""
    for attempt in range(tries):
        if done.count() and done.first.is_visible():
            return
        target.click()
        try:
            done.first.wait_for(state="visible", timeout=wait_ms)
            return
        except Exception:
            if attempt == tries - 1:
                raise


def expect_text(page, text: str, timeout=TIMEOUT_MS):
    page.get_by_text(text, exact=False).first.wait_for(state="visible", timeout=timeout)


def expect_gone(locator, timeout=TIMEOUT_MS):
    locator.first.wait_for(state="detached", timeout=timeout)


def expect_path(page, pattern: str, timeout=TIMEOUT_MS):
    page.wait_for_url(re.compile(pattern), timeout=timeout)


class Login:
    def __init__(self, page):
        self.page = page

    def sign_in(self, email: str, password: str):
        p = self.page
        if "/login" not in p.url:
            goto(p, "/login")
        p.locator("#email").fill(email)
        p.locator("#password").fill(password)
        p.get_by_role("button", name="Sign In").click()
        p.wait_for_load_state("networkidle")


def nav_link(page, href: str):
    return page.locator(f'nav a[href="{href}"], nav a[href="/{href}"]')


class Users:
    def __init__(self, page):
        self.page = page

    def open(self):
        goto(self.page, "/admin/users")

    def invite(self, email: str, role: str) -> str:
        p = self.page
        p.locator("#inviteEmail").fill(email)
        p.locator("#inviteRole").select_option(role)
        link = p.locator("#inviteLink")
        click_until(p, p.get_by_role("button", name="Send Invite"), link)
        return link.input_value()

    def invite_row(self, email: str):
        return self.page.locator("tr", has=self.page.get_by_text(email, exact=True)).filter(
            has=self.page.get_by_role("button", name="Revoke"))

    def user_row(self, email: str):
        return self.page.locator("tr", has=self.page.locator("td", has_text=re.compile(f"^{re.escape(email)}$")))


class Containers:
    def __init__(self, page):
        self.page = page

    def open(self):
        goto(self.page, "/")

    def card(self, name: str):
        return self.page.locator(".container-card", has=self.page.locator(".container-card-name", has_text=name))

    def create(self, name: str, description: str):
        p = self.page
        name_input = p.get_by_placeholder("my-project")
        click_until(p, p.get_by_role("button", name="New Container"), name_input)
        name_input.fill(name)
        p.get_by_placeholder("What is this container for?").fill(description)
        p.locator(".modal-footer-custom, .modal-content, .modal-dialog, body").get_by_role(
            "button", name="Create", exact=True).last.click()
        self.card(name).first.wait_for(state="visible")

    def delete(self, name: str):
        p = self.page
        confirm = p.get_by_role("button", name="Delete", exact=True)
        click_until(p, self.card(name).locator(".container-delete-btn"), confirm)
        confirm.last.click()
        expect_gone(self.card(name))


class Files:
    def __init__(self, page):
        self.page = page

    def row(self, name: str):
        return self.page.locator("tr.file-row", has=self.page.locator(".entry-name", has_text=name))

    def upload(self, path: Path):
        self.page.locator("#fileUploadInput").set_input_files(str(path))
        self.row(path.name).first.wait_for(state="visible", timeout=60_000)

    def wait_ready(self, name: str, timeout=INGEST_TIMEOUT):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            row = self.row(name)
            if row.locator(".badge", has_text="Ready").count():
                return
            if row.locator(".badge", has_text="Failed").count():
                raise AssertionError(f"{name} shows Failed in the file browser")
            time.sleep(5)
            self.page.reload(wait_until="networkidle")
        raise AssertionError(f"{name} never showed Ready within {timeout}s")

    def delete(self, name: str):
        p = self.page
        modal_delete = p.locator(".modal-custom, .modal-content, .modal-dialog").get_by_role("button", name="Delete", exact=True)
        click_until(p, self.row(name).locator('button[title="Delete"]'), modal_delete)
        modal_delete.last.click()
        expect_gone(self.row(name))


class Search:
    def __init__(self, page):
        self.page = page

    def run(self, scope_label: str, query: str, word: str, timeout=60):
        """Search until a result card contains `word` (a source may still be indexing)."""
        p = self.page
        goto(p, "/search")
        deadline = time.monotonic() + timeout
        while True:
            p.get_by_placeholder("Enter your search query...").fill(query)
            p.locator("select").first.select_option(label=scope_label)
            p.get_by_role("button", name="Search", exact=True).click()
            try:
                p.locator(".result-card", has_text=re.compile(re.escape(word), re.I)).first.wait_for(
                    state="visible", timeout=8000)
                return
            except Exception:
                if time.monotonic() > deadline:
                    raise AssertionError(f"no result containing {word!r} for {query!r} in {scope_label!r}")
                time.sleep(5)
                p.reload(wait_until="networkidle")


class Tokens:
    def __init__(self, page):
        self.page = page

    def create(self, name: str) -> str:
        p = self.page
        goto(p, "/settings/tokens")
        name_input = p.get_by_placeholder("e.g. my-script")
        click_until(p, p.get_by_role("button", name="New Token"), name_input)
        name_input.fill(name)
        p.get_by_role("button", name="Create Token").click()
        token = p.locator("#new-pat-token")
        token.wait_for(state="visible")
        return token.input_value() if token.evaluate("e => 'value' in e") else token.inner_text()

    def revoke(self, name: str):
        p = self.page
        goto(p, "/settings/tokens")
        row = p.locator("tr", has_text=name)
        confirm = row.get_by_role("button", name="Confirm Revoke")
        click_until(p, row.get_by_role("button", name="Revoke", exact=True), confirm)
        confirm.click()
        row.get_by_role("button", name="Revoke", exact=True).wait_for(state="detached")


class Agents:
    def __init__(self, page):
        self.page = page

    def card(self, name: str):
        return self.page.locator(".card", has=self.page.locator(".fw-semibold", has_text=re.compile(f"^{re.escape(name)}$")))

    def create(self, name: str, description: str):
        p = self.page
        goto(p, "/admin/agents")
        p.get_by_placeholder("my-agent").fill(name)
        p.get_by_placeholder("What does this agent do?").fill(description)
        click_until(p, p.get_by_role("button", name="Create Agent"), self.card(name))

    def add_key(self, name: str, key_name: str) -> str:
        p = self.page
        key_input = p.get_by_placeholder("e.g. production")
        click_until(p, self.card(name).get_by_role("button", name="API Keys"), key_input)
        key_input.fill(key_name)
        token = p.locator("#new-agent-key-token")
        click_until(p, p.get_by_role("button", name="Add Key"), token)
        return token.input_value() if token.evaluate("e => 'value' in e") else token.inner_text()

    def revoke_key(self, key_name: str):
        p = self.page
        row = p.locator("tr", has_text=key_name)
        row.get_by_role("button", name="Revoke", exact=True).click()
        confirm = row.get_by_role("button", name=re.compile("^Confirm"))
        try:
            confirm.wait_for(state="visible", timeout=3000)
            confirm.click()
        except Exception:
            pass  # revoked on the first click
        expect_gone(p.locator("tr", has_text=key_name).get_by_role("button", name="Revoke", exact=True))

    def toggle(self, name: str, button: str, badge: str):
        click_until(self.page, self.card(name).get_by_role("button", name=button, exact=True),
                    self.card(name).locator(".badge", has_text=badge))

    def delete(self, name: str):
        p = self.page
        confirm = self.card(name).get_by_role("button", name="Confirm Delete")
        click_until(p, self.card(name).get_by_role("button", name="Delete", exact=True), confirm)
        confirm.click()
        expect_gone(self.card(name))


class Connections:
    def __init__(self, page):
        self.page = page

    def row(self, name: str):
        return self.page.locator("tr", has_text=name)

    def create_filesystem(self, name: str, root: str):
        p = self.page
        goto(p, "/connections")
        click_until(p, p.get_by_role("button", name="Add connection"), p.locator("#connection-name"))
        p.locator("#connection-provider").select_option(label="Local filesystem")
        p.locator("#connection-name").fill(name)
        root_select = p.locator("select", has=p.locator("option", has_text="Choose a root…"))
        if root_select.count():
            root_select.select_option(label=root)
        else:
            p.get_by_placeholder("/data/docs").fill(root)
        p.locator("#connection-save").click()
        self.row(name).first.wait_for(state="visible")

    def delete(self, name: str):
        p = self.page
        goto(p, "/connections")
        click_until(p, self.row(name).get_by_role("button", name="Delete", exact=True),
                    p.locator("#delete-connection-confirm"))
        p.locator("#delete-connection-confirm").fill(name)
        p.locator("#delete-connection-submit").click()
        expect_gone(self.row(name))


class Sources:
    def __init__(self, page):
        self.page = page

    def row(self, name: str):
        return self.page.locator("tr", has=self.page.locator("td", has_text=re.compile(f"^{re.escape(name)}$")))

    def create(self, connection: str, name: str, sub_path: str):
        p = self.page
        goto(p, "/sources")
        dialog = p.locator("#new-source-dialog")
        click_until(p, p.get_by_role("button", name="New source"), dialog)
        select = p.locator("#source-connection")
        label = next(o for o in select.locator("option").all_inner_texts() if connection in o)
        select.select_option(label=label)
        p.locator("#source-name").fill(name)
        p.locator("#source-description").fill("Created by the UI end-to-end journey")
        dialog.get_by_placeholder("team-a").fill(sub_path)
        dialog.get_by_role("button", name="Create", exact=True).click()
        expect_gone(dialog, timeout=60_000)
        self.row(name).first.wait_for(state="visible")

    def sync(self, name: str):
        self.row(name).get_by_role("button", name="Sync").click()

    def delete(self, name: str):
        p = self.page
        goto(p, "/sources")
        click_until(p, self.row(name).get_by_role("button", name="Delete", exact=True),
                    p.locator("#delete-source-confirm"))
        p.locator("#delete-source-confirm").fill(name)
        p.locator("#delete-source-submit").click()
        expect_gone(self.row(name))


# ── The journey ──────────────────────────────────────────────────────────────────────────────────

ADMIN_ONLY = ["/admin/users", "/admin/agents", "/admin/providers", "/connections", "/settings"]
SETTINGS_TABS = ["Embedding", "Chunking", "Search", "LLM", "Upload", "Summary"]


def ui(c, t):
    """Web UI: sign-in, register and invites, containers, search, sources, settings, tokens, agents"""
    shots_dir = getattr(c, "ui_shots", None)
    if not shots_dir:
        print("  UI journey skipped (--no-ui)", flush=True)
        return
    try:
        from playwright.sync_api import sync_playwright
    except ImportError:
        t.record("UI", "Playwright is installed", lambda: t.expect(
            False, "playwright isn't importable: pip install playwright && playwright install --only-shell chromium"))
        return
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True)
        try:
            walk(Ui(c, t, browser, Shots(Path(shots_dir))))
        finally:
            browser.close()


def walk(u: Ui):
    c, t = u.c, u.t
    run = c.run
    anon, admin = u.page(), u.page()

    # Sign-in, and register refused without an invitation (CON-169 made the API route admins-only).
    u.item(anon, "UI sign-in", "anonymous visit to / goes to the login page",
           lambda: (goto(anon, "/"), expect_path(anon, r"/login"), anon.locator("#email").wait_for()))
    u.item(anon, "UI sign-in", "wrong password shows an error",
           lambda: (Login(anon).sign_in(c.admin_email, "wrong-password-1A!"), expect_text(anon, "Invalid email or password.")))
    u.item(anon, "UI register", "/register without an invitation is refused",
           lambda: (goto(anon, "/register"), expect_text(anon, "No invitation token provided")))
    u.item(anon, "UI register", "/register with a bad invitation is refused",
           lambda: (goto(anon, "/register?token=not-a-real-invitation"),
                    expect_text(anon, "This invitation is invalid, expired, or has already been used.")))
    signed_in = u.item(admin, "UI sign-in", "admin signs in and lands on Containers",
                       lambda: (Login(admin).sign_in(c.admin_email, c.admin_password),
                                expect_path(admin, r"^[^?]*://[^/]+/?$"),
                                admin.get_by_role("heading", name="Containers").wait_for(), True)[-1])
    if not signed_in:
        return
    u.item(admin, "UI sign-in", "admin sees the Admin menu",
           lambda: t.expect(nav_link(admin, "admin/users").count() == 1, "no Users link in the admin's nav"))

    # Invitations: the admin invites an Editor, who registers through the link.
    invitee_email, invitee_password = f"ui-editor-{run}@example.com", f"Ui-Editor-{run}-9a!"
    users = Users(admin)
    link = u.item(admin, "UI invites", "admin creates an Editor invitation",
                  lambda: (users.open(), users.invite(invitee_email, "Editor"))[-1])
    invitee = u.page()
    if link:
        url = link if link.startswith("http") else u.base + "/" + link.lstrip("/")
        url = re.sub(r"^https?://[^/]+", u.base, url)  # the link names the server's own address

        def register():
            goto(invitee, url)
            invitee.locator("#invite-displayName").fill(f"UI Editor {run}")
            invitee.locator("#invite-password").fill(invitee_password)
            invitee.locator("#invite-confirmPassword").fill(invitee_password)
            invitee.get_by_role("button", name="Create Account").click()
            expect_path(invitee, r"^[^?]*://[^/]+/?$", timeout=30_000)
            invitee.get_by_role("heading", name="Containers").wait_for()
        if u.item(invitee, "UI invites", "invitee registers through the link and is signed in", lambda: (register(), True)[-1]):
            u.item(invitee, "UI invites", "Editor has no Admin menu",
                   lambda: t.expect(nav_link(invitee, "admin/users").count() == 0, "an Editor sees the admin nav"))
            for path in ADMIN_ONLY:
                u.item(invitee, "UI invites", f"Editor is refused {path}",
                       lambda path=path: (goto(invitee, path), expect_text(invitee, "Access Denied")))
            u.item(invitee, "UI invites", "signed-in Editor is sent away from /register",
                   lambda: (goto(invitee, "/register"), t.expect("/register" not in invitee.url,
                                                                 f"stayed on {invitee.url}")))
            u.item(invitee, "UI invites", "the used invitation link is refused",
                   lambda: (invitee.context.clear_cookies(), goto(invitee, url),
                            expect_text(invitee, "This invitation is invalid, expired, or has already been used.")))

            anon_api = t.Api(c.base, c.spec)

            def editor_register_refused():
                tok = anon_api("POST", "/api/v1/auth/token",
                               body={"email": invitee_email, "password": invitee_password}).json["accessToken"]
                anon_api.as_({"Authorization": f"Bearer {tok}"})(
                    "POST", "/api/v1/identity/register", 403,
                    body={"email": f"ui-sneak-{run}@example.com", "password": "Ui-Sneak-1a!x", "role": "Viewer"})
            t.record("UI invites", "API cross-check: the Editor's token can't register users (403)", editor_register_refused)

    second = f"ui-revoke-{run}@example.com"
    u.item(admin, "UI invites", "admin revokes a pending invitation",
           lambda: (users.open(), users.invite(second, "Viewer"), users.open(),
                    click_until(admin, users.invite_row(second).get_by_role("button", name="Revoke"),
                                admin.locator(".alert-success, .alert-danger")),
                    expect_gone(users.invite_row(second))))

    def delete_user():
        users.open()
        row = users.user_row(invitee_email)
        click_until(admin, row.get_by_role("button", name="Delete", exact=True),
                    row.get_by_role("button", name="Confirm", exact=True))
        row.get_by_role("button", name="Confirm", exact=True).click()
        expect_gone(users.user_row(invitee_email))

    # Containers, upload, search.
    home, files = Containers(admin), Files(admin)
    cname, word = f"ui-{run}", f"ultramarine{run}"
    doc = c.work / f"ui-kestrel-{run}.md"
    doc.write_text(f"# Kestrel ledger\n\nThe kestrel ledger reconciles at dawn. Its code word is {word}.\n", encoding="utf-8")
    made = u.item(admin, "UI containers", "create a container",
                  lambda: (home.open(), home.create(cname, "Created by the UI journey"), True)[-1])
    if made:
        u.item(admin, "UI containers", "open it and upload a file",
               lambda: (home.card(cname).first.locator(".container-card-name").click(),
                        expect_path(admin, r"/containers/"), files.upload(doc)))
        u.item(admin, "UI containers", "the file reaches Ready", lambda: files.wait_ready(doc.name))
        u.item(admin, "UI search", "search finds the uploaded text",
               lambda: Search(admin).run(cname, "When does the kestrel ledger reconcile?", word))
        u.item(admin, "UI containers", "delete the file",
               lambda: (home.open(), home.card(cname).first.locator(".container-card-name").click(),
                        expect_path(admin, r"/containers/"), files.row(doc.name).first.wait_for(),
                        files.delete(doc.name)))
        u.item(admin, "UI containers", "delete the empty container", lambda: (home.open(), home.delete(cname)))

    # Personal tokens.
    tokens, pat = Tokens(admin), f"ui-pat-{run}"
    if u.item(admin, "UI tokens", "create a personal token",
              lambda: (t.expect(bool(tokens.create(pat)), "empty token"), True)[-1]):
        u.item(admin, "UI tokens", "revoke it", lambda: tokens.revoke(pat))

    # Agents and their keys.
    agents, agent, key = Agents(admin), f"ui-agent-{run}", f"ui-key-{run}"
    if u.item(admin, "UI agents", "create an agent", lambda: (agents.create(agent, "Created by the UI journey"), True)[-1]):
        if u.item(admin, "UI agents", "add an API key and see it once",
                  lambda: (t.expect(bool(agents.add_key(agent, key)), "empty key"), True)[-1]):
            u.item(admin, "UI agents", "revoke the key", lambda: agents.revoke_key(key))
        u.item(admin, "UI agents", "disable the agent", lambda: agents.toggle(agent, "Disable", "Disabled"))
        u.item(admin, "UI agents", "enable it again", lambda: agents.toggle(agent, "Enable", "Active"))
        u.item(admin, "UI agents", "delete the agent", lambda: agents.delete(agent))

    # Connections and sources: a filesystem connection over the stack's /data/fs, a source on guides/.
    conns, sources = Connections(admin), Sources(admin)
    conn, src = f"ui-fs-{run}", f"ui-guides-{run}"
    if u.item(admin, "UI sources", "create a filesystem connection",
              lambda: (conns.create_filesystem(conn, "/data/fs"), True)[-1]):
        if u.item(admin, "UI sources", "create a source on it", lambda: (sources.create(conn, src, "guides"), True)[-1]):
            u.item(admin, "UI sources", "sync the source", lambda: sources.sync(src))
            u.item(admin, "UI search", "search the source finds the synced file",
                   lambda: Search(admin).run(src, "How do I restart the falcon service?", "falconctl", timeout=INGEST_TIMEOUT))
            u.item(admin, "UI sources", "delete the source by typing its name", lambda: sources.delete(src))
        u.item(admin, "UI sources", "delete the connection by typing its name", lambda: conns.delete(conn))

    # Settings: every tab, a save, and the embedding connection test.
    def tab(name):
        goto(admin, "/settings")
        click_until(admin, admin.get_by_role("button", name=name, exact=True),
                    admin.locator("button.nav-link.active", has_text=re.compile(f"^\\s*{name}\\s*$")))
        admin.get_by_role("button", name=re.compile("^Save")).first.wait_for()
    for name in SETTINGS_TABS:
        u.item(admin, "UI settings", f"{name} tab", lambda name=name: tab(name))
    u.item(admin, "UI settings", "save the Chunking tab",
           lambda: (tab("Chunking"), admin.get_by_role("button", name="Save Changes").click(),
                    admin.locator(".alert-success").first.wait_for()))
    u.item(admin, "UI settings", "Embedding: test the connection",
           lambda: (tab("Chunking"), tab("Embedding"),
                    click_until(admin, admin.get_by_role("button", name="Test Connection"),
                                admin.locator(".alert-success, .alert-danger"), tries=2, wait_ms=60_000),
                    t.expect(admin.locator(".alert-success").count() > 0,
                             f"connection test failed: {admin.locator('.alert-danger').first.inner_text()[:200]}")))

    # Pages every journey above passes through, plus the ones that only need to render.
    for path, heading in [("/sources", "Sources"), ("/connections", "Connections"), ("/admin/providers", None),
                          ("/approvals", None), ("/profile", None)]:
        u.item(admin, "UI pages", f"{path} renders",
               lambda path=path, heading=heading: (goto(admin, path),
                                                    t.expect("access-denied" not in admin.url and "/login" not in admin.url,
                                                             f"{path} sent the admin to {admin.url}"),
                                                    admin.get_by_role("heading", name=heading).first.wait_for() if heading
                                                    else admin.locator("h1, h2, h3").first.wait_for()))

    if link:
        u.item(admin, "UI invites", "admin deletes the invited user", delete_user)

    # Sign out.
    u.item(admin, "UI sign-out", "Logout returns to the login page",
           lambda: (goto(admin, "/"), admin.locator("button.nav-logout-btn, button:has-text('Logout')").first.click(),
                    expect_path(admin, r"/login")))
    u.item(admin, "UI sign-out", "a signed-out visit to /settings asks to sign in",
           lambda: (goto(admin, "/settings"), expect_path(admin, r"/login")))
