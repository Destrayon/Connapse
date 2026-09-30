# What does Connapse need to build an Atlassian provider (Confluence Cloud + Jira Cloud)?
**Date:** 2026-09-30
**Status:** Reviewed (inline adversarial pass)
**Built on:** next-connectors-rag-shape-graphrag-2026-09-09.md, github-connector-implementation-2026-09-09.md

## Executive summary

The Atlassian provider for epic #540 is buildable on Connapse's existing source and sync engine. The work concentrates in five places. The first is an admin-level Atlassian credential, ideally a service account. The second is a full-listing sync per Confluence space and Jira project, because neither product offers a plain REST client a deletion feed or webhooks. The third is one storage-XHTML converter for Confluence plus one Atlassian Document Format (ADF, Atlassian's JSON rich-text format) renderer for Jira; no .NET library exists for either. The fourth is a per-hit permission verifier. Connapse currently allows only one such verifier (Azure's), so it has to become a composite. The fifth is a way to link each Connapse user to their Atlassian accountId.

The live permission-check endpoints named in the epic are confirmed and not deprecated. The research also found three new fail-open traps:
- Jira's bulk check silently evaluates the service account when `accountId` is missing.
- Jira comments can be restricted to a role or group, and no API can check that restriction for another user.
- Confluence's rendered formats inline included pages as seen by the service account.

Three uncertainties must be settled on a live test site before the spec is final:
- whether a service account's "App admin" role grants the Confluence Administrator and Administer Jira permissions that the permission checks require;
- whether service-account OAuth traffic counts against the shared 65,000-points-per-hour app quota;
- how account linking works when each self-hosted install must register its own Atlassian OAuth app.

## Research brief

**Question:** What is every external API and internal Connapse seam an Atlassian provider (one Cloud site connection, a Confluence source and a Jira source, per-user live filtering) needs, and what are the known traps in each?

**Sub-questions:**
1. Authentication: which credential a self-hosted server should use, and what setup must ask for.
2. Confluence: enumeration, incremental sync, deletion detection, and content rendering.
3. Jira: enumeration, incremental sync, deletion detection, ADF rendering, and comment visibility.
4. Per-user permission checks, identity mapping, and rate limits.
5. Connapse internals: which seams the provider plugs into, what it can reuse, and what must be generalized.

**Out of scope:** Confluence and Jira Data Center, write-back of any kind (Connapse never writes grants), GraphRAG edge extraction beyond noting which links are available, and pricing.

**Success criteria:** A map detailed enough to brainstorm a spec and phased plan from, with conflicts and open questions listed explicitly.

## Findings by sub-question

### 1. Authentication: an admin service account, not a person's API token

This section claims that the Atlassian provider should authenticate as an Atlassian service account with admin rights in both products, and that 3LO, Forge and Connect are poor fits for the site connection.

- **Checking another user's permission requires admin rights.** The Confluence check needs Confluence Administrator and the Jira check needs Administer Jira [primary: Confluence v1 content-permissions reference; Jira v3 OpenAPI spec]. The connection credential must therefore belong to an admin. Only Connect apps are exempt, and Connect reaches end of support in 2026–2027.
- **Classic API tokens (email plus token over Basic auth)** carry the full rights of a human account. Tokens created after 2024-12-15 expire within 1 to 365 days. Every token created before that date expired between March and May 2026. Unscoped tokens are labelled deprecated, but no end date has been published [primary: Atlassian account token docs; tertiary for the deprecation label]. They also tie the integration to a person and a paid seat.
- **Scoped API tokens** only work through `https://api.atlassian.com/ex/{jira|confluence}/{cloudId}`. The granular scopes include `read:content.permission:confluence` and `read:permission:jira` for the checks [primary: official OpenAPI specs]. The granular scopes are marked Beta. The Jira spec's classic-scope list for `permissions/check` is empty, while its description text names `read:jira-work`.
- **Service accounts** are non-human accounts that don't consume a user seat. Every plan gets five free; more require Atlassian Guard [primary: support.atlassian.com, "Understand service accounts"]. A service account can hold either kind of credential:
  - scoped API tokens, sent as a Bearer header, expiring within 365 days, with scopes fixed at creation;
  - an OAuth 2.0 client-credentials credential: POST `auth.atlassian.com/oauth/token` with `grant_type=client_credentials`, which returns a one-hour access token with no user involved [primary].

  Whether a service account can be given "App admin" in Jira and Confluence, and whether that equals the global admin permission the checks need, comes only from a vendor guide seen as a search snippet [secondary, single source]. **This is load-bearing; verify it on the test site.**
