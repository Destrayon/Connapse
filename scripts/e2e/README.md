# End-to-end test of the endpoint-consistency epic

`epic_e2e.py` builds the current checkout, starts it with Docker Compose on empty volumes beside an
SFTP server and the custom source example, and walks every user journey the epic changed: REST, MCP,
the `connapse` CLI and the web UI. It prints a pass/fail table and writes it to `<work>/report.md`, then tears the
stack down (containers, volumes and the images it built).

Every response is checked against the committed `docs/api/openapi.json`: the status must be declared,
errors must be `application/problem+json` with the documented `type`, and bodies must match their
schema. The report also lists every spec operation the run never called.

## Requirements

- Docker with Compose v2.24 or later, Python 3.10+ (standard library only), the .NET 10 SDK for the CLI.
- For the UI journey, Playwright for Python and its headless Chromium shell, ideally in a venv:
  `pip install playwright` then `playwright install --only-shell chromium`. No installed browser is used.
- About 3 GB of disk for the images and the embedding model. Every container is capped at 2 CPUs and
  4 GB and runs on the CPU only.

## Run

```bash
python scripts/e2e/epic_e2e.py --work E:/tmp/e2e
```

The CLI is cloned from `https://github.com/Destrayon/connapse-cli` at `origin/main` and built into
`<work>/cli-bin`. Its config lives in `<work>/clihome`, so your own `~/.connapse` is never read or changed.

| Option | Use |
| --- | --- |
| `--work DIR` | Scratch folder for test data, keys, the CLI build and the report (default `artifacts/e2e`). |
| `--port N` | Host port for the web app, bound to 127.0.0.1 (default 5163). |
| `--keep` | Leave the stack running after the run, to debug a failure. |
| `--no-up` | Test a stack started earlier with `--keep`. |
| `--no-build` | Reuse images built by an earlier run. |
| `--down` | Only tear the stack down. |
| `--cli-ref REF` / `--cli-src DIR` | Build the CLI from another ref, or from a local checkout. |
| `--no-cli` | Skip the CLI journey. |
| `--no-ui` | Skip the web UI journey. |
| `--shots DIR` | Where the UI journey saves its screenshots (default `<work>/ui-shots`). |

The stack differs from a default install in two ways. The rate limits are raised, because the run makes
far more than 200 requests a minute. A second copy of the custom source example (`custom-perms`) runs with
`permissions.check` on, because a custom provider needs it. Before you start a run, check `docker ps`: the
stack is seven containers, so don't start it beside another heavy workload.

The UI journey (`ui.py`) drives the Blazor pages in one headless Chromium, with a browser context per role:
anonymous, the admin, and an Editor the admin invites. It signs in and out, checks that `/register` refuses
anyone without an invitation and that the Editor is refused every admin page (and the register API), creates
and deletes a container, uploads and searches a file, creates a filesystem connection and source, syncs and
searches it, walks every settings tab, and creates and revokes a personal token, an agent and its key. Every
item saves a full-page screenshot named `NN-area-item.png`, and a failed item's ends in `-FAIL`. The harness
runs below normal priority, so the browser and builds it starts inherit that.

The harness's own spec checks have unit tests that need no Docker:
`python -m unittest discover -s scripts/e2e -p "test_*.py"`.

`E2E_PROJECT` sets the Compose project name (default `connapse-e2e`). The exit code is 0 only when
every item passed and every spec operation was called.

## What it can't run locally

S3, Azure Blob, GitHub App and Atlassian sources and the cloud sign-in links need real cloud accounts.
For those the harness checks that each route answers with its documented status and problem type.
