# How do other products keep their REST API, MCP server, CLI and web UI consistent?
**Date:** 2026-10-17
**Status:** Reviewed
**Built on:** programmatic-source-configuration-safety-2026-08-17.md (its MCP and connection-creation conclusions constrain the recommendation here); the 2026-10-17 Connapse surface map (artifact https://claude.ai/artifact/3xzn46jzukEo18Uw9dbihX)

## Executive summary

Products that keep their surfaces consistent do it by making every capability an operation in one described API, then deriving or testing everything else against it. Kubernetes, Vault, Grafana, Linear and Umbraco 14 run their own UI on the same API that customers use. Stripe, Cloudflare and Azure generate SDKs and CLIs from a machine-readable spec. Jellyfin and Azure enforce this in CI with a committed spec and a breaking-change diff. On MCP, the largest vendors moved away from one tool per endpoint. Stripe, Cloudflare and Stainless expose the whole API through two to four generic search/read/write or code-execution tools, which stay in sync with the API automatically. For Connapse, the Blazor Server UI should call the same operation layer as REST rather than Connapse's own HTTP API, which Microsoft recommends against for server-side components. A build-time OpenAPI document becomes the contract that the CLI and admin MCP tools are derived from. The main open question is how far configuration changes should reach MCP at all, given the August 2026 finding that a RAG server puts untrusted text into the model's context by construction.

## Research brief

**Question:** How do companies keep their REST API, MCP server, CLI and web UI consistent and current with each other, and which of those patterns fit Connapse?

**Sub-questions investigated:**
1. Spec-first code generation: what is the source of truth, what is generated, and what stays hand-written?
2. UI dogfooding and hand-written CLIs: who builds their UI on the public API, and how do separate CLIs keep up?
3. MCP server design relative to the API: curated tools, generic tools, or one tool per endpoint, and how are admin operations gated?
4. Enforcement: CI tooling that detects drift, and how it maps to .NET 10 and the C# MCP SDK.

**Out of scope:** SDK publishing for languages Connapse doesn't ship; GraphQL migration; pricing of commercial generators.

**Success criteria:** Concrete, sourced patterns per product, their trade-offs, and a recommendation for Connapse.

## Findings by sub-question

### 1. Spec-first generation (Stripe, Cloudflare, Azure, Stainless, Speakeasy, Google)

**Claim: every mature pipeline treats a machine-readable API description as the source of truth and generates the per-endpoint layer, while workflows, auth helpers and output shaping stay hand-written.**

- **Stripe:** publishes an OpenAPI spec, itself generated from internal definitions, for SDK generation. The Stripe CLI's `stripe resources` commands are auto-generated namespaces over API operations. No source confirms that they come from the public spec. [primary] https://github.com/stripe/openapi, https://github.com/stripe/stripe-cli/wiki/resource-commands
- **Cloudflare:** made OpenAPI the source of truth in 2022. Since 2024 its SDKs, Terraform provider and API reference are generated. Generation forced schema fixes: teams had to annotate defaults and server-computed fields, CI lint rules were added, and schema quality improved "by an order of magnitude." [primary] https://blog.cloudflare.com/automatically-generating-cloudflares-terraform-provider/
  - Cloudflare's new `cf` CLI (launched 28 Sep 2026) generates commands for 3,000+ operations from the same schema. It replaces about 280 hand-built Wrangler commands whose verbs were inconsistent (`d1 info`, `hyperdrive get`, `workflows describe`). Output is JSON by default. [primary] https://blog.cloudflare.com/cloudflare-cf-cli-launch/
- **Azure:** TypeSpec is the source, with emitters producing OpenAPI and clients; the core is GA, the language emitters are in preview. The az CLI generates one "atomic" command per API with `aaz-dev`. Hand-written code lives in `custom.py` and overrides hooks for validation, output reshaping and long-running operations. [primary] https://learn.microsoft.com/en-us/azure/developer/typespec/overview, https://azure.github.io/aaz-dev-tools/pages/usage/customization/
- **Stainless and Speakeasy:** both generate CLIs and MCP servers from OpenAPI. Speakeasy maps one tool per operation, with overlays to rename or disable tools. Stainless's MCP server has two tools, code execution against the SDK and docs search. Speakeasy's CLI generation is alpha and enterprise-only. [primary, vendor] https://www.stainless.com/docs/mcp, https://www.speakeasy.com/docs/cli-generation/create-cli
- **Google:** the `gws` Workspace CLI builds its command tree at runtime from Discovery documents and adds hand-written `+` helpers. It is not an officially supported product. [primary] https://github.com/googleworkspace/cli

Generation only covers what is in the spec. A capability that exists only inside a UI page is invisible to every one of these pipelines.

### 2. UI dogfooding and hand-written CLIs (Kubernetes, Vault, Grafana, Linear, GitLab, GitHub, Supabase, Sentry)

**Claim: the products with the least drift run their UI and CLI as ordinary clients of one API. Strict dogfooding is rare, though: most keep some internal-only endpoints and say so explicitly.**

- **Kubernetes:** every component talks through the API server, and kubectl is just a client. Version drift is bounded by a skew policy: kubectl is supported within one minor version of the server. [primary] https://kubernetes.io/releases/version-skew-policy/
- **HashiCorp Vault:** the CLI uses the HTTP API "similar to all other consumers." The UI, CLI and Go client are in one repo and ship in the server binary, so they release together. Some features are still API-only. [primary] https://developer.hashicorp.com/vault/api-docs
- **Grafana:** the HTTP API is "used by the Grafana frontend." The cost is that every UI endpoint becomes a public contract, and Grafana is now migrating legacy `/api` routes to a versioned `/apis`. [primary] https://grafana.com/docs/grafana-cloud/developer-resources/api-reference/http-api/
- **Linear:** the public GraphQL API is "the same API we use internally." [primary] https://developers.linear.app/docs/graphql/working-with-the-graphql-api
- **GitLab:** its style guide says REST and GraphQL should "share implementations as much as possible." [primary] https://docs.gitlab.com/ee/development/api_graphql_styleguide.html
- **Separate-repo CLIs:** gh, glab and the Supabase CLI are hand-written and release independently, often every two weeks. gh, glab and Vercel all ship a raw `api` command so users aren't blocked when the CLI lags the server. gh still has open issues blocked on missing API support. [primary] https://cli.github.com/manual/gh_api, https://github.com/cli/cli/issues/10620
- **Internal-only endpoints:** Supabase Studio calls both the public Management API and internal `/platform/...` routes, from three generated specs. Sentry tags every endpoint PUBLIC, EXPERIMENTAL or PRIVATE, and private ones "control UI features" with no stability promise. [primary] https://github.com/supabase/supabase/tree/master/packages/api-types/types, https://develop.sentry.dev/backend/api/public/
- **Umbraco 14** (.NET): its backoffice was rebuilt to talk to the server only through the OpenAPI-described Management API, with a generated TypeScript client. Extensions get the same API. [primary] https://docs.umbraco.com/umbraco-cms/develop-with-umbraco/headless-and-apis/management-api

Vault, Kubernetes, Umbraco and Grafana all have browser-side UIs, which need an HTTP API anyway. Connapse's Blazor Server components run on the server, so the honest equivalent is a shared operation layer rather than HTTP calls (see sub-question 4).

### 3. MCP server design relative to the API (Stripe, Cloudflare, GitHub, Notion, Supabase, Sentry, Linear)

**Claim: large vendors have converged on a few generic tools over the API, or on curated task-shaped tools. Pure one-tool-per-endpoint servers are treated as an anti-pattern beyond a few dozen tools. Admin operations, where exposed, always sit behind scopes, read-only switches or human approval.**

- **Stripe:** about 15 tools. Four generic ones (`stripe_api_search`, `stripe_api_details`, `stripe_api_read`, `stripe_api_write`) cover an allow-list of about 150 methods, including admin config such as webhook endpoints and tax settings. Risky writes like refunds need a human to approve on a web page, which issues a token the agent then retries with. [primary] https://docs.stripe.com/mcp
- **Cloudflare:** first tried hand-curated per-product servers and found that "no collection of hand-maintained servers could keep up." Code Mode now exposes 2,500+ endpoints through `search()`, which runs code over the OpenAPI spec, and `execute()`, in a V8 sandbox. That costs about 1,000 tokens, against roughly 1.17M for one tool per endpoint. [primary] https://blog.cloudflare.com/code-mode-mcp/
- **GitHub:** merging per-endpoint tools into method-parameter tools (`issue_read`) with aliases for the old names. Toolsets can be switched on or off by flag or header, and read-only mode drops write tools. Tools are hidden when a classic token lacks the scope. [primary] https://github.com/github/github-mcp-server
- **Notion:** deliberately rewrote tools "AI-first" instead of wrapping its endpoints, and returns Markdown. [primary] https://www.notion.com/blog/notions-hosted-mcp-server-an-inside-look
- **Supabase:** exposes admin operations (projects, branches, secrets) behind `features=`, `read_only=true` and `project_ref=`. Destructive SQL triggers a confirmation prompt, which Supabase calls a guardrail, not a guarantee. **Sentry** defaults to read-only "skills"; **Linear** offers a separate `/mcp/readonly` URL. [primary] https://supabase.com/docs/guides/getting-started/mcp, https://github.com/getsentry/sentry-mcp, https://linear.app/docs/mcp
- **Anthropic guidance:** build fewer tools for high-impact workflows instead of wrapping endpoints, and develop them against evals. Deferred tool loading raised internal MCP eval accuracy from 49% to 74% on Opus 4. [primary] https://www.anthropic.com/engineering/writing-tools-for-agents, https://anthropic.com/engineering/advanced-tool-use
- **Accuracy as tool count grows:** going from 10 to 100 tools cost about 9–10 points of retrieval hit rate in an academic benchmark. [secondary] https://arxiv.org/html/2602.23367v1
  - Vendor figures put the safe zone at roughly 25–50 tools. [secondary/tertiary] These numbers are directional only.
- **MCP spec 2026-07-28:** removes sessions, signals tool-list changes through `subscriptions/listen`, and adds caching hints to `tools/list`. Annotations such as `destructiveHint` remain untrusted hints, not enforcement. [primary] https://modelcontextprotocol.io/specification/2026-07-28/changelog

### 4. Enforcement and the .NET 10 toolchain

**Claim: the standard drift control is a build-generated spec committed to the repo, a CI breaking-change diff, and a coverage gate. .NET 10 supports all three first-party, and Connapse uses none of them today.**

- **Committed spec plus PR diff:** Jellyfin, a .NET server, generates its spec at the PR head and the merge-base on every PR and runs openapi-diff, which fails on errors. `@jellyfin/sdk` is regenerated each server release. [primary] https://github.com/jellyfin/jellyfin/blob/master/.github/workflows/openapi-pull-request.yml
- **Breaking-change and lint tools:** oasdiff checks for 100+ kinds of breaking change and can fail CI. Pin its GitHub Action at 0.0.51 or later because of CVE-2026-53507 (SSRF via external `$ref`). Azure runs a Spectral ruleset and LintDiff in its spec repo. [primary/secondary] https://github.com/Tufin/oasdiff, https://github.com/Azure/azure-api-style-guide
- **Coverage gate:** Kubernetes' APISnoop fails CI when a new conformance-eligible endpoint has no test. [primary] https://github.com/cncf/apisnoop
- **Build-time OpenAPI in .NET 10:** Microsoft.AspNetCore.OpenApi plus Microsoft.Extensions.ApiDescription.Server writes an OpenAPI 3.1 document during `dotnet build`. Generation runs the app's entry point against a mock server, so Connapse's startup migrations and hosted services must be skipped when the entry assembly is `GetDocument.Insider`. [primary] https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0
- **Client and CLI generation:** Kiota generates C# clients from OpenAPI. With `--language CLI` it generates System.CommandLine commands, which is how the Microsoft Graph CLI is built, but that mode depends on a System.CommandLine beta and its runtime is still preview. [primary] https://learn.microsoft.com/en-us/openapi/kiota/tutorials/cli-azure
- **Endpoints as MCP tools:** the official C# MCP SDK has no way to turn endpoints into tools. Community packages (MCPify 0.0.12, ZeroMCP.net) are unvetted. Connapse pins ModelContextProtocol.AspNetCore 1.0.0; NuGet lists 2.1.0. [primary] https://www.nuget.org/packages/ModelContextProtocol.AspNetCore/2.1.0
- **Shared layer in Blazor Server:** Microsoft calls an HttpClient back into the same server "not recommended" for server-side components. It recommends a service interface with a server implementation instead. [primary] https://learn.microsoft.com/en-us/aspnet/core/blazor/call-web-api

What Connapse has today: no OpenAPI package, no committed spec, `WithToolsFromAssembly()` for 12 hand-written tools, and a CLI client written by hand.

## Recommendation for Connapse

**Claim: adopt the "one described API" model in the Blazor Server form. The UI and REST call the same operations, the build emits OpenAPI, and CI fails when an operation is missing from a surface.**

1. **Operations layer.** Move each UI action into an operation method that owns validation and auditing. Blazor and the REST endpoints both call it; Blazor does not go over HTTP (Microsoft's guidance). This is GitLab's "share implementations" rule and gives the effect of Vault's single API without the network hop.
2. **OpenAPI as the contract.** Generate `openapi.json` at build time, commit it, and run oasdiff in CI. Tag each operation public, experimental or internal, as Sentry does, so a UI-only action is declared rather than accidental.
3. **Parity test.** Enumerate the operations and assert that each public one has a route. The cheap .NET version reads `EndpointDataSource` and the `[McpServerTool]` methods; no published example exists, so this is an inference.
4. **CLI.**
   - Add `connapse api <method> <path>` now; gh, glab and Vercel all use this escape hatch for lag.
   - Generate the CLI's C# client with Kiota from the published spec, and keep hand-written commands on top.
   - Publish a Kubernetes-style skew policy (CLI supported within one minor version of the server).
   - Kiota's CLI-generation mode is still preview, so don't use it yet.
5. **MCP.**
   - Keep the curated knowledge tools; that matches Notion and Anthropic's guidance.
   - Admin is the open decision. Any admin tools should be Stripe-style generic tools (`connapse_api_search`, `connapse_api_read`, `connapse_api_write`) over an allow-list of spec operations, so MCP never needs hand-syncing.
   - Gate them separately (see the conflict below).
   - Never generate one tool per endpoint.

## Design decision: admin over MCP with scoped tokens (2026-10-18)

**Claim: admin configuration can go on MCP if three things hold. The token's scopes decide which tools exist, every call is checked server-side, and actions that create credentials or delete things need a human approval that the model cannot forge.**

- **Scopes decide which tools are listed.** When a user asks for an MCP token, they pick its scopes, and `tools/list` shows only the tools those scopes allow. A knowledge-only token never sees admin tools, so it can't be talked into calling them. GitHub's MCP server does this for classic tokens. It also shrinks the tool list, which improves tool selection.
- **Every call is checked too.** Hiding a tool is not enforcement, because a client can call a tool by name. Today `/mcp` has one gate (`RequireAgent`), and the `Scope:*` policies are defined but unused. Per-tool checks are the prerequisite for everything else here.
- **A user's request is not approval.** The server sees only the model's tool call. A call the user asked for and a call a document injected look identical. An `approved: true` argument is model output, which injected text can forge. A request also doesn't protect against the model misreading what the user meant.
- **Approval comes through a channel the model can't write to.** Low-impact actions run on scope alone: reading admin state, triggering a sync, renaming a source, changing its interval. Creating a connection, saving provider credentials, and deleting anything return a pending request with a link. The user approves it in the Connapse UI, as Stripe does for refunds. MCP elicitation is a nicer in-client version where clients support it, but Connapse can't verify that a person saw it.
- **Approval is a setting chosen when the token is minted.** It's on by default for MCP tokens and can be turned off for a PAT used from the CLI. That matches how agents use `aws` and `az`: IAM is the scope, and the human approval is the client's own permission prompt before each command. With approval off, Connapse trusts the client to ask the person. With it on, Connapse asks the person itself. The token-creation page should say which one is in effect.
- **Effect on the August 2026 report.** This replaces its "nothing that changes configuration on MCP" rule with scoped tools plus verifiable approval. It still leaves knowledge-only tokens with no admin reach at all, which was that report's main concern.

## Conflicts and uncertainties

- **This run vs. programmatic-source-configuration-safety-2026-08-17.md.** That report said connection creation should stay off the API entirely and that nothing mutating configuration should go on MCP. Its reasons were the lethal trifecta, because a RAG server feeds attacker-influenceable document text to the model, plus the GitHub, Atlassian and Supabase MCP injection incidents.
  - This run found that Stripe, Supabase, Cloudflare and Vercel do expose admin configuration over MCP, but always behind scopes, read-only switches or out-of-band human approval. The two vendors hit by injection (GitHub, Supabase) responded with read-only modes, not by removing admin tools.
  - Connapse has since added Atlassian site creation over REST (`POST /api/v1/atlassian/sites`), which diverges from the August recommendation.
  - **Reconciled position:**
    - Read-only admin introspection over MCP (sync status, errors, setup state) is low-risk and high-value.
    - Triggering a sync of an existing source is a small mutation.
    - Creating anything that holds or names a credential (connections, provider credentials) should stay off MCP, or require Stripe-style human approval outside the agent.
    - Over REST, connection creation should be an Owner-tier, separately scoped grant, not reachable by agent tokens.
  - Decided 2026-10-18: admin goes on MCP behind scoped tokens and verifiable approval. See "Design decision" above.
- **Cloudflare's token figures are inconsistent.** Its own blog gives both 1.17M and "over 2M" tokens for one tool per endpoint.
- **Single-source claims:**
  - the oasdiff CVE (secondary)
  - Bitwarden's monorepo layout (tertiary, omitted from the recommendation)
  - GitHub MCP's tool count of about 92 (tertiary)
  - whether the Stripe CLI is generated from the public spec (unconfirmed)
- **Tool-count accuracy thresholds** come mostly from vendor blogs and depend heavily on the model and the tool descriptions.
- **MCP spec 2026-07-28 removes sessions.** Per-connection dynamic toolsets like GitHub's `enable_toolset` may need rework. This is an inference, not verified.

## Gaps — what we did not find

- No published .NET example of a parity test across routes, MCP tools and CLI commands.
- No primary source on whether github.com's web UI uses private endpoints.
- No measurement of the latency cost of a Blazor Server component calling its own REST API.
- No first-party way in the C# MCP SDK to derive tools from endpoints or OpenAPI.
- Fern's CLI and MCP generators couldn't be examined; the page was truncated.
- No source on how gcloud is generated.

## Source quality assessment

Most load-bearing claims rest on primary sources: vendor docs, engineering blogs, the MCP spec, and repositories checked in source for Vault, gh and Supabase Studio. Tool-count accuracy numbers are secondary or tertiary and are used only directionally. The prior August report was itself mostly primary and secondary. Its MCP incident list is secondary or tertiary, which weakens, but does not overturn, its "nothing mutating on MCP" conclusion.

## Sources

**Primary:**
- stripe/openapi; docs.stripe.com/mcp; stripe-cli wiki
- Cloudflare blogs: Terraform generation, cf CLI, Code Mode, Code Mode MCP
- Azure TypeSpec docs; aaz-dev-tools; Azure API style guide
- stainless.com/docs/mcp; Speakeasy CLI and MCP docs
- googleworkspace/cli; kubernetes.io skew policy; developer.hashicorp.com/vault/api-docs; hashicorp/vault repo
- Grafana HTTP API docs; Linear GraphQL docs; GitLab GraphQL style guide
- cli/cli repo, gh api manual, issues #10620 and #11727
- supabase/supabase api-types; Supabase MCP docs; develop.sentry.dev; getsentry/sentry-mcp; linear.app/docs/mcp
- github/github-mcp-server; Notion MCP blog
- Anthropic "Writing effective tools for agents" and "Advanced tool use"
- MCP spec 2026-07-28 changelog
- Jellyfin openapi workflow; Tufin/oasdiff; cncf/apisnoop
- Microsoft Learn: ASP.NET Core OpenAPI, Kiota CLI, Blazor call-web-api
- Umbraco Management API docs; NuGet ModelContextProtocol.AspNetCore 2.1.0

**Secondary:**
- arXiv 2602.23367 (HumanMCP); arXiv 2507.16044 (AutoMCP)
- Maxim tool-accuracy blog; OpenCVE CVE-2026-53507; meziantou.net Kiota build-time post
- Speakeasy "lessons from 50 production servers"

**Tertiary:**
- truto.one tool-count blog; alphasignal RAG-MCP summary
- instagit GitHub dynamic toolsets; mintlify Bitwarden mirror; ZeroMCP.net README