- **OAuth 2.0 authorization code flow (3LO, "three-legged": user, app and Atlassian)** acts as the consenting user. It needs a developer-console app with a fixed callback URL, and "sharing" enabled before other orgs can use it. Refresh tokens rotate and expire after 90 days idle [primary]. For the site connection this is a poor fit, since every self-hosted install would need its own app. It remains relevant for account linking (section 4).
- **Forge and Connect.** Connect stops receiving updates on 2026-03-31. Forge runs on Atlassian's infrastructure and is installed per site. Called as the app itself, the Confluence check returns false for everything [tertiary, community thread].
- **Discovery and validation:**
  - `GET https://{site}.atlassian.net/_edge/tenant_info` returns the cloudId without authentication [primary: Atlassian KB].
  - Jira `GET /rest/api/3/myself` and Confluence `GET /wiki/rest/api/user/current` validate the credential.
  - Jira `GET /rest/api/3/mypermissions?permissions=ADMINISTER` detects Jira admin.
  - No documented probe detects Confluence admin. The proposed workaround is a permission check with a group subject, where a 403 means not admin.

**Setup implications:** ask for the site URL (derive the cloudId from it), the service-account steps (create it, grant product access and admin, create a credential with the listed scopes), and the credential itself. Test connection runs the probes above. Show a warning as a token approaches expiry.

### 2. Confluence Cloud: list every page in each space, fetch storage bodies, build breadcrumbs locally

This section claims that Confluence sync should list each chosen space's pages in full on every cycle, fetch bodies only for changed pages, and render from storage format.

- **Enumeration (v2 API)** [primary: v2 OpenAPI spec]:
  - `GET /wiki/api/v2/spaces` filters by keys, type and status. Status can be current, archived or trashed; personal spaces are one of the types.
  - `GET /wiki/api/v2/spaces/{id}/pages` returns up to 250 pages per call, paged by a cursor in the `Link` header. Each row carries `parentId`, `parentType`, `version.number`, `subtype` and the title.
  - List endpoints return `storage` or `atlas_doc_format` bodies. Single-page GETs also offer `view`, `export_view` and `anonymous_export_view`.
  - Blog posts have the same shape at `/spaces/{id}/blogposts`.
  - Live docs are pages with `subtype=live`. They keep changing until 15 minutes after editing stops, so expect churn [primary, RFC-83].
  - Whiteboards, databases and embeds have no text body through REST. Folders matter only as parents in the page tree.
- **Incremental sync:**
  - v1 CQL (Confluence Query Language) search with `lastmodified` is not deprecated [primary: v1 spec]. It is minute-granular and uses the configured timezone, and the query cannot name a timezone [primary: CQL fields; secondary: community, CONFCLOUD-76352]. Onyx widens its window by a day to cope.
  - v2 `GET /pages?space-id=…&sort=-modified-date`, read until the watermark, avoids the timezone problem.
- **Deletes, moves and restriction changes:**
  - Confluence Cloud webhooks are available to Connect and Forge apps only, and are best-effort [primary].
  - CQL cannot find trashed content.
  - Therefore reconcile by listing `{id, version, spaceId, parentId, title}` for each space every cycle, without bodies. A page that is missing, trashed or deleted is removed from the index. A page moved to another space leaves space A's listing.
  - Restriction changes do not create a new version. That doesn't matter under live query-time checks.
  - Onyx does the same "slim listing, then prune" [secondary: Onyx confluence connector].
