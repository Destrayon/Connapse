# Atlassian provider, part 1: Confluence Cloud — design

**Epic:** #540 · **Date:** 2026-09-30 · **Status:** Approved in brainstorming, pending written review
**Research:** [atlassian-provider-lay-of-land-2026-09-30.md](../../research/atlassian-provider-lay-of-land-2026-09-30.md)

## Goal and scope

Index Confluence Cloud spaces into Connapse and filter every search hit live against Confluence's own answer to "can this user read this page", failing closed. This spec builds the shared Atlassian plumbing (site connection, user account linking, per-hit verification) with Confluence as the first source. Jira Cloud gets its own spec afterwards and reuses that plumbing.

**Out of scope:**
- Jira (next spec).
- Confluence Data Center.
- Archived pages.
- Whiteboards, databases and embeds, which have no text through REST.
- A search hint for unlinked users. Search stays silent, and a later change will hide such content properly.
- Any write to Atlassian. Connapse never writes grants.
- The link graph. Page-link titles are kept in metadata only.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Delivery | Confluence first, Jira second | Bigger demand, and it exercises the harder per-hit permission path first |
| Site credential | Atlassian service account with an **OAuth 2.0 client-credentials** credential | Renews itself (1-hour tokens, no yearly paste); no seat; not tied to a person |
| Account linking | User self-link via Atlassian OAuth (3LO), `read:me` only, tokens discarded | Proves account ownership; Connapse emails are unverified (`RequireConfirmedEmail = false`), so email matching could fail open |
| Content | Current pages (including live docs) and blog posts, comments appended, attachments (on by default, size cap, per-source toggle) | Mirrors what Confluence's own search returns |
| Granularity | One source per space | Same as one source per GitHub repo; each space syncs, fails and is removed independently |
| Unlinked users | Silent: no Confluence results, no hint | Owner's call; hiding will be addressed later |
| Permission model | Live per-hit check, cached 5 minutes, plus a pre-filter for unlinked users | Only live checks match Confluence (inherited restrictions, the space-role migration); a sync-time copy cannot |

## 1. Architecture and data model

**Provider level (one per install).** The Atlassian OAuth app used only for account linking: a client ID and an encrypted client secret in `provider_credentials`, like the GitHub App. It never reads content. Atlassian account ids are global across an org's sites, so one link per user covers every site.

**Connection (one per site).** `ConnectionProvider.Atlassian`, a new int value that needs no migration.
- `ConfigJson`: `{siteUrl, cloudId, clientId}`.
- The service account's client secret goes in the existing DataProtection-encrypted connection secret.

**Source (one per Confluence space).**
- `ScopeJson`: `{kind: "confluence-space", spaceId, spaceKey, includeAttachments: true, maxAttachmentMb}`.
- Jira will add `kind: "jira-project"` on the same connection.
- `ConnectorFactory` branches on `kind`.

**Resource URIs.**
- Pages and blog posts: `atlassian://{cloudId}/confluence/page/{contentId}`.
- Attachments: `atlassian://{cloudId}/confluence/page/{contentId}/attachment/{attachmentId}`.
- Every document must carry one, because a document with a null `ResourceUri` is visible to everyone.

**Paths are id-based**, so a rename or move never deletes and re-ingests: `/pages/{id}.md`, `/blogposts/{id}.md`, `/attachments/{attachmentId}/{filename}`.

**New components** (in `src/Connapse.Storage/Connectors/Atlassian/` unless noted):

| Component | Responsibility |
|---|---|
| `AtlassianApiClient` | Client-credentials token exchange at `auth.atlassian.com/oauth/token`, cached until about 5 minutes before expiry; calls pinned to `api.atlassian.com/ex/confluence/{cloudId}`; `Link: next` cursor paging restricted to that host; a 429 raises `AtlassianRateLimitedException(retryAfter)` so sync resumes next cycle |
| `ConfluenceSpaceConnector : ISyncCursorConnector` | Sync (section 3) |
| `ConfluencePageStateStore` | Per-page state, following `GitHubRecordStore` (section 3) |
| `ConfluenceStorageRenderer` | Storage XHTML to markdown with AngleSharp (section 3) |
| `AtlassianSearchResultVerifier : ISearchResultVerifier` | Per-hit checks (section 4) |
| `AtlassianSearchScopeResolver` | Pre-filter for unlinked users (section 4) |
| `AtlassianConnectionTester : IConnectionTester` | Setup probes (section 2) |
| `user_atlassian_identity_links` table and link flow (Identity) | Account linking (section 2) |
| Atlassian provider card component (Web) | Setup UI; its own component, not new branches in `Providers.razor` |

