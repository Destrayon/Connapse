# How should Connapse start with one `docker compose` command?
**Date:** 2026-11-07
**Status:** Reviewed
**Built on:** no prior corpus material (the Connapse knowledge base was not available in this session)

## Executive summary

One command is achievable with Compose alone. A published Compose file (`docker compose -f oci://ghcr.io/destrayon/connapse:compose up -d -y`) removes the clone, a one-shot `setup` container can generate secrets, and the app can download the embedding model itself while reporting that it is still getting ready. No studied project combines all three: this is a composition of sound parts, not a copy of a known recipe. The main uncertainty is delivery: published files need Compose 2.34 or newer, and neither that minimum nor the real GHCR path could be run here, so the docs must keep a download-the-file fallback. The sharpest risk found is that MinIO silently starts with `minioadmin:minioadmin` if its secret file is missing, so MinIO must not start until `setup` has succeeded.

## Research brief

**Question:** How do established self-hosted projects handle first-run secrets, automatic model download and a one-command start with no clone, and which of those fit Connapse's Compose files?

**Sub-questions:**
1. How do projects create secrets on first start, and how do Postgres and MinIO behave when those secrets change or go missing?
2. How do projects get a model into Ollama or an equivalent automatically, and what do they show while it downloads? Can the model be downloaded given Connapse's internal `backend` network?
3. Can Compose deliver a stack with no clone (OCI artifacts), and can extras be switched from `.env` with no change to the start command?

**Out of scope:** Kubernetes, installer scripts as the primary path, Filesystem sources (CON-265 removes them).

**Success criteria:** a design for CON-263 with each load-bearing behaviour either sourced or tested on the engine here (Compose 5.5.1, Docker Engine 29.8.2).

## Findings by sub-question

### First-run secrets across self-hosted Compose projects

None of the 13 projects checked uses a one-shot init container for secrets, so that pattern is sound but has no direct precedent. Open WebUI, n8n and Nextcloud AIO generate secrets in the app. Sentry and Coolify use an install script. Authentik, Dify, Supabase, Immich, Paperless, Plausible, Mattermost and Teable ask the user to fill in a file or ship a placeholder, which is the step CON-263 removes.

Tested behaviour of the stores Connapse uses:

- **Postgres** ignores its password once data exists, and a wrong password still passes `pg_isready`. Setting `POSTGRES_PASSWORD` and `POSTGRES_PASSWORD_FILE` together is fatal even with existing data, an empty `POSTGRES_PASSWORD` beside the file is fine, a missing file is fatal, and a root-owned 0600 file works.
- **MinIO** reads `MINIO_ROOT_USER_FILE` and `MINIO_ROOT_PASSWORD_FILE`. **If the file is missing it starts healthy with `minioadmin:minioadmin`**, which fails open. New credentials take effect on existing data: buckets survive and the old credentials stop working. `mc ready local` still works with file credentials. The Chainguard image has no busybox, so nothing should depend on its shell.
- **Compose** runs a one-shot service on every `up`, so it must be idempotent, and it does not restart dependents. `restart: unless-stopped` on one hangs `up`, so it needs `restart: "no"`. `--wait` exits 0 with this shape. `secrets: file:` cannot point at a generated file, because a missing source becomes a directory. Compose also cannot interpolate volume contents, so the web app must read the files itself.
- **Failure mode:** wiping the secrets volume while keeping the data produces new values and locks Postgres out, so backups must include the secrets volume.

### Automatic model download and readiness

No project studied pulls into a bundled Ollama automatically on start apart from Docker's genai-stack, which uses a one-shot container that blocks its dependants. Spring AI's `when_missing` blocks startup and its docs advise against it in production. Open WebUI and Onyx bake models into images. Immich, Onyx, LocalAI, Khoj and RAGFlow download at run time without Ollama. No project had a download banner or a `Retry-After`, so the "still getting ready" behaviour here is Connapse's own design, following the ASP.NET Core health-check docs: liveness stays healthy while readiness waits for a slow startup task.

