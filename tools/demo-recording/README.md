# Demo recordings

Scripts that record the GIFs in `docs/demos/` from a real Connapse build with seeded demo data only. Raw video goes
to `DEMO_RAW` (default `E:/tmp/con-200/raw`); `to-gif.mjs` turns each plan in `gifs/` into a GIF and fails if it is
15 s or longer, or 2 MB or larger. Needs Node, ffmpeg, Docker, [vhs](https://github.com/charmbracelet/vhs) and the
`claude` CLI.

Every command runs from the repository root with the demo stack's compose settings:

```powershell
$env:COMPOSE_PROJECT_NAME = 'connapse-demo'
$env:COMPOSE_FILE = 'docker-compose.yml;tools/demo-recording/compose.demo.yml'
$env:DEMO_EXAMPLE_DATA = '<a copy of tools/demo-recording/example-data>'
npm install --prefix tools/demo-recording; node tools/demo-recording/make-pdf.mjs
```

1. `tools/demo-recording/reset.ps1` starts an empty stack on port 5201. To record this checkout's code rather than the
   published image, set `CONNAPSE_VERSION` to a local tag (for example `demo`) and run `docker compose build web` first.
2. Quickstart: `vhs tools/demo-recording/tapes/quickstart.tape` (from `DEMO_RAW`, with `DEMO_REPO` set), then
   `node tools/demo-recording/shots/quickstart.mjs`. It registers the owner and creates the `halcyon` container.
3. Two users: `node tools/demo-recording/shots/setup-two-users.mjs`, then `shots/two-users.mjs <source id>`.
4. Agent shots: set `DEMO_AGENT_KEY_FILE` to a path outside the repository and run `shots/setup-agent.mjs`. Then run
   `shots/agent-session.mjs <name> "<prompt>"` for each transcript in `transcripts/` (the prompt is in its file), and
   `shots/agent-replay.mjs agent-remembers s1a s1b`, `... memory-history s4`, `... code-tools s6`.
5. Linked results: `node tools/demo-recording/shots/linked-results.mjs` (records a take with link search off and one with it on, then restores the original setting).
6. Sources, connectors and providers. Without the quickstart, `shots/setup-owner.mjs` makes the owner and `halcyon`.
   - `shots/connect-a-folder.mjs` connects `shared-folder/` over SFTP, then syncs it and searches it. Make a key
     outside the repository with `ssh-keygen -t ed25519 -N "" -f <path>`, set `DEMO_SFTP_KEY` to that path, and start
     the server with `docker compose --profile demo-sftp up -d`.
   - `shots/connectors.mjs` adds the example source server as a custom provider; start it first with
     `docker compose --profile demo-source up -d`. Record it on a stack that has not run step 3: the two-user setup
     claims that server's address.
   - `shots/provider-settings.mjs` switches the embedding and LLM providers without saving and runs Test Connection.
7. `node tools/demo-recording/to-gif.mjs tools/demo-recording/gifs/<name>.json` for each plan.

When done: `docker compose --profile demo-source --profile demo-sftp down -v`.