- **Rendering: use storage format and write our own converter.**
  - `export_view` renders as the caller, so include, excerpt-include and Jira macros pull in content the service account can see. That leaks restricted text into less-restricted pages' chunks and duplicates it [primary: ContentRepresentation docs; the leak is our inference].
  - Onyx inlines include macros by title lookup, which has exactly this leak [secondary].
  - The converter maps:
    - `ac:structured-macro`: code becomes a fenced block, info/note/warning/panel become a labelled quote, expand becomes its body, status becomes text, jira becomes the issue key, include and excerpt-include become a placeholder, and toc and children are dropped;
    - `ri:user ri:account-id` becomes a display name;
    - `ac:link` + `ri:page` (title-based, not id-based) becomes link text, and is the future link graph.
  - No .NET converter exists; AngleSharp plus a macro map is the likely path.
- **Breadcrumbs:** build the tree from the full listing's `parentId` and titles, at zero extra calls. `/pages/{id}/ancestors` returns ids only. Folder parents need one cached `GET /folders/{id}`. This feeds the breadcrumb prefix the epic calls for (space › ancestors › heading path).
- **Attachments:**
  - List with `/pages/{id}/attachments` and download with v1 `/content/{id}/child/attachment/{attId}/download`.
  - Worth indexing behind an option with a size cap, since Connapse already parses PDF and Office files. Attachments follow their page's permission.

### 3. Jira Cloud: ids via enhanced JQL search, bulk fetch, ADF rendering, and restricted comments

This section claims that Jira sync should page issue ids through the enhanced JQL endpoint with epoch-millisecond bounds, bulk-fetch full issues, reconcile deletions by listing ids, and handle restricted comments explicitly.

- **Projects:** `GET /rest/api/3/project/search` uses offset paging (100 per page) with `action=browse`, `status=live` and `typeKey` filters. The type is software, business or service_desk (Jira Service Management, JSM) [primary: Jira v3 spec].
- **Search:**
  - The old `/rest/api/3/search` is removed and returns 410 [primary: spec marks it "being removed"; secondary: Adaptavist on the August 2025 removal].
  - Use `GET/POST /rest/api/3/search/jql`:
    - the query must be bounded;
    - paging uses `nextPageToken`, which lasts 7 days, and there is no total count;
    - it returns up to 5,000 results per page when only ids are requested;
    - `fields` defaults to `id`.
  - It is eventually consistent, lagging from seconds to minutes [primary: search-and-reconcile page].
  - Inline comments are capped at 20 per issue [secondary, single source].
  - `POST /rest/api/3/issue/bulkfetch` returns 100 full issues per call [primary].
  - Onyx's pattern: page ids, then bulk-fetch 100 at a time [secondary].
- **Timezone trap:** a quoted JQL date is read in the *credential's profile timezone* [primary: JRACLOUD-74279]. Use unquoted epoch milliseconds, as Onyx does, and overlap the window by at least 5 minutes to cover search lag.
- **Deletions and moves:**
  - JQL never returns deleted issues, and no deleted-issue feed exists.
  - Webhooks need either the admin REST endpoint plus a publicly reachable URL, re-registered every 30 days, or an OAuth or Connect app [primary: Jira webhooks docs].
  - So reconcile by listing ids per project on a schedule.
  - Moved issues keep their numeric `id`, and old keys redirect. **Key records by id, not key.**
- **Comments (permission-relevant):**
  - A comment's `visibility` can restrict it to a group or a project role, and the API returns it only to members [primary].
  - Browse permission on the issue does not imply seeing the comment. `permissions/check` cannot evaluate comments, and `mypermissions?commentId=` works for the caller only.
  - Attachments added in restricted comments inherit the restriction. Worklogs have `visibility` too.
  - `jsdPublic=false` marks a JSM internal note, hidden from portal customers but not group- or role-restricted.
  - Onyx ignores all of this and indexes restricted comments into the issue, which leaks them [secondary].
- **ADF:**
  - The schema is `@atlaskit/adf-schema` (full.json, 43 node types) [primary: ADF structure page]. Confluence's `atlas_doc_format` uses the same schema [tertiary], so one renderer with an unknown-node fallback that emits child text covers both.
  - No .NET ADF converter exists. mcp-atlassian's `models/jira/adf.py` is the best reference [secondary].
  - Prefer ADF over `renderedFields` HTML, which is heavier, caps bulk fetch at 100, and still needs HTML-to-text conversion.
  - Mentions carry `attrs.text` and an accountId. `inlineCard` URLs are cross-system links and should be kept.
