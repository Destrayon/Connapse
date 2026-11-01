# CON-162: How Connapse's docs should be organized

**Date:** 2026-10-31 · **Status:** Proposed · **Feeds:** CON-164 (the CON-E1 docs rewrite)

Connapse's user docs should move out of the GitHub wiki and into `docs/`. Each page should be one of six page types, and the pages should be grouped by what the reader is trying to do: get started, self-host, connect data, do a task, look something up, or understand the system. The API and MCP reference should be generated from the committed OpenAPI spec and code, not maintained by hand. Every current page is mapped to a destination in [the page map](#5-where-every-current-page-goes). The reference pages that CON-E1 made stale are listed in [section 6](#6-what-con-e1-made-stale-or-missing).

Each recommendation cites at least two sources. Source keys such as `[DX1]` resolve in [Sources](#9-sources). The facts about Connapse come from this repo on `epic/783-endpoint-consistency` at `b34f87db`, and from the wiki repo `Destrayon/Connapse.wiki`.

## 1. Projects whose docs users praise

| Project | Evidence of praise | What it does well, and what Connapse should copy |
|---|---|---|
| **Stripe** | Widely cited as the benchmark [ST10][ST11][ST1] | Docs work like an application. Code samples are complete and switch by language. Quickstarts run end to end [ST6][ST12]. Errors return a `doc_url` that points to an error reference [ST9]. Writing is a job requirement, with templates so nobody starts from a blank page [ST1][ST2][ST3]. Every page is available as `.md`, and there is an llms.txt file and a docs MCP server [ST7][ST8]. |
| **Django** | "Hands-down the best documentation" (HN); a core developer says the docs drew him in [DJ4][DJ6] | The landing page explains its four-way split: tutorials, topic guides, reference, and how-to [DJ1][DJ2]. That split predates Diátaxis (a documentation framework built on the same four types) [DJ3]. Topic guides link to the reference instead of repeating it. Reference pages carry no general explanation [DJ2]. Docs are versioned with the code [DJ2][WK4]. |
| **Rust (the Book and doc.rust-lang.org)** | The 2024 survey calls the Book "a favourite learning resource"; reviewers call it the first read [RS1][RS2] | Docs are split into Learn, Use and Master [RS3]. The Book is a narrative read front to back, with project chapters between concept chapters, and it states what it assumes the reader already knows [RS4]. Code examples run as tests [RS7]. Contributor docs live on separate sites: the rustc-dev-guide and the Forge [RS5][RS6]. |
| **Tailwind CSS** | Called "incredibly robust"; its creator treats "documentation as a product" [TW4][TW3] | Every utility reference page follows one fixed template: a one-line summary, a class-to-CSS table, examples that show a rendered result above the code, and customization [TW1][TW2]. The install quickstart is 6 steps [TW2]. |
| **Supabase** | Reviewers compare it to Stripe; praise is mixed on G2 [SB7][SB8] | Top tabs are Start, Products, Build, Manage, Reference and Resources. Self-hosting has its own section [SB1][SB2]. The reference was rebuilt on Stripe's model and is generated from code [SB3][SB4]. The self-host quickstart ends at `docker compose ps` showing every service as healthy [SB6]. It publishes an llms.txt file, a `.md` file per guide, and a docs-search MCP tool [SB2][SB5]. |
| **GitLab** (self-hosted) | Its topic-type system is widely copied, and the frameworks research leaned on it heavily [G1][T1] | The landing page routes readers into Use, Administer and Install [GL0][MM1]. It has four enforced topic types (concept, task, reference, troubleshooting), plus tutorial and get-started pages [G1]–[G6]. |
| **Vercel / Next.js** | Praised for examples that switch between JavaScript and TypeScript [ST10] | It names three sections on the landing page: Getting Started, Guides, API Reference [VC4]. Each page's frontmatter tags its type [VC5]. Pages are served as `.md`, and there are llms.txt and AGENTS.md files [VC1][VC3]. |

Supporting examples: PostHog has a style guide linted by Vale on every PR [PH1][PH2]. Grafana and Mattermost publish complete settings references [GF1][MM2]. Airbyte and Home Assistant enforce a template for every integration page [AB1][HA1].

## 2. Recommendations

**R1. Move user docs into `docs/` and retire the wiki.**
- Search engines index a GitHub wiki only if its repo has 500 or more stars, and Connapse has 21 [GH1][GH2].
- Wiki edits can't go through pull requests [GH3][WK3].
- Docs that live with the code get review, versioning and CI [WTD1][DJ2].
- Of 17 well-known repos checked, 12 have the wiki turned off. These include Django, Rust, Supabase, Tailwind, Kubernetes, Grafana and PostHog [WK1][WK2].
- Jenkins retired its wiki over spam and staleness [JK1][WK3].

Once the content has moved, replace the wiki's Home page with a link to `docs/`. Then the repo owner turns the wiki off in Settings (CON-164 can't do that step).

**R2. Use the Diátaxis types for pages, but group the navigation by what the reader is doing.**
- Every page is one type: tutorial, how-to, reference or explanation [S1][S3][G1].
- Django, NumPy, Canonical, Gatsby and Cloudflare all adopted this split [DJ1][NP1][CN1][GB1][CF1].
- Diátaxis says the four types don't have to be the four top-level folders. The structure should follow "the product as it is for the user" [S8][S9].
- Meng et al. found developers got confused when docs were split by content type at the top level [AC3], and GitLab and Mattermost route their landing pages by reader instead [GL0][MM1].
- So the top level is reader journeys (section 3), and each page inside a journey has one declared type.