**Shared seams generalized (no behaviour change for existing providers):**
- `ISearchResultVerifier` becomes a composite that routes each hit by URI scheme. `AzureSearchResultVerifier` moves under it unchanged.
- `CompositeSearchScopeResolver` takes an injected list of resolvers instead of fixed aws, azure and github constructor arguments and a hard-coded scheme combiner.
- `ProviderSetupReader`'s three hard-coded entries become a registry that providers add to.
- `PrivateSourceVisibility` learns that Atlassian sources are admin-only (section 4).

## 2. Setup and account linking

The Providers page Atlassian card has three `ProviderStepCard`s, each with Easy setup and Manual values. Atlassian has no app-manifest flow, so "easy" means pre-filled values, copy buttons and deep links.

1. **Register the linking app (once per install).**
   - Deep link to developer.console.atlassian.com to create an OAuth 2.0 integration.
   - Show the callback URL `{connapse}/api/identity/atlassian/callback` with a copy button.
   - The only permission is User identity API `read:me`.
   - Remind the admin to enable distribution ("sharing"), because otherwise only the app owner can authorize.
   - The admin pastes the client ID and secret. The step turns green after a real authorize round-trip.
2. **Add a site (repeatable; each site is one Connection).**
   - The admin enters the site URL, and Connapse resolves the cloudId from unauthenticated `GET https://{site}.atlassian.net/_edge/tenant_info`.
   - Guided steps in admin.atlassian.com:
     - create a service account;
     - grant Confluence access and Confluence Administrator;
     - create an OAuth 2.0 credential with these granular scopes, shown with copy buttons: `read:space:confluence`, `read:page:confluence` (covers blog posts), `read:comment:confluence`, `read:attachment:confluence`, `read:folder:confluence`, `read:content-details:confluence` (v1 CQL search and the user lookups), and `read:content.permission:confluence` (permission check). Taken from the Confluence v1 and v2 OpenAPI specs on 2026-09-30. The v1 granular scopes are marked Beta there.
   - The admin pastes the client ID and secret.
3. **Test connection.** The probes run in order, and the failing one is named:
   1. token exchange;
   2. `GET /wiki/rest/api/user/current`;
   3. list spaces;
   4. **admin probe**: `POST /wiki/rest/api/content/{anyPageId}/permission/check` with a group subject, where 403 means "not Confluence Administrator".

   A site that fails the admin probe cannot be saved. Without admin, every check is a 403, which is safe but makes Confluence invisible to everyone.

**User linking** sits beside "Link GitHub account" on the user's linked-accounts page:
1. "Link Atlassian account" starts the authorization-code flow at `auth.atlassian.com/authorize` with scope `read:me`, a one-time `state` value, and PKCE if Atlassian accepts it (verified on the test site).
2. The callback exchanges the code, calls `GET https://api.atlassian.com/me`, stores `{userId, accountId, displayName, email, linkedAt}`, and **discards the tokens**. No `offline_access` is requested and no user token is stored.
3. The email is for display only and is never used for matching.
4. Users can unlink. Admins see a read-only list of linked users.
5. A deactivated Atlassian account needs no special handling, because Confluence's check answers no.

The New-source form, used after a site exists, picks the connection, then multi-selects spaces (current, non-personal by default, with a toggle to show personal spaces). It creates one source per chosen space, with the attachments toggle and size cap.

## 3. Sync and rendering

Sync runs inside the existing 5-minute `SourceSyncService` loop. The connector returns an `IsFullListing` delta, so the existing reconcile diffs it by signature and applies `DeletionGuard`.