- **Record header fields:** summary, status, type, priority, assignee and reporter, labels, components, fixVersions, parent, issuelinks, and created/updated. Discover rich-text custom fields with `GET /rest/api/3/field`. In v3, textarea custom fields hold ADF [primary].
- **Attachments:** `GET /rest/api/3/attachment/content/{id}` [primary].

### 4. Permissions, identity mapping and rate limits: a live check per hit, with a null-accountId guard

This section claims that live permission checks at query time are confirmed for both products and not deprecated, and that identity linking is the least-settled part of the design.

- **Confluence check** [primary: v1 spec]:
  - `POST /wiki/rest/api/content/{id}/permission/check` takes `{subject:{type:"user"|"group", identifier}, operation:"read"}`. The identifier can also be `"anonymous"`.
  - It returns `{hasPermission, errors[]}` and honours site, space and content restrictions, including inherited ones.
  - 400 means an invalid subject (for example an unknown accountId), 403 means the caller isn't admin, and 404 means the content is missing.
  - There is no batch variant, and v2 offers only the caller's own operations.
  - **Rule:** anything other than a 200 with `hasPermission=true` is a deny.
  - Behaviour for deactivated users, guests, and archived or trashed content is undocumented.
- **Jira check** [primary: v3 spec]:
  - `POST /rest/api/3/permissions/check` takes `{accountId, projectPermissions:[{permissions:["BROWSE_PROJECTS"], issues:[…]}]}` and returns the permitted ids.
  - **The 1,000-issue limit is confirmed**; more returns 400. Invalid ids and nulls are silently dropped, and absence means deny.
  - **New trap:** if `accountId` is omitted, Jira checks the *calling* account (the admin service account), which fails fully open. The code must refuse to send a request without it.
  - Issue security levels are implied by issue-level checking but not stated. Verify on the test site.
  - JSM portal-only customers probably lack BROWSE_PROJECTS, so they fail closed.
- **No impersonation for REST clients.** API tokens and 3LO have no "run as". Forge's `asUser(accountId)` offline impersonation exists [secondary], but it means building a Forge app plus a remote, which is a different architecture.
- **Connapse user to accountId: options and fail-open risks**
  - **(a) Jira user search** (`/rest/api/3/user/search?query=`) prefix-matches display name and email and omits hidden emails, so `bob@x.com` can match `bob@x.com.au` [primary].
  - **(b) Org admin API** (`/v2/orgs/{orgId}/directories/{dirId}/users/search`) returns email, `emailVerified`, account status and claim status, but needs a separate org-admin API key and covers only managed accounts [primary].
  - **(c) 3LO self-link:** the user signs in to Atlassian once, Connapse reads `GET api.atlassian.com/me` (`read:me`) and discards the token [primary]. This is proof of account ownership.
  - **(d) SAML/SCIM via Atlassian Guard:** no API exposes a NameID or externalId as a join key, so it collapses to email matching.
  - **Recommendation from the evidence:** (c) as the authoritative link. (a) or (b) only as an optional admin bulk-map, restricted to an exact case-insensitive single match on a verified, active, managed account, and deny otherwise.
- **Rate limits:**
  - The points model has been enforced since 2026-03-02 for Forge, Connect and 3LO apps: 1 point per request plus per-object costs (identity and access objects cost 2), a shared quota of 65,000 points per hour per app on Tier 1, and bursts of 100 requests per second [primary: Jira and Confluence rate-limiting pages].
  - **API-token traffic is not on the points model.** It has had its own limits since 2025-11-22, with no published numbers and `X-Beta-*` headers [primary for the exclusion; secondary for the separate limit].
  - Headers: `Retry-After`, `X-RateLimit-*` and `RateLimit-Reason`.
  - Estimate: 200 searches per hour with 50 Confluence hits each is 10,000 checks, roughly 30,000 points per hour if the points model applied (inferred). Jira needs one bulk call per search.
  - Cache allow and deny per (accountId, objectId) for about 5 minutes, which is the same window GitHub uses. Treat 429s and errors as deny.