Ollama facts: `POST /api/pull` streams newline-delimited JSON with `status`, `total` and `completed`, and ends in `success`. Errors can arrive mid-stream after HTTP 200 as `{"error":...}`, so every line must be parsed. Interrupted pulls resume, concurrent pulls share one download, and if every client disconnects the download cancels. A re-pull fetches the manifest first and so fails offline even if the model exists, so `POST /api/show` must be checked first (404 means missing). `nomic-embed-text` is 274 MB. The Ollama image has no `curl`, so it cannot be used in a health check.

**Network (tested):** the `backend` network is `internal: true`, but `ollama` is also on the `frontend` bridge, and Docker's default route uses the non-internal network whatever the names sort to. `registry.ollama.ai` answered 200 from a container attached this way, and moby's `Endpoint.Less` ranks internal networks after others. No network change or `gw_priority` is needed. This was tested on Docker Desktop, not on native Linux, though the engine code is shared.

The download should live in the app, not a one-shot container: a one-shot would block `docker compose up` for minutes and could not report progress to the UI, and the app must re-run the same check when the configured model changes. Pulling should apply only to the bundled sidecar, never to a user's own Ollama. No source settles the external case; that is a judgment call.

### One command with no clone, and switches from `.env`

Compose can load a file from an OCI registry with `-f oci://`. The minimum is Compose 2.34.0 (Docker Desktop 4.40+), it is not experimental, and anonymous pulls work on public GHCR artifacts. `up` shows a variables table when the file uses `${...}`: the default answer is yes, empty non-interactive stdin exits 1, and `-y` skips it. Raw HTTPS URLs are not supported as `-f`. `.env` in the current folder is loaded, and tested from a published file that includes `COMPOSE_PROFILES`.

Publishing fails on `build:`-only services and local `include`, and `env_file` content ships only with `--with-env`, so the published file needs `image:` and `environment:` interpolation. Named volumes only, because relative paths resolve into Compose's cache. The project name comes from the folder unless `name:` is set, and adding `name:` to the existing files would silently orphan existing volumes, so `name: connapse` belongs only in a small overlay used at publish time. Tested: two `-f` files publish and run together.

In practice almost nobody uses `oci://` yet: only BeeCompose among the projects found. Immich downloads a file then runs `up`, Dify, Plausible and Supabase clone, Paperless and Ollama use `curl | sh`. So the docs should keep a plain download fallback for older Compose.

Tested for switches: `scale: 0` alone still pulls the image. `scale: 0` with `pull_policy: never` starts and downloads nothing, and dependents with `depends_on.required: false` start. Empty interpolation is rejected by both fields, and Compose interpolation has no negation, so a single variable cannot be written directly into both. **A variable service name in `extends` solves it:** two hidden template services, `local-model-on` and `local-model-off`, and `extends: {service: local-model-${CONNAPSE_LOCAL_MODEL:-on}}`. Tested from a published file: off downloads nothing, default starts it, and a bad value stops with `service "local-model-false" not found`. This needs `!reset` to clear the template's profile, which arrived in Compose 2.24, so that becomes the documented minimum for file-based installs. `COMPOSE_PROFILES=with-reranker` handles the reranker. An unknown profile is silently ignored, so the old `--profile with-ollama` keeps working.

## Conflicts and uncertainties

- Docs disagree on whether bind mounts block publishing; the file avoids them, so it does not matter.
- Compose issue #13899 reports a 5.3.0 regression with scale-0 dependencies. `required: false` is the safe form and is what was tested, on 5.5.1 only.
- `!reset` minimum (2.24) and `oci://` minimum (2.34) come from docs. Older Compose binaries were not run: the download was blocked by this environment's safety policy.
- One-shot secret generation has no precedent in the projects studied; it rests on the tests above.

## Gaps — what we did not find