**Each cycle, per space:**
1. **Slim listing.** v2 `GET /spaces/{id}/pages` and `/blogposts` with `status=current`, `limit=250`, no body. The listing yields id, `version.number`, `version.createdAt`, `parentId`, `parentType`, title and subtype. Anything missing (trashed, deleted, moved to another space, archived) drops out and is deleted through the normal diff.
2. **Change query.** v1 CQL search: `space = KEY AND type IN (comment, attachment) AND lastmodified >= <watermark − 1 day>`, ordered by `lastmodified`. The one-day overlap makes correctness independent of CQL's timezone handling. Hits update the page-state store, and repeats are no-ops.
3. **Daily attachment sweep.** Once per day per source, list `GET /pages/{id}/attachments` for every page to catch deleted attachments, which the change query cannot see.

**Page-state store** (per source, following `GitHubRecordStore`): one record per page and blog post holding `{version, lastCommentAt, attachments[{id, version, filename, size}], title, parentId, parentType}`.
- The listing emits pages from the API plus their known attachments from the store, so reconcile never deletes a live attachment.
- The page signature is `version.createdAt` combined with `lastCommentAt`. A new or edited comment re-ingests the page; an unchanged page is never re-fetched.
- **Known gap:** a deleted comment lingers until the page next changes. There is no permission impact, because comments inherit the page's permission.

The cursor holds `{watermark, lastAttachmentSweepAt}` as JSON in `sources.sync_cursor`.

**Reading content** (when ingestion asks the connector for a file):
- Page or blog post: `GET /pages/{id}?body-format=storage` (or blogposts), plus its footer and inline comments.
- Attachment: v1 `GET /wiki/rest/api/content/{pageId}/child/attachment/{attachmentId}/download`.
  - Skip it, and count it in the source status, if it's over `maxAttachmentMb` or has no Connapse parser.
- **Never** fetch `view`, `export_view` or `styled_view`. Those render include macros as the service account and would leak restricted content into less-restricted pages.

**`ConfluenceStorageRenderer` (storage XHTML to markdown):**

| Storage element | Output |
|---|---|
| Headings, paragraphs, lists, tables, `a` | Markdown equivalents |
| `code` macro | Fenced block with language |
| `info` / `note` / `warning` / `tip` / `panel` | Blockquote with a label ("Note: …") |
| `expand` | Its title, then its body inline |
| `status` | Its text |
| `jira` | The issue key |
| `include` / `excerpt-include` | `[includes: Page Title]`, **never the included content** |
| `toc` / `children` / `pagetree` | Dropped |
| `ri:user ri:account-id` | Display name, via cached `GET /wiki/rest/api/user/bulk` |
| `ac:link` + `ri:page` | Link text (title); target title added to `confluence:links` metadata |
| `ri:attachment` image or link | `[attachment: filename]` |
| `ac:task-list` | Markdown checkboxes |
| Unknown macro | Its rich-text or plain-text body if any; otherwise dropped |

Comments are appended under `## Comments`, each as `--- Comment by {Name}, {date} ---` followed by its body.

**Chunking and breadcrumbs.**
- Pages and blog posts use the existing DocumentAware chunker, which already prefixes each chunk's heading path. **One change:** the chunker prepends a document-level `breadcrumb` metadata value ahead of the heading path.
- The breadcrumb is `Space name › ancestor titles › page title`, built from the listing's parent ids. Blog posts use `Space name › Blog › title`. Folder parents cost one cached `GET /folders/{id}` each.
- Attachments use their existing parsers, with breadcrumb `…page breadcrumb › filename`.

## 4. Search-time permission checks

**Pre-filter.** `AtlassianSearchScopeResolver` drops every `atlassian://` prefix for a user with no Atlassian link, so unlinked users cost zero API calls and see nothing from Confluence.

**Per-hit verification** (linked users):
- Search over-fetches through the existing candidate multiplier.
- The composite verifier routes `atlassian://` hits to `AtlassianSearchResultVerifier`.
- For each hit it resolves the connection from the URI's cloudId and calls `POST /wiki/rest/api/content/{contentId}/permission/check` with `{subject: {type: "user", identifier: accountId}, operation: "read"}`. An attachment uses its page's id.
- At most **10 checks run at once** per search, with a **total budget of about 3 seconds**. Anything unchecked when the budget runs out is denied.
- Denied hits are dropped and backfilled from lower-ranked candidates, as with Azure.