- **Local evaluation cannot reach parity.** The restrictions API hides inherited restrictions (CONFCLOUD-71108), and spaces are mid-migration to role-based permissions (`PRE_ROLES`, `ROLES_TRANSITION`, `ROLES`) [primary]. Use local data only as a pre-filter that can deny, never allow.

### 5. Connapse internals: what the provider plugs into

This section claims that the provider reuses the source, sync and chunking engine unchanged, but needs three permission and search seams generalized that are currently hard-coded to AWS, Azure and GitHub.

- **Reuse as-is:**
  - `ISyncCursorConnector` / `SyncDelta`, including `IsFullListing` and `RequiresFullResync` (`src/Connapse.Core/Interfaces/ISyncCursorConnector.cs`);
  - `SourceSyncService`, a 5-minute background loop with compare-and-swap cursor writes, `DeletionGuard` and access-revoked hiding (`src/Connapse.Web/Services/SourceSyncService.cs`);
  - `ConnectorFile` with `ResourceUri`, `Metadata` and `Strategy`;
  - `RecordChunker`, which takes a header block and `--- … ---` separators and repeats the header in every chunk (`src/Connapse.Ingestion/Chunking/RecordChunker.cs`), plus content-pinned strategy handling;
  - connection secrets encrypted with DataProtection (`PostgresConnectionStore`);
  - the `ProviderStepCard` family;
  - the fail-closed wrapper in `HybridSearchService`;
  - the fake-`HttpMessageHandler` and `SharedWebAppFixture` test patterns.

  There is no separate record-shaped interface: GitHub issues are virtual files (`/issues/{n}.md`) with `Strategy = Record`, and Jira issues can follow the same pattern.
- **Must generalize:**
  - **`CompositeSearchScopeResolver`** takes fixed aws, azure and github constructor arguments and a hard-coded scheme combiner (`src/Connapse.Storage/CloudScope/CompositeSearchScopeResolver.cs`).
  - **`ISearchResultVerifier`** is registered once, Azure only (`src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs:283`). Atlassian's per-object checks belong on this over-fetch-then-drop path, so it needs a composite that routes by URI scheme.
  - **`PrivateSourceVisibility`**, which hides source listings, is GitHub-only.
  - The typed per-provider methods on `IProviderCredentialStore`, `ProviderSetupReader`'s three hard-coded entries, and the `Key == "aws" | "azure" | "github"` branches in the 4,070-line `Providers.razor`.
  - `GitHubApiClient`'s paging and rate-limit logic is GitHub-shaped, and the named HttpClient has no resilience handlers. `ProviderResilience` exists but is used only for model providers.
  - The identity-link tables are one per provider; Atlassian needs its own link table or a generic one.
- **Net-new:**
  - a `ConnectionProvider.Atlassian` enum value (stored as an int, so no migration);
  - a factory branch that splits by kind (Confluence or Jira);
  - the Atlassian HTTP client;
  - the storage-XHTML converter and the ADF renderer;
  - `atlassian://` resource URIs;
  - the verifier(s);
  - the link flow;
  - the provider card and New-source form;
  - an `IConnectionTester`;
  - a fake Atlassian handler.
- **Risks found in existing code:**
  - A document with a null `ResourceUri` is visible to everyone (`KeywordSearchService.cs` filter), so every Atlassian document must carry a URI or it fails open.
  - MCP `get_document` appears to resolve only the container, not source visibility (`McpTools.cs:621-631`). This is unverified; if true, it affects GitHub-private documents today.
  - Issue #374 (Search.razor bypassing scope) is stale: all three search surfaces now pass `UserId` into `HybridSearchService`.
  - The per-source eval harness does not exist; `tests/Connapse.Eval` uploads corpora into a container and bypasses connectors and permissions.
- **Premise drift:** GitHub is now App-only and supports private repositories. "Connection-less" GitHub sources throw at sync time (`ConnectorFactory.cs:159-164`). The Atlassian design should follow the connection-bound path.

## Conflicts and uncertainties