- No project shows download progress in its UI, so nothing to copy.
- No source for how to treat a user's own external Ollama.
- A real GHCR publish was not tested: only a local plain-HTTP registry was. The artifact cannot exist until the next release.
- Native Linux networking was not tested.

## Source quality assessment

Almost all claims rest on primary sources: project repositories, official docs and source code. Behaviour marked as tested was run here on Docker Desktop. Nothing rests on tertiary sources.

## Sources

**Primary, Docker and Compose:** https://raw.githubusercontent.com/docker/docs/main/content/manuals/compose/how-tos/oci-artifact.md, https://raw.githubusercontent.com/docker/docs/main/data/summary.yaml, https://raw.githubusercontent.com/docker/docs/main/content/manuals/desktop/release-notes.md, https://docs.docker.com/compose/how-tos/project-name/, https://docs.docker.com/compose/how-tos/init-containers/, https://docs.docker.com/reference/compose-file/services/, https://docs.docker.com/reference/cli/docker/network/create/, https://docs.docker.com/engine/release-notes/28/, https://github.com/docker/compose/tree/main/pkg/remote, https://github.com/docker/compose/blob/main/cmd/compose/options.go, https://github.com/docker/compose/blob/main/pkg/compose/publish.go, https://github.com/docker/compose/blob/main/pkg/compose/dependency_wait.go, https://github.com/compose-spec/compose-go/blob/main/cli/options.go, https://github.com/compose-spec/compose-go/blob/main/loader/loader.go, https://github.com/docker/compose/issues/13899, https://github.com/docker/compose/issues/10360, https://github.com/moby/moby/blob/master/daemon/libnetwork/sandbox.go

**Primary, Ollama and readiness:** https://raw.githubusercontent.com/ollama/ollama/main/docs/api.md, https://github.com/ollama/ollama/blob/main/server/download.go, https://ollama.com/library/nomic-embed-text, https://docs.spring.io/spring-ai/reference/api/embeddings/ollama-embeddings.html, https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0

**Primary, Postgres and MinIO:** https://github.com/docker-library/postgres/blob/master/docker-entrypoint.sh, https://github.com/docker-library/docs/blob/master/postgres/README.md, https://github.com/minio/minio/blob/master/cmd/common-main.go, https://github.com/minio/mc/blob/master/cmd/ready-main.go, https://images.chainguard.dev/directory/image/minio/overview

**Primary, other projects:** https://github.com/docker/genai-stack/blob/main/docker-compose.yml, https://github.com/open-webui/open-webui/blob/main/backend/start.sh, https://github.com/open-webui/open-webui/blob/main/Dockerfile, https://github.com/n8n-io/n8n/blob/master/packages/core/src/instance-settings/instance-settings.ts, https://github.com/nextcloud/all-in-one/blob/main/php/src/Data/ConfigurationManager.php, https://github.com/getsentry/self-hosted/blob/master/install/generate-secret-key.sh, https://github.com/coollabsio/coolify/blob/main/scripts/install.sh, https://docs.goauthentik.io/install-config/install/docker-compose/, https://github.com/langgenius/dify/blob/main/docker/README.md, https://github.com/supabase/supabase/blob/master/docker/.env.example, https://github.com/immich-app/immich/blob/main/docker/example.env, https://github.com/paperless-ngx/paperless-ngx/blob/main/docker/compose/docker-compose.env, https://github.com/plausible/community-edition/blob/master/README.md, https://github.com/mattermost/docker/blob/main/env.example, https://github.com/teableio/teable/tree/develop/dockers/examples/standalone, https://github.com/onyx-dot-app/onyx/blob/main/deployment/docker_compose/docker-compose.yml, https://github.com/immich-app/immich/blob/main/server/src/repositories/machine-learning.repository.ts, https://github.com/mudler/LocalAI/blob/main/docker-compose.yaml, https://github.com/khoj-ai/khoj/blob/main/docker-compose.yml, https://github.com/infiniflow/ragflow/blob/main/docker/.env, https://github.com/beevelop/beecompose
