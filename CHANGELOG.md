# Changelog

All notable changes to Connapse are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- On a server reached over plain http, such as one on a home network, `connapse auth login` and apps that sign in through the browser with a `localhost` callback no longer fail. An app that registers without saying what kind it is, and whose callbacks are all on `localhost`, `127.0.0.1` or `[::1]`, is now registered as an app on the user's computer instead of being refused for not using https. [Add https to a home server](docs/self-host/home-network-https.md) says which sign-ins still need https and how to get it without opening the server to the internet.

## [v0.4.0] - 2026-11-07

v0.4.0 is the first release since v0.3.2 (March 2026) and the largest so far. It is also a clean break: an install upgrading from v0.3.2 has steps to take, listed under [Upgrading from v0.3.2](#upgrading-from-v032). Read them before you upgrade.

### Highlights

- **Search the tools your team already uses.** Connapse now indexes GitHub repositories, Jira, Confluence, Amazon S3, Azure Blob Storage and SFTP servers, including the folders on your own computer, and any other system through a small server you write to the [custom source protocol](docs/protocols/custom-source-v1.md). GitHub, Jira and Confluence results are limited to what each person can read there, and S3 and Azure Blob results are too once per-user permissions are set up. SFTP has no per-user permissions: everyone who can search the source sees every file in it.
- **Agents can remember.** Six new MCP tools let an AI agent store dated facts with their sources in a memory space, correct them later without losing the history, and recall them in another session. A personal space is seen only by its owner and their agents; a team space by everyone. People see and manage the facts on the **Memory** page.
- **One command to start.** `docker compose -f oci://ghcr.io/destrayon/connapse:compose up -d -y` gives a working server with search ready: nothing to clone, no file to edit, no default passwords. Whole folders upload from the browser with their subfolders.
- **Agents can read code.** C#, TypeScript and Python files are split at each definition, and four new MCP tools find a symbol, its references, what a change would affect, and a map of the repository.
- **Results bring their neighbours.** With link search on, a search also returns what its best results point to: the issue a pull request closes, the epic above a Jira issue, the Confluence page an issue links.
- **Better answers out of the box.** Keyword search ranks with BM25 (the ranking most search engines use), hybrid search was retuned on measured question sets, every passage carries its document's title, and PDFs are read by layout, including tables and scanned pages, when the server has the memory for it (see step 4 below). An optional local reranker re-scores the top results.
- **One set of rules everywhere.** The web app, the REST API and the MCP server now share one implementation per action, so they can't disagree about who may do what. Errors share one format, the API has a published OpenAPI spec, and an agent's deletes wait for a person to approve them.
- **Sign in from MCP clients.** Connapse is an OAuth 2.1 authorization server, so clients such as Claude Code and Cursor sign in through the browser instead of a pasted key.

### Upgrading from v0.3.2

Migrations run when the app starts and only go forward, so the backup in step 2 is the only way back. [Upgrade Connapse](docs/self-host/upgrade.md) has the commands.

1. Let uploads that are still indexing finish. Jobs queued by v0.3.2 live in memory and are lost when it stops.
2. Back up, following [Back up and restore Connapse](docs/self-host/backup-and-restore.md).
3. Save each S3, Azure Blob or filesystem container's settings while v0.3.2 is still running: `GET /api/containers` returns every container's `connectorType` and `connectorConfig` (bucket, prefix, root path and so on). The upgrade deletes both, and you need them to set those containers up again as sources. Keep the output somewhere private, since a config can hold credentials.
4. Take the new `docker-compose.yml` from the release package. The `minio/minio` image the old file names is no longer published, so the old file can't start MinIO. If you keep your own compose file, change the MinIO image to `cgr.dev/chainguard/minio` and add `user: "0"`. Reading PDFs by layout needs about 3 GB for `web`, which the release file gives it; with less, PDFs are read in plain content order and the log says so.
5. Set `CONNAPSE_VERSION=v0.4.0`, pull and start, as in the upgrade guide. On first start, the upgrade removes every document and folder from each old S3, Azure Blob or filesystem container, because without the record of where they came from Connapse could no longer check them against the cloud's permissions. This removes only Connapse's copies, never the files in your bucket or folder. The containers themselves stay, empty; delete them or leave them. Search also refuses any S3, Azure Blob or Atlassian document that is missing its cloud address, so no such document is shown to everyone who can read its container.
6. Set each of those up again as a source in a new container, using the settings you saved in step 3. A filesystem container comes back as an SFTP source, with [Connect this computer](docs/sources/sftp.md), or as a folder upload. See the first breaking change below.
7. Re-issue every personal access token and agent key that has no scopes, and check that tokens used for admin work carry the admin scope they need.
8. Move MCP clients that use the SSE transport to Streamable HTTP at `/mcp/`, and expect MCP clients that signed in with OAuth to sign in once more.
9. Install the standalone [connapse-cli](https://github.com/Destrayon/connapse-cli) and run `connapse auth login`.
10. Ask everyone who linked an AWS or Azure account to link it again under **Profile → Integrations**.
11. Run a reindex with `POST /api/settings/reindex` and an empty body. It re-reads only the documents whose parser or settings changed, which after this upgrade is most PDFs, Word and PowerPoint files and text files. `POST /api/containers/{id}/reindex` with `{"dryRun": true}` shows how many first. See [Change the embedding model](docs/guides/change-embedding-model.md#do-it-with-the-api).

### Breaking changes

**Storage and deployment**

- **Containers no longer connect to S3, Azure Blob or local folders** (#348, #375). Those are now *sources*, which sync read-only from a *connection* an administrator sets up. A container is only ever Connapse's own storage. An S3, Azure Blob or filesystem container from v0.3.2 stops syncing, and the upgrade removes its documents and folders, since they lost their permission source; the empty container stays. **To upgrade:** set each one up again as a source (steps 5 and 6 above): set up the provider under **Admin → Providers** and add a connection on the **Connections** page and a source on the **Sources** page for each. A local folder is reached over SFTP. See [Sources](docs/sources/index.md).
- `connectorType` and `connectorConfig` are gone from container requests and responses, and `POST /api/containers/test-connection` is removed. A request that still sends `connectorType` gets an ordinary container. Connections are tested on the **Connections** page or through `/api/v1/connections`.
- **The Azure Blob connector and Azure AD sign-in were rebuilt** (#480, #505). The `Identity__AzureAd__*` and `Identity__AwsSso__*` settings are no longer read. Set Azure up again under **Admin → Providers → Azure**, following [Azure setup](docs/sources/azure-setup.md).
- **Linked AWS and Azure accounts are deleted** by the upgrade (#480). Each person links again under **Profile → Integrations**.
- **The MinIO image changed** to `cgr.dev/chainguard/minio`, running as root so it can read the volume the old image wrote (#538). See step 4 above.
- **Compose ships no default passwords**, and only `web` publishes a port from `docker-compose.yml`; `docker-compose.dev.yml` now binds to `127.0.0.1`. An existing install keeps its data and passwords. The `with-ollama` profile is gone: Ollama starts by default, so a hosted-provider install sets `CONNAPSE_LOCAL_MODEL=off`. `docker-compose.yml` uses the prebuilt image unless you add `--build`. See [Upgrade Connapse](docs/self-host/upgrade.md#moving-to-the-one-command-start) (CON-263)
- **An address set by environment variable or command line now beats the saved one**, so a value saved outside Docker can no longer point a Compose server at `localhost`. This covers settings under `Knowledge:` ending in `Url` or `Endpoint`; the Ollama and TEI addresses other providers share apply only while one of those is the saved provider. An Ollama or TEI address saved on a Compose server has to move to `docker-compose.override.yml`. Every other saved setting still beats the deployment (#399, CON-260)
- **The CLI moved to its own repository** (#304), and the old CLI sign-in routes `/cli/authorize` and `/api/v1/auth/cli/exchange` are removed (#286). Install [connapse-cli](https://github.com/Destrayon/connapse-cli); it reads the same `~/.connapse/credentials.json`, so `connapse auth login` once is enough.

**Tokens, scopes and sign-in** (#1063)

- **`POST /api/v1/identity/register` needs an administrator** with `admin:users`. It used to create an account for anyone, including callers who weren't signed in (#1051). Make accounts with invites instead.
- **A token with no scopes passes no scope check** (#940). Personal access tokens and agent keys created without scopes lose access to scope-protected routes and MCP tools. Re-issue them with scopes; the create forms now tick `knowledge:read` by default.
- **A scoped token gets only its scopes, never its owner's role** (#848). An Owner's `knowledge:read` token can no longer change settings.
- **Admin actions need their admin scope on a token:** `admin:sources` to create, change, sync, retry or delete a source (#1013); `admin:users` for every agent and agent-key action (#960); `admin:audit` for the audit log; `admin:oauth-clients` for OAuth clients; `admin:settings` for settings.
- **Container writes need `knowledge:write`** on a token (#909).
- **The `agent:ingest` scope is gone** (#1052). A key or token created with it is still accepted, but the scope is dropped and grants nothing, and the upgrade removes it from existing keys and tokens. Use `knowledge:write`.
- **Agent keys can hold only `knowledge:read` and `knowledge:write`** (#960, #1052). Any other scope is refused.
- **An agent's deletes over MCP wait for approval by default, existing keys included** (#848). An agent's `container_delete`, `delete_file`, `bulk_delete` or `memory_forget` returns "pending approval" until someone approves it on **Approvals** (`/approvals`). See [Approve a held delete](docs/guides/tokens-and-agents.md#approve-a-held-delete).
- **OAuth refresh tokens issued before this release are refused** with `invalid_grant`, and their client signs in again (#937). `/oauth/token` now refreshes only tokens issued to the same OAuth client.

**MCP** (#1063)

- **MCP serves only Streamable HTTP at `POST /mcp/`** (#847). The SSE transport (`GET /mcp/sse`, `POST /mcp/message`), `GET /mcp/` and `DELETE /mcp/` are removed. A browser request from a foreign `Origin` gets 403; clients that send no `Origin` are unaffected.

**REST API**

- **Errors are `application/problem+json`** with a stable `type` (#834). Scripts that read `error` keep working: file, folder and cloud-unlink errors still carry their old codes there (#1082, #1083). Scripts that check for `application/json` on a failure, or read fields other than `error`, `message`, `field` and `detail`, may need changes. [Upgrade Connapse](docs/self-host/upgrade.md#scripts-that-read-error-codes) maps each old code to its new `type`.
- **A bulk upload no longer fails as a whole** when one file has an unsupported type or a name that is too long. It succeeds, and that file's entry carries its own `error`. `cloud_access_denied` is gone; an upload to anything but Connapse storage gets `write_denied`.
- **`GET` and `PUT /api/containers/{id}/settings` return only the search settings the container chose** (CON-157). A missing search setting means "use the deployment's value". `PUT` stores only the search settings the body names; a client that relied on a partial body resetting the rest must now send them. A body that isn't a JSON object gets 400.
- **`DELETE /api/sources/{id}` needs `?confirmName=<source name>`** (#697), and answers 400 without it.
- **`GET /api/v1/sources/scope-schema/{provider}` matches only an exact provider name** (#933). A padded name such as `" S3"`, or a list, gets 404.
- **A `PUT /api/v1/providers/github/app` that loses a race with another save gets 409 `github-app-changed`** (#1087). It used to get `github-app-replace-unconfirmed`, or overwrite the other save silently.

**Search and indexing results**

These change what a search returns rather than making a call fail.

- **Keyword search ranks with BM25 by default** (#549). Set **Settings → Search → KeywordRanker** (`Knowledge__Search__KeywordRanker`) to `TsRank` for the old ranking.
- **`FusionAlpha` defaults to 0.65** (#566, #669). A saved value equal to an old default (0.3 or 0.75) is moved to 0.65; other saved values are kept.
- **`LinkMaxHops` defaults to 1 instead of 2** (CON-33). Set it to 2 to keep the old two-step walk.
- **Markdown files (`.md`, `.markdown`, `.mdx`) are always split by their headings**, whatever chunking strategy is set (#318).
- **PDFs are read by layout by default** (#654). Indexing is slower and each parser host can use about 800 MB more memory. A parser with less than about 900 MB reads PDFs without the layout model and logs a warning, so give `web` the memory in step 4. Set **Settings → Upload → PdfTextMode** (`Knowledge__Upload__PdfTextMode`) to `ContentOrder` for the faster reading.
- **Embeddings for models that expect prefixes, such as the default `nomic-embed-text`, now get them** (#560). Search keeps working on old vectors until a reindex moves them over. Turn `UseModelPrefixes` off to keep the old embeddings.

**Only for servers built from `main` between May and November 2026**

- **Folder summaries are removed and deleted on upgrade** (#1073). Their endpoints answer 404, the `summary` settings category and `Hangfire:SummaryWorkerCount` are gone, and `container_list` and `container_describe` no longer return summaries. No release ever shipped them.
- **Filesystem sources are removed** (CON-265, #393), because SFTP reaches a machine's folders and works in Docker and on hosted servers. **The upgrade deletes every Filesystem connection and source, along with everything indexed from them, and this can't be undone.** To keep searching those folders, add them again over SFTP with [Connect this computer](docs/sources/sftp.md), or use folder upload for a one-off folder. The API and agent tools refuse the `Filesystem` provider and the `filesystem` scope, and `Sources:Security:AllowedFilesystemRoots` is no longer read.
- **Memory on documents containers is removed** (CON-255), because agents answered no better with it than without it (0.68 with a check on what they saved and 0.61 without, against 0.70 with memory off). Memory lives only in personal and team spaces. A migration deletes every fact and star on documents containers, including facts shared with the team there, **which can't be undone**, so back up the database before upgrading if you need them. Facts already in personal and team spaces are untouched. Naming a documents container in `memory_remember`, `memory_recall`, `memory_timeline` or the memory REST routes is refused, `GET /api/memory/spaces` drops `kind=container` and `counts.containers`, and the Memory tab on documents containers is gone.

### Added

**Setup and uploads**
- One-command start: `docker compose -f oci://ghcr.io/destrayon/connapse:compose up -d -y` gives a working Connapse with search ready, with nothing to clone and no file to edit. The first start makes random database and file storage passwords and keeps them in a `secrets` volume, the embedding model downloads on its own, and until it finishes search answers `503` with [`model-not-ready`](docs/problems.md#model-not-ready), the layout shows progress and uploads wait. `GET /health/ready` reports the same state. `CONNAPSE_LOCAL_MODEL=off` keeps Ollama from downloading or starting, and `COMPOSE_PROFILES=with-reranker` turns the reranker on, both from `.env` (CON-263)
- Upload a whole folder from the browser: choose or drop a folder and everything inside it uploads with its subfolder paths, and the files that fail are listed. `POST /api/containers/{id}/files` takes `paths`, one folder per file (CON-257)

**Sources and permissions**
- Connections, sources and providers replace connector containers, with a **Providers**, **Connections** and **Sources** page and the `/api/sources` and `/api/v1/connections` routes (#348, #368, #371, #372, #418)
- Amazon S3 with per-user search permissions read from S3 Access Grants, and a guided setup through CloudShell (#414, #416, #436)
- Azure Blob Storage with per-user permissions read from Azure role assignments and Data Lake ACLs (#505)
- SFTP sources, with guided setup for a remote server (#406, #408)
- GitHub repositories, public and private, filtered by what each person can read on GitHub (#530)
- Confluence Cloud and Jira Cloud, filtered by each person's Atlassian permissions (#697, #735)
- A versioned protocol for custom sources, so any system can be indexed through a small server, with a conformance test tool (#737, #781)
- Search results are trimmed to what the person searching may read in the source system, and anything Connapse can't confirm is left out (#425, #426)

**Search**
- Link-aware search: with `EnableLinkSearch` on, search also returns documents its top results link to, one step out. Links come from GitHub (pull request closes issue, sub-issue parent), Jira (issue links, epic, parent, fix version, linked Confluence pages), Confluence (parent page, labels, page links, mentioned Jira issues) and code files. Linked documents pass the same permission check as ordinary results (CON-33, CON-34)
- BM25 keyword ranking in plain PostgreSQL, with no extension needed (#549)
- An optional local reranker, started with `--profile with-reranker` (#542), and a Voyage AI reranker (#300)
- Per-container HNSW vector indexes, built in the background (#574)
- The document title at the top of every passage, on by default (#675)
- Embedding size setting works for any OpenAI-compatible model: "Ask the model for this size" sends `dimensions` to models such as Qwen3-Embedding, and vectors of the wrong size are refused with an error naming the setting to change (#666, reported by @marcusyoung)
- A cache that skips re-embedding text it has embedded before (#300)

**Files**
- Code files: `.cs`, `.ts`/`.mts`/`.cts`/`.tsx` and `.py`/`.pyi` are parsed with tree-sitter, cut into one passage per definition, and linked by definitions, imports, calls and base types (CON-36, CON-108, CON-109)
- More file types, detected text encodings, and parsers that run in their own sandboxed process so one bad file can't stop indexing (#593, #641)
- Layout-aware PDF reading, borderless tables and scanned pages read by region (#642, #652, #653)
- New chunking strategies: heading-aware for Markdown, sentence window, and sentence-aware fixed size (#312, #318)
- A reindex that can be previewed with a dry run, capped, and continued (#593)

**MCP and agents**
- Agent memory MCP tools: `memory_spaces`, `memory_remember`, `memory_correct`, `memory_forget`, `memory_recall` and `memory_timeline`, storing dated facts with sources and keeping their history (CON-39)
- Memory spaces: every fact lives in a personal space only its owner sees or a team space everyone sees, an agent must name the space it saves to and is told who will see the fact, and a fact is shown only to readers who can open every source it cites. People manage spaces and facts on the **Memory** page and at `/api/memory/spaces` (CON-255)
- Code MCP tools: `code_find_symbol`, `code_references`, `code_impact` and `code_repo_map` (CON-37)
- `container_describe` MCP tool, and tool annotations that mark which tools read, delete or can be repeated safely (#274, #345)
- OAuth 2.1 sign-in for MCP clients, with discovery, PKCE and dynamic client registration (#286, #306, #308)
- An **Approvals** page for deletes agents ask for (#1063)
- `tools/list` shows a token only the tools its scopes allow (#1063)

**API and administration**
- REST routes for connections, provider status, every Settings category, source search and stats, bulk file delete, file retry, container rename, the audit log and account links (#1063)
- A committed OpenAPI spec at `docs/api/openapi.json` (#1063)
- `GET /api/v1/info` and `GET /api/v1/auth/me` (#302, #303)
- Every admin setting on the Settings page, with help text (#655)
- Settings saves with a stale `If-Match` get 412 instead of overwriting another save (#1063)
- Documentation rewritten under `docs/`; the wiki is retired (#1063)

### Changed
- Background work runs on Hangfire, so indexing survives a restart, and failed documents retry (#333, #562)
- A document has one status, and a reindex keeps the old passages searchable until the new ones are ready (#562)
- Results whose scores tie are ordered by document path, so re-indexing gives the same ranking (CON-152, CON-158)
- Search answers 503 `embeddings-unavailable` when the embedding service is down; keyword search still works (#1094)
- The MCP server runs MCP SDK 2.1 and the 2026-07-28 spec (#1063)
- The LLM settings tab says that no feature uses these settings yet (#1074)

### Fixed
- Upgrading deletes the AWS SSO and Cognito settings left behind when those sign-ins were removed, client secrets included. Delete the old client in AWS too: clearing the copy here does not revoke it (#448)
- Files up to the upload limit can really be uploaded: anything over about 30 MB used to fail as a connection error, whatever the limit said. The server, the upload form, folder upload and the admin setting now share `MaxFileBytes`, a file over it gets a clear refusal (`413`, `file-too-large`), and a large upload is held on disk rather than in memory. A reverse proxy needs a body limit 1 MiB above `MaxFileBytes`, so the nginx example now says `257m` (CON-266)
- Audit entries for actions taken by agents were dropped; they are now recorded under the user the agent acts for
- Changing one link-search setting on a container no longer resets its other search settings (CON-157)
- A newly built vector index is used right away instead of being skipped on stale table statistics (CON-34)
- Deleted documents no longer come back: a file whose storage delete fails is retried instead of only logged, and a sync running alongside a folder delete can no longer restore its files (#378, reported and traced by @marcusyoung)
- The Files tab lists every file in a container's root: it used to fetch only the 50 newest documents in the whole container, so after many uploads into a subfolder the older root files disappeared from view (#812, reported by @marcusyoung)
- Uploading a file that is already in the container replaces it instead of failing to index with `23505 duplicate key ... idx_documents_container_path` (#812, reported by @marcusyoung)
- PDFs whose text holds math symbols reach Ready, and a failure whose message holds broken text is saved as Failed instead of leaving the document stuck in Processing (#334, reported by @marcusyoung)
- **Security:** saved API keys no longer reach the browser, and container settings hide them from readers (#1063, #1077)
- **Security:** approval requests no longer store secrets in plain text (#1085)
- **Security:** revoking an OAuth client stops refreshes and code redemptions already in flight, and a refreshed OAuth token keeps only the scopes it was granted (#1063, #1081)
- Deleting a file can no longer wipe a new upload to the same path (#1075)
- Text too long for the Ollama embedding model is split instead of failing the upload (#1027)
- Passages are cut from the source text, so code survives word for word (#1028)
- Parsing adapts to the memory available instead of failing large PDFs, and a parser host that dies is reported and retried once (#657, #677, #1086)
- `$_`, `$&` and similar text no longer splices a document into itself during sentence splitting (#664)
- Settings reloads no longer race (#632)
- A rate-limited `/api` request gets 429 whatever it accepts (#1078)
- A null required field on an Identity request is a 400, not a 500 (#1093)
- API sign-in with a token returns a token instead of a 500 (#1063)
- A missing storage bucket or a storage outage is reported as a storage error instead of "file not found" (#1063)
- Search results show file names in their stored case (#752)
- Dependency updates for published advisories in ASP.NET Core, SignalR and System.Security.Cryptography.Xml (#307, #370, #979, #1096)

## [v0.3.2] - 2026-03-15

### Added
- CLI `files list`, `files delete`, `files get` commands (#166)
- CLI `container stats` command (#165)
- CLI `--pre` flag for update command to install prerelease builds (#231)
- CLI `--help` and `-h` flags for root and all subcommands (#229)
- API `GET /api/containers/{id}/files/{fileId}/content` endpoint (#163)
- API `GET /api/containers/{id}/stats` endpoint (#162)
- API security headers middleware (X-Content-Type-Options, X-Frame-Options, etc.) (#181)
- Bulk MCP tools: `bulk_upload` and `bulk_delete` for batch operations (#128)
- `get_document` MCP tool for full document retrieval (#99)
- `container_stats` MCP tool (#104)
- Rate limiting middleware with per-user and per-IP policies (#103)
- Pagination for listing endpoints (#111)
- MCP search results now include scores and metadata (#93)
- Document IDs in `list_files` MCP output (#100)
- Truncated vs total match count in MCP search (#101)
- Raw text upload support in MCP `upload_file` tool (#118)
- File type validation: reject uploads with unsupported extensions (#194)
- Input validation hardening: centralized `ValidationConstants`, search param bounds, agent field validation, path depth limits (#228)
- `IUploadService` — unified upload pipeline shared by API and MCP endpoints (#214)
- Manual re-embed button in embedding settings (#215)
- Cloud connector integration tests (LocalStack + Azurite) (#114)
- Docker release package and ghcr.io publish (#113)
- Unit tests for ingestion pipeline, identity services, SemanticChunker (#108, #109, #110)
- CloudIdentity and Search integration tests (#112)
- Dynamic tests badge from CI results (#105)
- Connapse branding logos and favicon (#120)
- Convex Combination fusion for hybrid search with configurable alpha (#92)
- DBSF (Distribution-Based Score Fusion) as alternative outlier-robust fusion method
- AutoCut: automatic result trimming via score gap detection

### Fixed
- **Security**: empty `X-Api-Key` header no longer falls through to cookie auth (#224)
- **Security**: path traversal in upload filenames rejected (#183)
- **Security**: control characters rejected in filenames
- Uppercase container names now return 400 instead of 409 (#225)
- Filenames exceeding 255 characters rejected with 400 (#221)
- Zero-byte file uploads rejected with 400 (#193)
- Stale error message cleared when file ingestion succeeds (#242)
- CLI home directory resolution uses `USERPROFILE` env var on Windows (#241)
- CLI container name resolution uses direct lookup instead of broken paginated list (#232)
- CLI `EnsureAuthenticated` missing on container/search/upload/reindex commands (#164)
- CLI source-generated JSON context for trim-safe deserialization (#186)
- CLI pagination parameters added to container list (#185)
- MCP `list_files` works at paths without explicit folder records (#191)
- MCP `delete_file` cancels in-flight ingestion jobs (#146)
- MCP `container_delete` stops watchers and writes audit log (#147)
- API pagination `take` parameter enforces min/max bounds (#188)
- API returns 404 for nonexistent settings category (#192)
- Cookie `Secure` flag adapts to HTTP/HTTPS scheme (#187)
- SSL certificate bypass scoped to localhost only (#160)
- Bootstrap Icons self-hosted instead of CDN (#240)
- LICENSE copyright updated to Connapse Contributors (#161)
- Stale project names and URLs replaced (#168)
- MinIO containers isolated by auto-scoping to container ID prefix (#121)
- Empty parent folders cleaned up after file deletion (#123)
- Folder entries created when uploading files via MCP (#86)
- Keyword search improved for exact term matches (#85)
- Keyword search for technical terms with dual-config tsvector (#91)
- Fire-and-forget admin operations now surface errors (#68)
- N+1 query in user listing replaced with batch JOIN (#102)
- MCP Server file deletion failures now logged instead of swallowed (#61)

### Changed
- Upload pipeline unified via `IUploadService` — API and MCP share same validation and ingestion logic (#214)
- `bulk_delete` delegates to `delete_file` for consistent behavior (#147)
- CLI release assets renamed to `connapse-cli-*` for clarity (#220)
- Version centralized via MinVer git tags (#119)
- `container_delete` parameter standardized to `containerId` (#98)
- InMemory (ephemeral) connector removed (#94)
- Legacy `IngestionWorker` fallback bypassing connector system removed (#84)
- Dead `IngestFromPathAsync` removed from `IKnowledgeIngester` (#69)
- Fake `SearchStreamAsync` that buffered before yielding removed (#67)
- Reflection-based MinIO settings extraction removed (#62)
- MCP server migrated to official C# MCP SDK (#66)

### Documentation
- Agent API key scope model and access boundaries clarified (#226)
- First-time registration flow documented in deployment guide
- `.env.example` expanded with all configuration variables
- Pagination query parameters documented for all list endpoints (#184)
- Bulk API tools documented (#169)
- Container write guard behavior documented (#167)
- MCP documentation updated for all 11 tools (#159)
- README rewritten with benefit-first positioning and GIF hero section

## [v0.3.1] - 2026-03-03

### Added
- 4 connector types: MinIO, Filesystem (FileSystemWatcher), S3 (IAM-only), Azure Blob (managed identity)
- Per-container settings overrides (chunking, embedding, search, upload)
- Cloud identity linking: AWS IAM Identity Center (device auth) + Azure AD (OAuth2+PKCE)
- IAM-derived scope enforcement — cloud permissions as source of truth
- Multi-provider embeddings: Ollama, OpenAI, Azure OpenAI
- Multi-provider LLM: Ollama, OpenAI, Azure OpenAI, Anthropic
- Multi-dimension vector support with partial IVFFlat indexes per model
- Cross-model search with automatic Semantic-to-Hybrid fallback
- Background sync: FileSystemWatcher for local, 5-min polling for cloud containers
- Connection testing for all providers (S3, Azure Blob, MinIO, LLM, embeddings, AWS SSO, Azure AD)
- Provider-specific API keys and IOptionsSnapshot for settings
- 457 passing tests (unit + integration)

### Fixed
- CLI self-update for global tool installs
- AWS account IDs removed from logs (CodeQL)
- User-controlled values sanitized in ConnectorWatcherService logs

## [v0.2.2] - 2026-02-28

### Added
- CLI `self-update` command
- CLI `--version` flag

## [v0.2.1] - 2026-02-27

### Fixed
- Release workflow: correct binary name and NuGet key guard
- CodeQL security alerts: log injection and PII exposure
- Input sanitization for user-provided values

### Security
- SECURITY.md updated to reflect v0.2.0 auth implementation

## v0.2.0 - 2026-02-27

### Added
- Three-tier authentication: Cookie sessions + Personal Access Tokens + JWT (HS256)
- Role-based access control (Admin / Editor / Viewer / Agent)
- Invite-only user registration (admin-controlled)
- First-class agent entities with API key lifecycle management
- Agent management UI and PAT management UI
- Audit logging for uploads, deletes, and container operations
- CLI auth commands (`auth login`, `auth whoami`, `auth pat`)
- CLI PKCE authentication flow
- GitHub Actions release pipeline (native binaries + NuGet global tool)
- 256 passing tests (unit + integration)
- `/health` endpoint

### Fixed
- PAT UX: auto-revoke old tokens on re-login

## v0.1.0 - 2026-02-17

### Added
- Document ingestion pipeline (PDF, Office documents, Markdown, plain text)
- Hybrid search: vector similarity + keyword full-text search
- Container-based file browser with folder hierarchies
- Web UI (Blazor Server)
- REST API
- Command-line interface
- MCP server for Claude Desktop integration
- PostgreSQL + pgvector for vector storage
- MinIO (S3-compatible) object storage
- Ollama integration for local embeddings
- Docker Compose deployment

[Unreleased]: https://github.com/Destrayon/Connapse/compare/v0.4.0...HEAD
[v0.4.0]: https://github.com/Destrayon/Connapse/compare/v0.3.2...v0.4.0
[v0.3.2]: https://github.com/Destrayon/Connapse/compare/v0.3.1...v0.3.2
[v0.3.1]: https://github.com/Destrayon/Connapse/compare/v0.2.2...v0.3.1
[v0.2.2]: https://github.com/Destrayon/Connapse/compare/v0.2.1...v0.2.2
[v0.2.1]: https://github.com/Destrayon/Connapse/compare/v0.2.0...v0.2.1