- **Service-account OAuth versus API token, and rate limits.** The auth research recommends the service account's OAuth client-credentials credential. The permissions research found that API-token traffic is exempt from the points quota while OAuth app traffic is on it. Whether service-account client-credentials traffic counts as "app" traffic is not documented. With a permission check per Confluence hit, this decides whether Connapse shares the 65,000-points-per-hour pool. Unresolved; test it, or pick the scoped API token and accept a yearly rotation.
- **3LO account linking versus self-hosting.** The permissions research recommends a 3LO self-link as the only proof of accountId. The auth research notes that 3LO needs a developer-console app with a fixed callback URL per install. GitHub already solves the same problem with a per-install GitHub App plus an OAuth link, so an admin registering an Atlassian OAuth app during setup is consistent with precedent. It does add a setup step. Unresolved design choice.
- **Connect end of support:** 2026-03-31 (end of updates) and 2027-01-31 (end of support) from the Atlassian blog [primary], versus "Q4 2026" in a secondary summary. Irrelevant to the recommendation.
- **ADF shared between Confluence and Jira:** supported by the schema package, but the claim that Confluence's `atlas_doc_format` is the same ADF rests on a tertiary community post. The design renders Confluence from storage anyway, so the shared renderer is optional.
- **The 20-comment inline cap in enhanced search** rests on one secondary source.
- **Single-source load-bearing claim:** service-account "App admin" equals the admin rights the checks need (a vendor guide seen only as a snippet).

## Gaps: what we did not find

- The numeric limits for API-token traffic.
- The points cost of a Confluence permission check, and of a Jira bulk check per issue.
- Confluence check behaviour for deactivated users, guests, and archived or trashed content.
- Explicit confirmation that Jira's bulk check applies issue security levels, and how it treats JSM portal-only customers.
- Whether service-account credentials are available on Free and Standard plans, and what the "centralized user management" prerequisite means for older orgs.
- Whether a single scoped token can carry both Jira and Confluence scopes.
- Whether `GET /me` returns an email-verified flag, and whether API-token callers can use `/user/email`.
- Which timezone Confluence CQL `lastmodified` uses exactly (site or profile).
- Whether page moves, or Jira comment deletions, bump the modified or `updated` timestamps.
- A documented way to detect Confluence admin rights.
- Any .NET library for ADF or Confluence storage format.
- How to extract whiteboard or database text.

## Source quality assessment

Most API facts rest on primary sources: the Jira v3, Confluence v1 and Confluence v2 OpenAPI specs, downloaded and parsed on 2026-09-30, plus developer.atlassian.com and support.atlassian.com pages. Reference-implementation behaviour (Onyx, LlamaIndex, mcp-atlassian) is secondary but cited to source files. The load-bearing single-source or weak claims are:
- that service-account App admin grants the global admin permission (secondary snippet);
- the enhanced-search 20-comment cap (secondary);
- Confluence ADF equals Jira ADF (tertiary);
- the Forge permission-check trap and Forge impersonation details (tertiary and community).

The Connapse internals section rests on direct code reading with file references. The 2026-09-09 report's permission findings were primary and are confirmed here, with the Jira 1,000-issue limit now verified from the spec.

## Sources