**Cache** (in-memory `IMemoryCache`, not persisted):
- The key is `(cloudId, accountId, contentId)`.
- Allow answers last 5 minutes; failures last 30 seconds and count as deny.
- Revocations therefore take effect within 5 minutes.

**Fail-closed rules.** A hit is allowed **only** on HTTP 200 with `hasPermission: true`. Everything else denies:
- 400, 403 or 404;
- 429 or other 5xx responses;
- a timeout;
- an undecryptable secret, or a failed token exchange;
- a missing document row;
- a URI that doesn't parse or names an unknown cloudId.

A guard refuses to send any check whose identifier is empty, whitespace or `anonymous`. The same guard pattern will protect Jira's null-`accountId` trap. When checks for a site fail repeatedly, the connection shows a warning on the Sources page.

**Other surfaces.**
- Blazor, REST and MCP search already route through `HybridSearchService`, so all three get this.
- **Direct document reads** (MCP `get_document`, REST fetch by id or path) must run the same verifier for `atlassian://` documents before returning content.
- `PrivateSourceVisibility` shows Atlassian sources to admins only, because space names can be sensitive.

## 5. Testing

- **`FakeAtlassianApi : HttpMessageHandler`**, stateful like `FakeGitHubApi`. It holds spaces, pages and blog posts with versions and parents, folders, comments, attachments, CQL search, a per-account permission table, the token endpoint, and switches for 429, 5xx, timeouts and "not admin".
- **Renderer unit tests.** Hand-written storage-XHTML fixtures, one per table row above, including the include placeholder and unknown macros.
- **Sync integration tests** (`SharedWebAppFixture`):
  - first sync; edit; delete; move out of the space;
  - a new comment re-ingests the page;
  - a new attachment is ingested;
  - the attachment sweep deletes a removed attachment;
  - an oversized or unsupported attachment is skipped and counted;
  - the deletion guard trips on a mass disappearance;
  - the breadcrumb appears in chunks.
- **Enforcement integration tests.** Seeded `atlassian://` documents: linked-allowed, linked-denied, unlinked (no API call made), 403, 429, timeout, budget exhaustion, the empty-identifier guard, direct document read, and an attachment checked against its page. Also an Azure-verifier regression test under the composite.
- **Linking tests:** state mismatch rejected; `/me` failure leaves no link; tokens not persisted.
- **bUnit tests:** the provider card steps and the New-source space picker.
- **Live test-site checklist (before release)** on a free Atlassian Cloud site where the owner is admin:
  1. A service account with App admin passes the admin probe and gets correct answers for other users.
  2. Record the rate-limit headers on client-credentials responses; confirm it isn't on the points quota.
  3. Account linking works with distribution enabled; PKCE accepted or not.
  4. Behaviour for inherited restrictions, a deactivated user, and a page moved between spaces.

## 6. Delivery order

The implementation plan sizes these into PRs.

1. **Generalize seams** with no behaviour change: the composite verifier, the resolver list, and the provider registry.
2. **Site connection:** the API client, `ConnectionProvider.Atlassian`, the tester, and provider card steps 1–3.
3. **Account linking:** the table, the flow, and the linked-accounts UI.
4. **Confluence sync:** the connector, the page-state store, the renderer, the chunker breadcrumb, and the New-source form.
5. **Enforcement:** the verifier, the pre-filter, direct-read enforcement, and source-listing visibility.
6. **Comments and attachments:** the change query, the attachment sweep, and attachment ingestion.
7. **Live-site validation** against the checklist, with any resulting fixes.

## Open questions (resolved on the test site, not by design)

- Whether "App admin" on a service account grants the Confluence Administrator permission the checks require. If it doesn't, the setup step changes to granting the global permission explicitly.
- Whether client-credentials traffic is on the 65,000-points-per-hour app quota. If it is, add a scoped API token as an alternative credential (a follow-up issue).
- Whether Atlassian's authorize endpoint accepts PKCE.