Add two types the four don't cover:
- **Quickstart.** Good Docs and GitLab both have one [T3][G1].
- **Troubleshooting.** Good Docs and GitLab both have one [T7][G6].

**R3. Keep the top level to seven groups or fewer, keep it shallow, and make search work.**
- Diátaxis caps lists at about seven items [S9][S8].
- Deep trees are harder to use [NN1][AC3].
- Developers mostly come back to look things up, which is a search task [AC4][AC3].
- GitHub's file view has no docs search. That is one reason for R9's static site.

**R4. Make the quickstart one happy path that ends in a visible success, with every command copy-pasteable.**
- Show the expected output after each step and leave out error branches [T8][OG1].
- Examples must work as pasted. Placeholders broke copying in observed sessions [AC3][AC4].
- Model the ending on Supabase: all containers report healthy, then the reader runs one search that returns a result [SB6][TW2].
- Don't print a time estimate. None of the praised projects does [ST6][TW2].

**R5. Generate the API reference from the spec, and keep explanatory text out of it.**
- Reference describes and does nothing else. Its structure mirrors the product [S6][G4].
- Generated reference is the norm [SB3][RS7][RD1].
- Connapse already commits `docs/api/openapi.json` from the build (#857).
- Render it with a tool that shows each operation's code samples, such as Redoc or Scalar [RD1][SC1].
- Code samples should show a short sequence of calls, not a single call, because that is what helps developers [AC1][AC5].
- The hand-written part of the API docs shrinks to one overview page: base paths, auth, token scopes, versioning, ETags, errors and paging. Workflows such as "automate setup with the API" become how-to guides.

**R6. Keep the error reference at its current path and anchors.**
- RFC 9457 makes a problem's `type` URI its identifier, and says it should resolve to human-readable docs [RFC1].
- Stripe links each error to its entry in the same way [ST9][T9].
- Connapse's `OperationProblems.TypeBase` is `…/docs/problems.md#`. Moving that page changes every error's identifier.

**R7. Give self-hosting its own section: install, configure, run in production, back up, upgrade, troubleshoot.**
- Sentry, PostHog, GitLab and Mattermost all keep operator docs apart from usage docs [SE1][PH4][GL0][MM1].
- Supabase and Sentry follow this same sequence of pages [SB6][SE1].
- The configuration reference lists every setting with its environment variable and default, and states the precedence order once at the top. Grafana, Mattermost and PostHog all do this [GF1][MM2][PH5].

**R8. Write every connector page from one template, and check it in review.**
- Airbyte fails a connector's QA checks when its doc headings are out of order [AB1].
- Home Assistant ties its required doc sections, such as removal steps and troubleshooting, to its quality tiers [HA1][HA2].
- Grafana's data-source pages state permissions up front and include provisioning-as-code examples [GF2].
- Connapse's AWS, Azure, GitHub, Atlassian and custom-source pages already share most of this shape, an earlier project decision (the provider step cards). The template in section 7 formalizes it.

**R9. Make every page work for AI agents.**
- Serve each page as Markdown and publish an `llms.txt` index. Stripe, Supabase, Vercel, PostHog, Cloudflare and GitHub Docs all do [LT1][ST7][SB2][VC1][PH3].
- Connapse's audience is agent builders, so this matters more for Connapse than for most projects.
- Tailwind refused llms.txt over lost ad traffic to its docs [TW5][WK5]. That reason doesn't apply to a free, self-hosted project.
- Phasing: GitHub already serves the raw Markdown. CON-164 writes `docs/llms.txt` now. A static site that also serves `.md` pages is a follow-up, not part of CON-164.

**R10. Keep contributor and design docs out of user navigation.**
- Rust, Kubernetes and Django each give contributor and design material a separate home [RS5][RS6][K1][K2][DJ5].
- Decision records live in the repo, numbered, and are kept when superseded [AD1][AD2].
- Kubernetes labels its archived proposals as historical and says they "should not be considered up to date" [K3][K2].
- Connapse's `docs/research/` and `docs/superpowers/` stay where they are. Research reports and memory link to them. Each gets a one-paragraph README saying it is internal and historical.

**R11. Update docs in the same PR as the change they describe, and check links in CI.**
- Kubernetes can drop a feature from a release when its docs PR is missing [K4]. Write the Docs recommends blocking merges that lack docs [WTD1].
- Run a link checker in CI [LY1][DJ2]. Lychee has a GitHub Action [LY1].
- Pages linked from code must keep their anchors (section 5, "Pinned paths").

**R12. Make the README answer four questions: what Connapse is, why use it, how to start, and where to get help. Link out for everything else.**
- Sources for the four questions: [GH4][OG2].
- makeareadme.com says to move long content into other docs rather than delete it [MR1][OG2].
- Connapse's README is 472 lines and repeats the deployment and development guides. It should keep the pitch, the quickstart and the docs index, and drop the rest.

## 3. The page tree

The top level has six reader journeys plus the contributor area. The type in brackets is each page's Diátaxis type, which goes in its frontmatter as `type:`, following Next.js [VC5]. Create a folder only when it has a page to hold. Diátaxis warns against empty structures [S8].

```
README.md                         pitch, quickstart, docs index (R12)
docs/
├── index.md                      [landing] routes readers by goal (R3)
├── llms.txt                      agent index of every page below (R9)
├── get-started/
│   ├── quickstart.md             [quickstart] compose up → healthy → sign in → upload → search
│   └── connect-an-agent.md       [tutorial] connect Claude through MCP and build a container
├── self-host/
│   ├── install.md                [how-to] requirements, compose profiles (Ollama, reranker, GPU)
│   ├── production.md             [how-to] TLS, secrets, reverse proxy, hardening
│   ├── backup-and-restore.md     [how-to]
│   ├── upgrade.md                [how-to] NEW: upgrade, run migrations, roll back
│   └── troubleshooting.md        [troubleshooting]
├── sources/
│   ├── index.md                  [explanation] containers, connections, sources; what each provider indexes
│   ├── uploads-and-minio.md      [how-to] NEW: the default managed storage
│   ├── filesystem-and-sftp.md    [how-to] NEW: incl. "a machine Connapse cannot see"
│   ├── aws-setup.md              [how-to] connector template (R8)
│   ├── aws-multi-region.md       [how-to]
│   ├── azure-setup.md            [how-to]
│   ├── github-setup.md           [how-to]
│   ├── atlassian-setup.md        [how-to] Confluence and Jira
│   └── custom-source-setup.md    [how-to]
├── guides/
│   ├── users-and-invites.md      [how-to] roles, invites, removing users
│   ├── tokens-and-agents.md      [how-to] personal tokens, scopes, agent keys
│   ├── mcp-clients.md            [how-to] Claude Desktop/Code, Cursor, others; approval prompts
│   ├── automate-with-the-api.md  [how-to] NEW: providers → connections → sources over REST, ETag saves
│   ├── tune-search.md            [how-to] providers, reranker, fusion, chunking
│   └── change-embedding-model.md [how-to] switch model, re-index
├── concepts/
│   ├── how-connapse-works.md     [explanation] the user-level architecture
│   ├── search.md                 [explanation] hybrid search, fusion, reranking, AutoCut
│   ├── permissions.md            [explanation] per-object trimming, cloud identity, fail-closed
│   └── ingestion.md              [explanation] parsing, chunking, file types, summaries
├── reference/
│   ├── rest-api.md               [reference] overview + rendered openapi.json (R5)
│   ├── mcp-tools.md              [reference]
│   ├── configuration.md          [reference] every setting, env var, default (R7)
│   ├── roles-and-scopes.md       [reference] NEW: role × token scope × operation table
│   ├── file-types.md             [reference] NEW: supported formats and limits
│   └── cli.md                    [reference] stub that links to connapse-cli's docs
├── problems.md                   [reference] error types: path pinned (R6)
├── api/openapi.json              generated, path pinned (build writes it)
├── protocols/custom-source-v1.*  [reference] path pinned (code and server authors link it)
├── contributing/                 not in the user nav (R10)
│   ├── development.md            local setup, build, tests, migrations
│   ├── architecture.md           internals: projects, patterns, data flow
│   ├── repo-setup.md
│   ├── confluence-test-site.md
│   └── qa/container-summaries.md
├── plans/  research/  superpowers/   internal, historical (R10)
└── demos/                        images
```

The landing page `docs/index.md` should present each journey as an overview card that says who it is for. Diátaxis says landing pages should read like overviews, not bare link lists [S9][G1].

**Pinned paths.** The app links to the paths below. CON-164 keeps the anchors on all of them.

| Path | Linked from | Rule |
|---|---|---|
| `docs/problems.md` | Every error's `type`, via `OperationProblems.TypeBase` | Never move it |
| `docs/api/openapi.json` | The build | Never move it |
| `docs/protocols/custom-source-v1.md` | Code, and outside source-server authors | Never move it |
| `docs/aws-setup.md`, `docs/azure-setup.md`, `docs/github-setup.md`, `docs/atlassian-setup.md`, `docs/custom-source-setup.md`, `docs/connectors.md` | `Providers.razor`, `Connections.razor`, `ProviderFieldSpec.cs`, source pickers, `AtlassianConnectionTester` | Move them into `sources/` only in the same PR that updates those constants. Leave a one-line "moved to" stub at the old path, because servers still running older releases link there. |

## 4. What the audit found

All facts below are from this repo and `Destrayon/Connapse.wiki` as of 2026-10-31.

**Overall.**
- There are 16 user-facing pages in `docs/` plus the README. There are 13 wiki pages and about 100 internal pages under `research/` and `superpowers/`.
- Nothing tags a page's type, and there is no landing page. The README's docs list links 7 of the 16 pages.

**The wiki is eight months stale.**
- The last content commit was 2026-03-09.
- It still describes things that have since been removed or replaced:
  - the InMemory connector;
  - "5 connector types";
  - "8 MCP tools";
  - AWS device-authorization sign-in, since replaced by Roles Anywhere;
  - the Azure AD setup.
- It predates the connector/source split, so it has no GitHub, Atlassian, SFTP or custom-source pages.
- It duplicates `docs/` on the API, deployment, connectors and MCP. The two copies disagree.

**The README is oversized and out of date.**
- It runs 472 lines and repeats the deployment, development and test commands.
- It says "11 MCP tools". The server has 12 named tools plus 3 generic `connapse_api_*` tools.
- Its one-line quickstart (`docker-compose up -d`) skips the auth variables that the next section says are required.

**Two pages bundle several page types.**
- `docs/deployment.md` (1,063 lines) mixes a quickstart, local development, production, a configuration reference, backup and troubleshooting.
- `docs/architecture.md` (849 lines) mixes user-level explanation with contributor internals. Its sections are labelled by version ("(v0.2.0)", "(v0.3.0)"), not by topic.

**Internal pages sit next to user pages.**
- `PUBLIC_RELEASE_PREP.md` is a March checklist that contains a draft README.
- `docs/GITHUB_SETUP.md` is repo admin setup.
- `docs/confluence-test-site.md` and `docs/agent/container-summaries-qa.md` are for contributors and testers.

**Two files have near-identical names.** `docs/GITHUB_SETUP.md` covers repo administration and `docs/github-setup.md` covers the GitHub connector.

## 5. Where every current page goes

Decisions: **Keep** means it moves or stays largely as is. **Merge** means it is folded into the target. **Rewrite** means it is restructured into the template. **Drop** means it is deleted, or replaced with a stub that links to the new page.

### Repo root

| Page | Decision | Destination and reason |
|---|---|---|
| `README.md` | Rewrite | Trim to the four questions, the quickstart and the docs index (R12). Fix the tool count and the quickstart's auth step. |
| `CONTRIBUTING.md` | Keep | Stays at the root. Development setup moves to `contributing/development.md`, and CONTRIBUTING links to it. |
| `CHANGELOG.md` | Keep | It stops at v0.3.2 under `[Unreleased]`, so it needs v0.4.0 entries before release. |
| `SECURITY.md` | Keep | Move "Self-Hosting Security Checklist" into `self-host/production.md` and link it. |
| `GOVERNANCE.md`, `CODE_OF_CONDUCT.md`, `THIRD_PARTY_NOTICES.md` | Keep | Standard root files. |
| `PUBLIC_RELEASE_PREP.md` | Drop | A one-off March checklist containing a stale draft README. It is in git history if needed. |
| `CLAUDE.md` | Keep | Contributor agent instructions. An `AGENTS.md` pointer is optional [AG1][AG2]. |

### `docs/`

| Page | Decision | Destination and reason |
|---|---|---|
| `api.md` (1,788 lines) | Rewrite | Becomes the `reference/rest-api.md` overview (R5). The per-endpoint tables are replaced by the rendered spec. The workflows move to `guides/automate-with-the-api.md`. The MCP and CLI sections move to their own reference pages. |
| `api/openapi.json` | Keep | Generated and pinned. |
| `problems.md` | Keep | Pinned (R6). Add the error types the spec declares that the page doesn't yet list (section 6). |
| `mcp-tools.md` | Rewrite | `reference/mcp-tools.md`. It is the oldest page (2026-08-22) and is missing tools (section 6). |
| `deployment.md` (1,063 lines) | Rewrite (split) | Quick Start and First-Time Setup → `get-started/quickstart.md` and `self-host/install.md`. Production → `self-host/production.md`. Configuration Reference → `reference/configuration.md`. Backup → `self-host/backup-and-restore.md`. Troubleshooting → `self-host/troubleshooting.md`. Local Development → `contributing/development.md`. "Monitoring (Planned)" → drop. |
| `architecture.md` (849 lines) | Rewrite (split) | The user-level explanation → `concepts/how-connapse-works.md`, `concepts/search.md` and `concepts/ingestion.md`. Projects, patterns and internals → `contributing/architecture.md`. Drop the version labels from section headings. |
| `connectors.md` | Rewrite | `sources/index.md` (explanation). "Indexing files on a machine Connapse cannot see" → `sources/filesystem-and-sftp.md`. "Why connections are UI-only" is now false and comes out (section 6). |
| `aws-setup.md` | Keep | Fit it to the connector template, then move it to `sources/`. Pinned until the code links move with it. |
| `aws-per-user-permissions-multi-region.md` | Keep | `sources/aws-multi-region.md`. |
| `azure-setup.md` | Rewrite | Fit to the template. It has no Prerequisites or Remove section today. Move it to `sources/`. Pinned. |
| `github-setup.md` | Keep | Fit to the template, then move it to `sources/`. Pinned. |
| `atlassian-setup.md` (607 lines) | Keep | Already close to the template. Move it to `sources/`. Pinned, with anchors used by the connection tester. |
| `custom-source-setup.md` | Keep | Add a Remove section, then move it to `sources/`. Pinned. |
| `protocols/custom-source-v1.md` and `.openapi.json` | Keep | Pinned. Listed under Reference in the nav. |
| `GITHUB_SETUP.md` | Keep | `contributing/repo-setup.md`. The move ends the name clash with `github-setup.md`. |
| `confluence-test-site.md` | Keep | `contributing/confluence-test-site.md`. |
| `agent/container-summaries-qa.md` | Keep | `contributing/qa/container-summaries.md`. |
| `demos/*.gif` | Keep | The README and the quickstart use them. Re-record them if the UI has changed. |
| `research/*` (40 files) | Keep | Internal. Add a README marking them historical (R10). |
| `superpowers/plans`, `specs`, `runbooks` (about 60 files) | Keep | Internal. Add a README marking them historical (R10). |

### Wiki (`Destrayon/Connapse.wiki`)

| Page | Decision | Destination and reason |
|---|---|---|
| `Home` | Drop | Replace it with a stub that links to `docs/index.md` (R1). |
| `_Sidebar`, `_Footer` | Drop | Not needed once Home is a stub. |
| `Getting-Started` | Merge | Into `get-started/quickstart.md`. Its walkthrough seeds `connect-an-agent.md`. |
| `Deployment-Guide` | Merge | Into `self-host/install.md` and `reference/configuration.md`. Check its environment variable table against appsettings. |
| `Connectors-Guide` | Merge | Into `sources/*`. Delete the InMemory and "connector type" content, which no longer matches the product. |
| `Search-and-AI-Configuration` | Merge | Into `concepts/search.md` (why) and `guides/tune-search.md` (how). The settings rows go to `reference/configuration.md`. |
| `User-Management` | Merge | Into `guides/users-and-invites.md` and `reference/roles-and-scopes.md`. Remove the device-authorization content. |
| `API-Reference` | Drop | Superseded by the generated reference. Its 26 unversioned routes no longer match. |
| `CLI-Reference` | Drop | The CLI is in its own repo. `reference/cli.md` links there. |
| `MCP-Integration` | Merge | Client configuration goes into `guides/mcp-clients.md`. Its tool list is superseded. |
| `AWS-SSO-Setup` | Drop | Describes the removed device-authorization flow. `aws-setup.md` replaces it. |
| `Azure-AD-Setup` | Drop | `azure-setup.md` replaces it. Check for any app-registration details before deleting. |

## 6. What CON-E1 made stale or missing

CON-E1 (epic #783, about 400 commits ahead of `main`) moved every capability onto shared operations. It also added REST routes for things that used to be UI-only. These are the doc gaps CON-164 must close:

1. **REST reference.**
   - The spec declares 112 paths. `api.md` has no routes for `/api/v1/connections` (8 in the spec), `/api/v1/identity` (9), `/api/v1/invites` or `/api/v1/info`. It covers 2 of the 14 provider routes.
   - The users, settings-category, personal-token, agent-key and folder operations exist, but the guides don't explain them.
   - Generating the reference (R5) fixes the coverage. The overview still has to explain:
     - why some routes are unversioned (`/api/containers`, `/api/sources`, `/api/settings`, `/api/batches`) while others sit under `/api/v1`;
     - that settings saves are conditional on `If-Match`, and an outdated read returns `412`;
     - that every failure is a Problem Details response.
2. **Token scopes and roles.**
   - The `admin:sources` and `admin:settings` token scopes are new. Source changes need Administrator.
   - Administrators, not only the Owner, can now replace a connection's secret.
   - No page has the full role × scope × operation table. Add `reference/roles-and-scopes.md`, generated from the spec's per-operation scope metadata (CON-20) if possible.
3. **MCP tools.**
   - `mcp-tools.md` lacks `container_describe`.
   - It lacks the generic `connapse_api_read`, `connapse_api_search` and `connapse_api_write` tools, which reach every operation marked for MCP.
   - It doesn't explain that the tool list is filtered by the token's scope, that destructive calls and credential calls need approval (#782, CON-20), or that `/mcp` validates the Origin header (#846).
4. **Connections are no longer UI-only.**
   - Connections now have a full lifecycle over REST, including create with the page's save checks, discovery and tests (CON-11, CON-113, CON-115, CON-116).
   - Providers (AWS, Azure, GitHub App and custom) can be set up over the API (CON-118 to CON-122).
   - `connectors.md` "Why connections are UI-only" contradicts this.
   - Every connector page needs an "API equivalent" step alongside its UI steps, and `guides/automate-with-the-api.md` should cover the whole sequence.
5. **Typed source scopes.** Each provider's scope is now a typed schema in the spec (#859, CON-77). The connector pages' "Source" sections should show the JSON for each scope.
6. **Error reference.** Check `problems.md` against the spec's declared problem types, so that every `type` URI resolves to an entry (R6).
7. **README and wiki.** Fix the tool count. Retire the wiki pages listed in section 5.

## 7. Page templates

Each template's headings and order are from the cited templates. Bracketed lines are instructions to the writer. Every page starts with frontmatter:

```yaml
---
title: <page title>
type: quickstart | tutorial | how-to | reference | explanation | troubleshooting
audience: user | admin | operator | developer
---
```

### Quickstart

From [T3][T8], with the success state from [SB6].

```markdown
# Quickstart
[One sentence: what you'll have running at the end.]

## Before you begin
- [Docker version, free RAM and disk, ports. Nothing optional.]

## 1. <Verb> <thing>
[One command block that works as pasted.]
You should see: [expected output]

## 2. ... (5–8 steps, one happy path, no branches)

## You're done when
[The visible success: every service healthy, and one search returns a cited result.]

## Next steps
- [3–5 links, one per journey: connect an agent, add a source, run in production]
```

### Tutorial

From [S4][T2][G2]. Use "we". Every step produces a visible result. Offer no choices.

```markdown
# Tutorial: <verb> <outcome>
[What we'll build, and what you need to know already.]

## Before you begin
## <Step as an imperative> (one H2 per stage, about 7 at most)
[Action → code → "Notice that…" result]
## What you built
[One paragraph. Don't restate the steps.]
## Next steps
```

### How-to

From [S5][T4][G3]. The title is a verb plus a noun. It covers one goal for a reader who already knows the product.

```markdown
# <Verb> <object>
Use this when you want to <goal>.

## Prerequisites
- [Role and token scope first, then everything else]

## Steps
1. [One action per step. Where to act before what to do. "Optional:" prefix for optional steps.]
2. ... (10 at most; split the guide if longer)

## Do it with the API
[The same steps as REST calls, linked to the operations in reference/rest-api.md.]

## Related
```

### Connector (a how-to specialization)

From [AB1][HA1][GF2], following the existing ProviderStepCard shape. Every section is required. If a section doesn't apply, say so in one line.

```markdown
# <Provider> setup
[One line: what this connects, and which editions or versions.]

## What gets indexed
[Object types, what's excluded, and why]
## How permissions work
[Per-object trimming at parity with the source system; what is fail-closed]
## Before you begin
[Roles, grants and policies, as copyable JSON or CLI]
## 1. <Provider step> … ## N. Source
[Easy setup, then Manual values, per step; each with "Do it with the API"]
## Sync behaviour
[Schedule, incremental or full, and how deletes are handled]
## Limitations
## Troubleshooting
[One H3 per error message the tester returns. These anchors are linked from the app; keep them.]
## Removing <Provider>
[What Connapse deletes and what it leaves in the provider]
```

### Reference

From [S6][T5][G4]. The title is a noun. It describes, gives an example, and contains no instructions or opinion. Its structure mirrors the product.

```markdown
# <Thing> reference
[1–2 sentences: what's listed and where it comes from (generated or hand-written).]

## <Group>   (ordered as the product orders it)
| Name | Type | Default | Required | Description |
### <Entry>
[Description, example, scope or role needed, errors it can return (linked to problems.md)]
```

For `reference/configuration.md`, use the layout from [GF1][MM2][PH5]:
- State the precedence order once, at the top: appsettings < environment file < user secrets < environment variables < the settings table < CLI arguments.
- Then the environment variable naming rule (`Section__Key`).
- Then an "Always required" table.
- Then one table per settings group, with these columns: `Setting | UI path | Env var | Type | Default | Restart? | Secret?`

### Explanation

From [S7][T6][G5]. The title should read naturally after "About". The page has no steps.

```markdown
# <Concept>
[Definition in the first paragraph.]

## Why it works this way
[Design decisions, the alternatives rejected, the trade-offs]
## How it fits together
[Optional diagram]
## When it matters to you
[Use cases]
## Related
[Links to the how-tos and the reference. Don't repeat their content.]
```

### Troubleshooting

From [T7][G6]. Use one topic per error. The title is the error text, so a reader who searches for the error lands on it.

```markdown
# Troubleshooting <area>

## Error: <exact message text>
You might see: <where it appears>
**Cause:** This happens when …
**Fix:**
1. …
```

## 8. Style rules

Every rule below has at least two sources. CON-164 can turn them into Vale rules later; PostHog lints its docs with Vale [PH1].

- Address the reader as "you". Tutorials use "we". Diátaxis wants "we" in tutorials and Microsoft wants "you" elsewhere, so each page type follows its own convention [S4][MS1][GS1].
- Use the active voice. Start instructions with the verb. Cut "you can" [GS1][MS2].
- Task headings start with a bare verb ("Add a source"). Concept and reference headings are nouns. All headings use sentence case [GS2][MS3][G5].
- Use numbered steps, one action each. Say where before saying what. State the result after the action [GS3][MS4].
- Put prerequisites before the steps, with role and scope first [GS3][G3][T4].
- Code blocks work as pasted. Introduce each one with a sentence. Mark omissions with a comment, not "…" [T2][GS4][AC3].
- Never write "simply", "just" or "easily" [DJ2][PH1].
- Link to the reference instead of repeating it, and give every page its own context so it works without the others [DJ2][EP1][EP2].
- Show the version a feature arrived in only where it matters, as a "Since v0.4.0" note on that entry. Never put a version in a section heading [DJ2][RS8].

## 9. Sources

Every URL was fetched or probed between 2026-10-30 and 2026-10-31. Tier P means a primary source: the project's own docs, a standard, or a peer-reviewed study. Tier S means a secondary source: an independent write-up or a forum post.

**Frameworks and templates**
- [S1] P https://diataxis.fr/compass/
- [S2] P https://diataxis.fr/map/
- [S3] P https://docs.divio.com/documentation-system/
- [S4] P https://diataxis.fr/tutorials/
- [S5] P https://diataxis.fr/how-to-guides/
- [S6] P https://diataxis.fr/reference/
- [S7] P https://diataxis.fr/explanation/
- [S8] P https://diataxis.fr/how-to-use-diataxis/
- [S9] P https://github.com/evildmp/diataxis-documentation-framework/blob/de7a2d813e2fda339b959e3075afb4e2f09bf54c/complex-hierarchies.rst (archived 2023 version; removed from the live site in 2026)
- [T1] P https://www.thegooddocsproject.dev/template
- [T2] P https://www.thegooddocsproject.dev/template/tutorial
- [T3] P https://www.thegooddocsproject.dev/template/quickstart
- [T4] P https://www.thegooddocsproject.dev/template/how-to
- [T5] P https://www.thegooddocsproject.dev/template/reference
- [T6] P https://www.thegooddocsproject.dev/template/concept
- [T7] P https://www.thegooddocsproject.dev/template/troubleshooting
- [T8] P https://gitlab.com/tgdp/templates/-/raw/main/quickstart/guide_quickstart.md
- [T9] P https://gitlab.com/tgdp/templates/-/raw/main/api-reference/guide_api-reference.md
- [G1] P https://docs.gitlab.com/development/documentation/topic_types/
- [G2] P https://docs.gitlab.com/development/documentation/topic_types/tutorial/
- [G3] P https://docs.gitlab.com/development/documentation/topic_types/task/
- [G4] P https://docs.gitlab.com/development/documentation/topic_types/reference/
- [G5] P https://docs.gitlab.com/development/documentation/topic_types/concept/
- [G6] P https://docs.gitlab.com/development/documentation/topic_types/troubleshooting/
- [GL0] P https://docs.gitlab.com/
- [OG1] P https://trac.osgeo.org/osgeolive/wiki/How%20to%20document%20the%20quickstart%20file?version=10
- [WTD1] P https://www.writethedocs.org/guide/docs-as-code/

**Style guides**
- [GS1] P https://developers.google.com/style/highlights
- [GS2] P https://developers.google.com/style/headings
- [GS3] P https://developers.google.com/style/procedures
- [GS4] P https://developers.google.com/style/code-samples
- [MS1] P https://learn.microsoft.com/en-us/style-guide/grammar/person
- [MS2] P https://learn.microsoft.com/en-us/style-guide/top-10-tips-style-voice
- [MS3] P https://learn.microsoft.com/en-us/style-guide/scannable-content/headings
- [MS4] P https://learn.microsoft.com/en-us/style-guide/procedures-instructions/writing-step-by-step-instructions
- [EP1] S https://everypageispageone.com/the-book
- [EP2] S https://blog.adobe.com/en/publish/2013/10/10/7-principles-for-designing-help-topics-in-the-age-of-the-web

**Projects**
- [ST1] S https://www.mintlify.com/blog/stripe-docs
- [ST2] S https://slab.com/blog/stripe-writing-culture
- [ST3] S https://review.firstround.com/podcast/from-kickoffs-to-retros-and-slack-channels-stripes-documentation-best-practices-with-brie-wolfson/
- [ST6] P https://docs.stripe.com/quickstarts
- [ST7] P https://docs.stripe.com/building-with-llms
- [ST8] P https://docs.stripe.com/llms.txt
- [ST9] P https://docs.stripe.com/api/errors
- [ST10] S https://draft.dev/learn/12-documentation-examples-every-developer-tool-can-learn-from
- [ST11] S https://treblle.com/blog/best-api-documentation-examples
- [ST12] S https://www.moesif.com/blog/best-practices/api-product-management/the-stripe-developer-experience-and-docs-teardown/
- [SB1] P https://supabase.com/docs
- [SB2] P https://supabase.com/llms.txt
- [SB3] P https://supabase.com/blog/new-supabase-docs-built-with-nextjs
- [SB4] P https://github.com/supabase/supabase/blob/master/apps/docs/DEVELOPERS.md
- [SB5] P https://supabase.com/docs/guides/getting-started/mcp
- [SB6] P https://supabase.com/docs/guides/self-hosting/docker
- [SB7] S https://hackceleration.com/supabase-review/
- [SB8] S https://g2.com/products/supabase-supabase/reviews?page=3
- [TW1] P https://tailwindcss.com/docs/padding
- [TW2] P https://tailwindcss.com/docs/installation/using-vite
- [TW3] S https://developerexperience.buzzsprout.com/1806381/episodes/10091539-documentation-is-a-product-adam-wathan-tailwind-css
- [TW4] S https://medium.com/@nicholasfagner/tailwind-css-a-review-c6052fca94b7
- [TW5] S https://thenewstack.io/tailwind-creator-says-ai-played-a-role-in-downsizing/
- [DJ1] P https://docs.djangoproject.com/en/stable/
- [DJ2] P https://docs.djangoproject.com/en/dev/internals/contributing/writing-documentation/
- [DJ3] S https://jacobian.org/2009/nov/10/what-to-write/
- [DJ4] S https://news.ycombinator.com/item?id=1740429
- [DJ5] P https://github.com/django/deps
- [DJ6] S https://hackers.pub/@freakboy3742@cloudisland.nz/019befac-12f0-74ce-b384-a1e57ab96c5d/quotes
- [RS1] P https://blog.rust-lang.org/2025/02/13/2024-State-Of-Rust-Survey-results/
- [RS2] S https://bitfieldconsulting.com/posts/best-rust-books
- [RS3] P https://doc.rust-lang.org/
- [RS4] P https://doc.rust-lang.org/book/ch00-00-introduction.html
- [RS5] P https://rustc-dev-guide.rust-lang.org/about-this-guide.html
- [RS6] P https://forge.rust-lang.org/
- [RS7] P https://doc.rust-lang.org/rustdoc/write-documentation/documentation-tests.html
- [RS8] P https://rustc-dev-guide.rust-lang.org/contributing-to-guide.html
- [VC1] P https://vercel.com/docs/ai-tooling/markdown-access
- [VC3] P https://nextjs.org/docs/app/guides/ai-agents
- [VC4] P https://nextjs.org/docs
- [VC5] P https://github.com/vercel/next.js/pull/89973
- [PH1] P https://posthog.com/handbook/docs-and-wizard/docs-style-guide
- [PH2] P https://posthog.com/handbook/wizard-and-docs.md
- [PH3] P https://posthog.com/llms.txt
- [PH4] P https://posthog.com/docs/self-host
- [PH5] P https://posthog.com/docs/self-host/configure/environment-variables
- [GF1] P https://grafana.com/docs/grafana/latest/setup-grafana/configure-grafana/
- [GF2] P https://grafana.com/docs/grafana/latest/datasources/postgres/configure/
- [MM1] P https://docs.mattermost.com/
- [MM2] P https://docs.mattermost.com/administration-guide/configure/environment-configuration-settings.html
- [SE1] P https://develop.sentry.dev/self-hosted/
- [AB1] P https://docs.airbyte.com/platform/contributing-to-airbyte/resources/qa-checks
- [HA1] P https://contributors.home-assistant.io/writing/reference/integration-page/
- [HA2] P https://contributors.home-assistant.io/core/quality-scale/rules/
- [K1] P https://github.com/kubernetes/community
- [K2] P https://github.com/kubernetes/enhancements
- [K3] P https://github.com/kubernetes/design-proposals-archive
- [K4] P https://kubernetes.io/docs/contribute/new-content/new-features/
- [NP1] P https://numpy.org/neps/nep-0044-restructuring-numpy-docs.html
- [CN1] P https://ubuntu.com/blog/diataxis-a-new-foundation-for-canonical-documentation
- [GB1] P https://www.gatsbyjs.com/contributing/docs-contributions/docs-structure/
- [CF1] P https://blog.cloudflare.com/new-and-improved-workers-docs/
- [JK1] P https://alpha.docs.jenkins.io/dev-docs/tutorial-improve/migrate-documentation-to-github.html

**Wikis, READMEs, agents, tooling**
- [GH1] P https://docs.github.com/en/communities/documenting-your-project-with-wikis/about-wikis
- [GH2] P https://github.com/orgs/community/discussions/4992
- [GH3] P https://docs.github.com/en/communities/documenting-your-project-with-wikis/adding-or-editing-wiki-pages
- [GH4] P https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-readmes
- [WK1] P `gh api repos/<owner>/<repo>` `has_wiki`, checked for django/django, rust-lang/rust, supabase/supabase, tailwindlabs/tailwindcss, kubernetes/kubernetes, grafana/grafana, PostHog/posthog, home-assistant/core, qdrant/qdrant, langchain-ai/langchain, neovim/neovim, microsoft/vscode, dotnet/aspnetcore, open-webui/open-webui
- [WK2] P https://github.com/neovim/neovim/wiki (a stub pointing to neovim.io/doc)
- [WK3] S https://fabmob.readthedocs.io/en/latest/contribute_to_the_doc/migration_reason/
- [WK4] P https://github.com/grafana/grafana/blob/main/docs/README.md
- [WK5] S https://www.eweek.com/news/tailwind-labs-lays-off-engineers-due-to-ai/
- [OG2] P https://opensource.guide/starting-a-project/
- [MR1] S https://www.makeareadme.com/
- [LT1] P https://llmstxt.org/
- [AG1] P https://agents.md/
- [AG2] P https://github.blog/changelog/2025-08-28-copilot-coding-agent-now-supports-agents-md-custom-instructions/
- [AD1] P https://www.cognitect.com/blog/2011/11/15/documenting-architecture-decisions
- [AD2] P https://github.com/npryce/adr-tools
- [LY1] P https://github.com/lycheeverse/lychee
- [RD1] P https://redocly.com/docs/realm/content/api-docs/openapi-extensions/x-code-samples
- [SC1] P https://scalar.com/products/api-client/code-generation
- [RFC1] P https://datatracker.ietf.org/doc/html/rfc9457

**Studies of how developers use docs**
- [AC1] P Robillard & DeLine 2011, "A field study of API learning obstacles", https://www.cs.mcgill.ca/~martin/papers/ese2011.pdf
- [AC3] P Meng et al. 2019, SIGDOC, https://sigdoc.acm.org/?p=1425
- [AC4] P Brandt et al., CHI 2009, https://hci.stanford.edu/publications/2009/webUseStudy/brandt_chi09_webuse.pdf
- [AC5] P Sohan et al., VL/HCC 2017, https://www.cs.mcgill.ca/~martin/papers/vlhcc2017.pdf
- [NN1] P https://www.nngroup.com/articles/flat-vs-deep-hierarchy/

### Conflicts and thin evidence

- **Top level by page type or by journey.** Diátaxis's four boxes conflict with Meng et al.'s finding that a split by content type confused developers [AC3]. R2 takes the middle path: journeys at the top, with typed pages inside. Diátaxis's archived hierarchy page allows this [S9], but that page has one source since its 2026 removal.
- **Continuous narrative or standalone pages.** Robillard found newcomers want a continuous narrative at first [AC1], which pulls against Every Page is Page One [EP1]. The quickstart and tutorial are linear. Everything else stands alone.
- **Quickstart length.** No study sets a target. Good Docs allows up to two hours, while vendor blogs target under 30 minutes. So the plan sets a step budget (5–8 steps) instead of a time.
- **Stripe's three-column API reference** rests on one teardown [ST12], so the plan doesn't rely on it.
- **GitHub's own README docs** still say longer docs suit wikis [GH4]. That conflicts with GitHub's indexing rule for wikis [GH1].
- **Thin praise.** Praise for Supabase and PostHog comes mostly from reviews and self-assessment. Neither project is load-bearing for any recommendation.