**Primary**
- Jira v3 OpenAPI: https://developer.atlassian.com/cloud/jira/platform/swagger-v3.v3.json
- Confluence v1: https://developer.atlassian.com/cloud/confluence/swagger.v3.json
- Confluence v2: https://dac-static.atlassian.com/cloud/confluence/openapi-v2.v3.json
- Confluence content permissions: https://developer.atlassian.com/cloud/confluence/rest/v1/api-group-content-permissions/
- Confluence scopes: https://developer.atlassian.com/cloud/confluence/scopes-for-oauth-2-3LO-and-forge-apps/
- CQL fields: https://developer.atlassian.com/cloud/confluence/cql-fields/
- Confluence webhooks: https://developer.atlassian.com/cloud/confluence/modules/webhook/
- Confluence rate limiting: https://developer.atlassian.com/cloud/confluence/rate-limiting/
- Jira rate limiting: https://developer.atlassian.com/cloud/jira/platform/rate-limiting/
- Jira webhooks: https://developer.atlassian.com/cloud/jira/platform/webhooks/
- Jira search and reconcile: https://developer.atlassian.com/cloud/jira/platform/search-and-reconcile/
- ADF structure: https://developer.atlassian.com/cloud/jira/platform/apis/document/structure/
- Basic auth: https://developer.atlassian.com/cloud/jira/platform/basic-auth-for-rest-apis/
- 3LO apps: https://developer.atlassian.com/cloud/jira/platform/oauth-2-3lo-apps/
- API tokens: https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/
- Service accounts: https://support.atlassian.com/user-management/docs/understand-service-accounts/
- Service-account tokens: https://support.atlassian.com/user-management/docs/manage-api-tokens-for-service-accounts/
- Service-account OAuth: https://support.atlassian.com/user-management/docs/create-oauth-2-0-credential-for-service-accounts/
- cloudId: https://support.atlassian.com/jira/kb/retrieve-my-atlassian-sites-cloud-id/
- Org users API: https://developer.atlassian.com/cloud/admin/organization/rest/api-group-users/
- Page restrictions: https://support.atlassian.com/confluence-cloud/docs/add-or-remove-page-restrictions/
- Inherited restrictions KB: https://support.atlassian.com/confluence/kb/confluence-get-page-restrictions-api-doesnt-display-inherited-restrictions/
- Storage format: https://confluence.atlassian.com/doc/confluence-storage-format-790796544.html
- ContentRepresentation: https://docs.atlassian.com/ConfluenceServer/javadoc/7.20.1/com/atlassian/confluence/api/model/content/ContentRepresentation.html
- Connect end of support: https://www.atlassian.com/blog/development/announcing-connect-end-of-support-timeline-and-next-steps
- JRACLOUD-74279: https://jira.atlassian.com/browse/JRACLOUD-74279
- CONFCLOUD-76352: https://jira.atlassian.com/browse/CONFCLOUD-76352
- RFC-83 live docs: https://community.developer.atlassian.com/t/rfc-83-live-docs-pages-in-confluence-cloud/88495

**Secondary**
- Onyx Confluence connector: https://github.com/onyx-dot-app/onyx/blob/main/backend/onyx/connectors/confluence/connector.py and `onyx_confluence.py`
- Onyx Jira connector: `backend/onyx/connectors/jira/connector.py`, `utils.py` and `access.py`
- LlamaIndex Confluence reader: https://github.com/run-llama/llama_index/blob/main/llama-index-integrations/readers/llama-index-readers-confluence/llama_index/readers/confluence/base.py
- LlamaIndex Jira reader: https://github.com/run-llama/llama_index/blob/main/llama-index-integrations/readers/llama-index-readers-jira/llama_index/readers/jira/base.py
- mcp-atlassian: https://github.com/sooperset/mcp-atlassian (`models/jira/adf.py`, `preprocessing/base.py`, `jira/search.py`)
- Adaptavist on the search deprecation: https://docs.adaptavist.com/sr4jc/latest/release-notes/breaking-changes/atlassian-rest-api-search-endpoints-deprecation/
- API-token rate limiting thread: https://community.developer.atlassian.com/t/api-token-rate-limiting/92292
- Rewind service-account guide: https://help.rewind.com/hc/en-us/articles/28787160870043-The-Importance-of-Service-Accounts
- CQL UTC thread: https://community.atlassian.com/forums/Confluence-questions/How-do-I-pass-a-UTC-time-as-the-value-of-lastModified-in-a-REST/qaq-p/1557903

**Tertiary (flagged where used)**
- Forge permission-check thread: https://community.developer.atlassian.com/t/confluence-content-id-permission-check-from-always-returns-no-permission-for-user-but-im-the-owner-of-the-pages/101710
- Forge impersonation thread: https://community.developer.atlassian.com/t/how-to-use-forge-offline-user-impersonation-from-remote/94615
- Unscoped-token deprecation thread: https://community.developer.atlassian.com/t/api-tokens-without-scopes-are-being-deprecated/92510
- Confluence ADF thread: https://community.developer.atlassian.com/t/confluence-rest-api-v2-create-page-with-atlas-doc-format-representation/67565
- mcp-atlassian issue #847
- AdfKit on NuGet
