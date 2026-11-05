"""Browser test of the paged memory list at 50,000 facts (CON-247).

Starts its own Postgres and MinIO (each capped at 2 CPUs and 4 GB), runs the web app from this checkout in Release,
seeds one team memory space with 50,000 current facts (a third corrected once, so about 66,000 versions) straight
into Postgres, then times in headless Chromium how long the list takes to show its first page, page 2 and a search.
Each must show within 1 second. Screenshots and the timings go to --work; everything it started is removed at the end.

    pip install playwright && playwright install --only-shell chromium
    python scripts/e2e/memory_scale.py --work E:/tmp/con-247
"""

from __future__ import annotations

import argparse
import json
import os
import secrets
import socket
import statistics
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PREFIX = "connapse-memscale"
FACTS = 50_000
BUDGET_S = 1.0
TRIALS = 3
CAP = ["--cpus", "2", "--memory", "4g"]


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run(*args: str, check=True, **kw) -> str:
    done = subprocess.run(args, capture_output=True, text=True, check=False, **kw)
    if check and done.returncode != 0:
        raise RuntimeError(f"{' '.join(args)} failed: {done.stderr.strip() or done.stdout.strip()}")
    return done.stdout.strip()


def psql(sql: str) -> str:
    return run("docker", "exec", "-i", f"{PREFIX}-pg", "psql", "-U", "connapse", "-d", "connapse", "-v", "ON_ERROR_STOP=1",
               "-qtA", input=sql)


def wait(what: str, ready, timeout=180.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        try:
            if ready():
                return
        except Exception:  # noqa: BLE001 - not up yet
            pass
        time.sleep(1)
    raise TimeoutError(f"{what} wasn't ready within {timeout:.0f} s")


def start_services(pg_port: int, minio_port: int):
    down()
    run("docker", "run", "-d", "--name", f"{PREFIX}-pg", *CAP, "-p", f"127.0.0.1:{pg_port}:5432",
        "-e", "POSTGRES_DB=connapse", "-e", "POSTGRES_USER=connapse", "-e", "POSTGRES_PASSWORD=connapse_dev",
        "pgvector/pgvector:pg17")
    run("docker", "run", "-d", "--name", f"{PREFIX}-minio", *CAP, "--user", "0", "-p", f"127.0.0.1:{minio_port}:9000",
        "-e", "MINIO_ROOT_USER=connapse_dev", "-e", "MINIO_ROOT_PASSWORD=connapse_dev_secret",
        "cgr.dev/chainguard/minio", "server", "/data")
    wait("Postgres", lambda: run("docker", "exec", f"{PREFIX}-pg", "pg_isready", "-U", "connapse", "-d", "connapse",
                                 check=False).endswith("accepting connections"))


def start_app(work: Path, port: int, pg_port: int, minio_port: int, email: str, password: str) -> subprocess.Popen:
    # Published, as an install runs it: run from the source folder, Production serves no Blazor script.
    published = work / "app"
    run("dotnet", "publish", str(ROOT / "src" / "Connapse.Web"), "-c", "Release", "-o", str(published), "-nologo", "-v", "q")
    env = dict(os.environ,
               ASPNETCORE_ENVIRONMENT="Production",
               ASPNETCORE_URLS=f"http://127.0.0.1:{port}",
               ConnectionStrings__DefaultConnection=(
                   f"Host=127.0.0.1;Port={pg_port};Database=connapse;Username=connapse;Password=connapse_dev"),
               Knowledge__Storage__MinIO__Endpoint=f"127.0.0.1:{minio_port}",
               Knowledge__Storage__MinIO__AccessKey="connapse_dev",
               Knowledge__Storage__MinIO__SecretKey="connapse_dev_secret",
               Knowledge__Storage__MinIO__UseSSL="false",
               # Nothing listens here; the list never embeds.
               Knowledge__Embedding__BaseUrl="http://127.0.0.1:9",
               CONNAPSE_ADMIN_EMAIL=email,
               CONNAPSE_ADMIN_PASSWORD=password,
               Identity__Jwt__Secret=secrets.token_hex(32),
               RateLimiting__ApiPermitLimit="100000",
               RateLimiting__AuthPermitLimit="100000")
    log = open(work / "app.log", "w", encoding="utf-8")  # noqa: SIM115 - closed with the process
    flags = subprocess.BELOW_NORMAL_PRIORITY_CLASS if sys.platform == "win32" else 0
    app = subprocess.Popen(["dotnet", "Connapse.Web.dll"],
                           cwd=published, env=env, stdout=log, stderr=subprocess.STDOUT,
                           creationflags=flags)

    def up():
        if app.poll() is not None:
            raise SystemExit(f"the app exited ({app.returncode}); see {work / 'app.log'}")
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/login", timeout=5) as r:
            return r.status == 200
    wait("the web app", up, timeout=300)
    return app


def seed(email: str) -> str:
    """One team memory space: 50,000 current facts by 50 authors, a third with one earlier, corrected version."""
    admin = psql(f"SELECT id FROM users WHERE normalized_email = upper('{email}');")
    space = psql("INSERT INTO containers (name, kind, audience) VALUES ('scale-memory', 'memory', 'team') RETURNING id;")
    space = space.splitlines()[0]
    psql(f"""
        CREATE TEMP TABLE authors AS
          SELECT n, CASE WHEN n = 1 THEN '{admin}'::uuid ELSE gen_random_uuid() END AS id,
                 CASE WHEN n = 1 THEN 'admin' ELSE 'person ' || n END AS name
          FROM generate_series(1, 50) n;
        CREATE TEMP TABLE seed AS
          SELECT g, gen_random_uuid() AS id, gen_random_uuid() AS old_id,
                 now() - g * interval '1 minute' AS recorded
          FROM generate_series(1, {FACTS}) g;
        INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, valid_from, recorded_at,
                                  superseded_at, author_user_id, author_name, scope)
          SELECT s.old_id, s.id, '{space}', 'service ' || s.g % 900, 'setting ' || s.g % 13, 'old value ' || s.g,
                 NULL, s.recorded - interval '30 seconds', s.recorded, a.id, a.name, 1
          FROM seed s JOIN authors a ON a.n = s.g % 50 + 1 WHERE s.g % 3 = 0;
        INSERT INTO memory_facts (id, chain_id, container_id, subject, attribute, value, valid_from, recorded_at,
                                  replaces_id, author_user_id, author_name, scope)
          SELECT s.id, s.id, '{space}', 'service ' || s.g % 900, 'setting ' || s.g % 13,
                 'value ' || s.g || CASE WHEN s.g % 250 = 0 THEN ' runs on the kafka cluster' ELSE '' END,
                 s.recorded - interval '1 day', s.recorded, CASE WHEN s.g % 3 = 0 THEN s.old_id END, a.id, a.name, 1
          FROM seed s JOIN authors a ON a.n = s.g % 50 + 1;
        ANALYZE memory_facts;
    """)
    return space


def timed(page, act, done) -> float:
    start = time.perf_counter()
    act()
    done()
    return time.perf_counter() - start


def browse(base: str, space: str, email: str, password: str, shots: Path) -> dict[str, list[float]]:
    from playwright.sync_api import expect, sync_playwright

    url = f"{base}/containers/{space}"
    times: dict[str, list[float]] = {"first page": [], "page 2": [], "search": []}
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 900})
        page.set_default_timeout(15_000)
        page.goto(f"{base}/login")
        page.locator("#email").fill(email)
        page.locator("#password").fill(password)
        page.get_by_role("button", name="Sign In").click()
        page.wait_for_url(lambda u: "/login" not in u)

        rng = page.locator("#memory-range")
        rows = page.locator(".memory-row")
        # One untimed pass first, so the timings measure the list rather than the app's first-request start-up.
        page.goto(url)
        expect(rows.first).to_be_visible()

        for trial in range(TRIALS):
            last = trial == TRIALS - 1
            times["first page"].append(timed(page, lambda: page.goto(url),
                                             lambda: expect(rng).to_contain_text("1–50 of")))
            expect(rows).to_have_count(50)
            expect(rng).to_contain_text(f"of {FACTS:,} facts you can see")
            if last:
                page.screenshot(path=str(shots / "01-first-page.png"))

            times["page 2"].append(timed(page, lambda: page.locator("#memory-next").click(),
                                         lambda: expect(rng).to_contain_text("51–100 of")))
            expect(rows).to_have_count(50)
            if last:
                # The pager and the range sit below the 50 rows.
                rng.scroll_into_view_if_needed()
                page.screenshot(path=str(shots / "02-page-2.png"))

            search = page.locator("#memory-search")
            search.fill("kafka")

            def submit():
                search.press("Enter")
            times["search"].append(timed(page, submit, lambda: (
                page.wait_for_url(lambda u: "q=kafka" in u), expect(rows.first).to_contain_text("kafka"))))
            expect(rng).to_contain_text("of 200 facts you can see")
            if last:
                page.screenshot(path=str(shots / "03-search.png"))

        page.goto(url)
        expect(rng).to_contain_text(f"of {FACTS:,} facts you can see")
        page.locator("#memory-filter-author").click()
        page.screenshot(path=str(shots / "04-author-filter.png"))
        page.locator("#memory-author-me").click()
        # The admin wrote one fact in 50.
        expect(rng).to_contain_text(f"of {FACTS // 50:,} facts you can see")
        page.screenshot(path=str(shots / "05-filtered.png"))
        browser.close()
    return times


