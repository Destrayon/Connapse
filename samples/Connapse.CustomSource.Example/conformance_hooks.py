"""Conformance hooks for the example server: what only its "system" (a folder) can do.

Usage, as the --create-command, --delete-command and --sign-in-command of connapse-source-check:
    python conformance_hooks.py create  <data-folder>
    python conformance_hooks.py delete  <data-folder>
    python conformance_hooks.py sign-in <data-folder>

create adds a file to $CONNAPSE_SCOPE and prints its document ID; delete removes $CONNAPSE_DOCUMENT_ID (the base64url of its path);
sign-in posts $CONNAPSE_LINK_TOKEN to $CONNAPSE_LINK_URL, signs in as alice and prints the redirect URL.
"""
import base64, http.cookiejar, os, re, sys, urllib.parse, urllib.request, uuid

DATA = sys.argv[2]
action = sys.argv[1]
scope = os.environ.get("CONNAPSE_SCOPE", "")

if action == "create":
    rel = f"conformance-{uuid.uuid4().hex}.md"
    with open(os.path.join(DATA, "scopes", scope, rel), "w") as f:
        f.write("# Created by the conformance suite")
    # The example server's document ID is the base64url of the path inside the scope.
    print(base64.urlsafe_b64encode(rel.encode()).decode().rstrip("="))
elif action == "delete":
    # The ID comes from the middle man under test, so only a file this script created, directly inside
    # the scope folder, is ever removed.
    doc = os.environ["CONNAPSE_DOCUMENT_ID"]
    rel = base64.urlsafe_b64decode(doc + "=" * (-len(doc) % 4)).decode()
    if not re.fullmatch(r"conformance-[0-9a-f]{32}\.md", rel):
        sys.exit(f"refusing to delete '{rel}': not a file this hook created")
    scope_dir = os.path.realpath(os.path.join(DATA, "scopes", scope))
    target = os.path.realpath(os.path.join(scope_dir, rel))
    if os.path.dirname(target) != scope_dir:
        sys.exit(f"refusing to delete '{rel}': outside the scope folder")
    os.remove(target)
elif action == "sign-in":
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *a, **k):
            return None
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(jar))
    link_url = os.environ["CONNAPSE_LINK_URL"]
    page = opener.open(link_url, urllib.parse.urlencode({"token": os.environ["CONNAPSE_LINK_TOKEN"]}).encode()).read().decode()
    state = re.search(r'name="state" value="([^"]+)"', page).group(1)
    complete = urllib.parse.urljoin(link_url, "/link/complete")
    try:
        opener.open(complete, urllib.parse.urlencode({"state": state, "user": "alice"}).encode())
    except urllib.error.HTTPError as e:
        print(e.headers["Location"])