def down():
    run("docker", "rm", "-fv", f"{PREFIX}-pg", f"{PREFIX}-minio", check=False)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--work", type=Path, default=ROOT / "artifacts" / "memory-scale")
    args = ap.parse_args()
    work: Path = args.work.resolve()
    shots = work / "shots"
    shots.mkdir(parents=True, exist_ok=True)

    email, password = "admin@memscale.test", "Ms-" + secrets.token_urlsafe(18) + "9a!"
    port, pg_port, minio_port = free_port(), free_port(), free_port()
    app = None
    try:
        start_services(pg_port, minio_port)
        app = start_app(work, port, pg_port, minio_port, email, password)
        space = seed(email)
        times = browse(f"http://127.0.0.1:{port}", space, email, password, shots)
    finally:
        if app is not None:
            if sys.platform == "win32":
                run("taskkill", "/PID", str(app.pid), "/T", "/F", check=False)
            else:
                app.kill()
            app.wait()
        down()

    ok = True
    lines = [f"Memory list at {FACTS:,} current facts; budget {BUDGET_S:.1f} s for each, every trial.", "",
             "| Step | Trials (s) | Median (s) | Within budget |", "| --- | --- | --- | --- |"]
    for step, values in times.items():
        within = max(values) <= BUDGET_S
        ok &= within
        lines.append(f"| {step} | {', '.join(f'{v:.2f}' for v in values)} | {statistics.median(values):.2f} | "
                     f"{'yes' if within else 'NO'} |")
    report = "\n".join(lines)
    print(report)
    (work / "report.md").write_text(report + "\n", encoding="utf-8")
    (work / "times.json").write_text(json.dumps(times, indent=2), encoding="utf-8")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
