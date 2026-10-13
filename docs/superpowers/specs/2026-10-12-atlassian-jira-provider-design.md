# Atlassian provider, part 2: Jira Cloud — design

**Epic:** #540 · **Date:** 2026-10-12 · **Status:** Approved in brainstorming, pending written review
**Builds on:** [part 1, Confluence](2026-09-30-atlassian-confluence-provider-design.md) (merged in #697)
**Research:** [atlassian-provider-lay-of-land-2026-09-30.md](../../research/atlassian-provider-lay-of-land-2026-09-30.md), [jira-restricted-comments-2026-10-12.md](../../research/jira-restricted-comments-2026-10-12.md)

## Goal and scope

Index Jira Cloud projects into Connapse and filter every search hit live against Jira's own answer, failing closed. Jira reuses part 1's site connection, account linking, unlinked-user pre-filter and verifier. What's new is a Jira sync, an ADF renderer, a bulk issue check, and a local rebuild of Jira's comment-visibility rule. No Jira API answers that rule for another user.

**Out of scope:**
- Jira Data Center.
- Worklogs, which carry little text.
- Any write to Atlassian.
- A Forge app or stored user tokens. Both were considered for restricted comments and rejected; see Decisions.
- The issue-link graph. Links are kept in the record header only.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Restricted comments | Indexed as their own documents and checked at search time by rebuilding Jira's rule: browse the issue **and** belong to the comment's group or role | Skipping them loses real content (owner); no endpoint answers it for another user; this needs no new credential or app |
| Rejected alternatives | Forge offline impersonation; stored per-user 3LO refresh tokens | Forge needs an app plus backend per site (per self-hosted install); stored tokens reverse the discard-tokens decision and need yearly re-consent |
| JSM internal notes | Their own documents, gated by the `SERVICEDESK_AGENT` project permission in the same bulk check | Exact and live; otherwise customers approved for an issue would see agent-only notes |
| Comment-author exception | Not modelled | Sources conflict; ignoring it is stricter than Jira, never looser |
| Content | Issues (header fields, description, rich-text custom fields, comments) and attachments (on by default, size cap, per-source toggle). All project types, including JSM | Mirrors Confluence's content choices |
| Granularity | One source per project | Same as one source per space |
| Site connection | The same Atlassian site and service account; each product passes or fails the site test independently | A site may serve Confluence, Jira or both |

## 1. Architecture and data model

**Site connection.**
- The existing `ConnectionProvider.Atlassian` connection and `ConfigJson` (`{siteUrl, cloudId, clientId}`) are unchanged.
- One client-credentials token covers both products, so `AtlassianTokenSource` is reused as is.
- The site test records per-product results in `ConnectionHealth`. A site can be saved when at least one product passes all its probes.
- New source offers only products that passed.

**API client.**
- `AtlassianApiClient` takes a product, Confluence or Jira, which sets its pinned prefix: `/ex/confluence/{cloudId}` or `/ex/jira/{cloudId}`. `EnsureOnSite` and `Resolve` follow that prefix.
- The `/wiki/` path resolution applies only to Confluence.
- It gains a `nextPageToken` paging helper for JQL.
- The 401-retry and 429 behaviour (`AtlassianRateLimitedException`) are unchanged.

**Source (one per Jira project).**
- `ScopeJson`: `{kind: "jira-project", projectId, projectKey, includeAttachments: true, maxAttachmentMb}`.
- `ConnectorFactory.CreateAtlassian` branches on `kind` and returns `ISyncCursorConnector`.
- The other `"confluence-space"` branches handle `"jira-project"` too: `SourceResponse`, `SourceScopeSummary`, `PrivateSourceVisibility` (admin-only listing, searchable when hidden), the source edit form, and `SourcesEndpoints` create/patch, through a `JiraSourceScope` beside `ConfluenceSourceScope`.

**Documents.** Paths are id-based, so renames and moves never re-ingest.

| Path | Content | Access rule |
|---|---|---|
| `/issues/{issueId}.md` | Record header, description, rich-text custom fields, unrestricted public comments | Browse the issue |
| `/issues/{issueId}/comments/{commentId}.md` | One restricted comment or JSM internal note | Browse, plus group/role membership, plus agent for internal notes |
| `/issues/{issueId}/attachments/{attachmentId}/{filename}` | An attachment | Browse, plus its comment's restriction if it was added in a restricted comment |

**Resource URIs.**
- An issue is `atlassian://{cloudId}/jira/issue/{issueId}`.
- A comment appends `/comment/{commentId}`, and an attachment appends `/attachment/{attachmentId}`.
- `AtlassianUri` gains a product field and parses both products. A URI that doesn't parse is denied, as today.
- Every document carries a URI, because a null `ResourceUri` is visible to everyone.

**Restrictions travel in the URI.** A restricted document's URI ends with its restriction:
- `/internal` for a JSM internal note;
- `/group/{groupId}` for a group restriction;
- `/role/{projectId}/{escaped role name}` for a role restriction. The project is the one whose roles the name is resolved in.

For example, an internal note restricted to a group is `atlassian://{cloudId}/jira/issue/10001/comment/20002/internal/group/{groupId}`.
- The verifier and direct reads already load only URIs. Sync rewrites a document's URI on every listing pass, even when its content isn't re-ingested (`SourceSyncService`), so a changed restriction takes effect at the next sync.
- A comment URI with no restriction does not parse and is denied, because only restricted comments are separate documents.
- Comment and attachment documents also carry `jira:visibility` metadata, for display only.

**New components** (in `src/Connapse.Storage/Connectors/Atlassian/` unless noted):

| Component | Responsibility |
|---|---|
| `JiraProjectConnector : ISyncCursorConnector` | Sync (section 2) |
| `JiraIssueStateStore` | Per-issue state, following `GitHubRecordStore` |
| `AdfRenderer` | ADF to markdown (section 2) |
| `JiraRecordRenderer` | Record header plus body for an issue; comment documents |
| `JiraPermissionChecker` (CloudScope) | Bulk issue check, group and role membership (section 3) |
| Jira probes in `AtlassianConnectionTester` | Site test (section 4) |
| `JiraProjectForm.razor` (Web) | New-source project picker |

## 2. Sync and rendering

Sync runs inside the existing 5-minute `SourceSyncService` loop. The connector returns an `IsFullListing` delta, so reconcile diffs by signature and applies `DeletionGuard`.

**Each cycle, per project:**
1. **Id listing.**
   - JQL `project = {projectId}`, fields `id`, up to 5,000 per page with `nextPageToken`.
   - An issue missing from the listing (deleted, or moved to another project) is deleted through the diff.
   - A moved issue keeps its id and appears in the other project's source.
2. **Change query.**
   - JQL `project = {projectId} AND updated >= {watermarkMs − 5 minutes} ORDER BY updated`.
   - The bound is **unquoted epoch milliseconds**, because a quoted date is read in the service account's timezone (JRACLOUD-74279). The overlap covers search indexing lag. Repeats are no-ops.
3. **Fetch changed issues.**
   - `POST /rest/api/3/issue/bulkfetch`, 100 per call, with the header fields, `description`, the discovered rich-text custom fields and `attachment`.
   - Comments come from `GET /issue/{id}/comment`, paged, not from the 20-comment inline cap.
   - Restricted comments are requested with `expand=renderedBody`.

**Issue-state store.** One record per issue: `{updated, comments[{id, updated, visibility, internal}], attachments[{id, filename, size, created, restrictingCommentId?}]}`.
- The listing emits each issue document, its comment documents and its attachments from the store, so reconcile never deletes live children.
- An issue document's signature is `updated`. A comment document's signature is the comment's `updated`.
- The cursor is `{watermarkMs, lastCommentSweepAt}` in `sources.sync_cursor`. An unparseable cursor returns `RequiresFullResync`.
- A 429 returns a `Deferred` delta that keeps the cursor. A 403 or 404 on the project maps to the source-unreadable path, as for Confluence.

**Comment routing:**
- No `visibility` and `jsdPublic` not false: appended to the issue document under `## Comments`, each as `--- Comment by {Name}, {date} ---`.
- Group or role `visibility`, or `jsdPublic: false`: its own comment document with the restriction metadata. Its breadcrumb is the issue's plus "Comment".
- A comment whose restriction changes moves between the two on the next sync. The issue document re-renders and the comment document appears or is deleted.

**Attachments:**
- An attachment is tied to a restricted comment when that comment's `renderedBody` links to `/attachment/content/{id}` (or the v2 equivalent). It then inherits that comment's restriction.
- An attachment not linked from any restricted comment follows the issue rule.
- The size cap, parser check, skip counting in `SyncDelta.Notice` and the `includeAttachments` toggle behave exactly as in Confluence.
- Content is read with `GET /rest/api/3/attachment/content/{id}`, redirect-checked, through `SizeCappedStream`.

**`JiraRecordRenderer`:**
- The header is the key and summary, type, status, priority, assignee, reporter, labels, components, fix versions, parent (key only), issue links (type, direction and key only, never the linked issue's summary, which may be in a project or security level the reader can't see), created and updated.
- The description and each rich-text custom field follow, as `## {Field name}` sections. Rich-text custom fields are those whose schema is a textarea custom type, discovered once per cycle through `GET /rest/api/3/field`.
- Display names come from the issue payload. Mentions use their `attrs.text`.

**`AdfRenderer` (ADF JSON to markdown):**

| ADF node | Output |
|---|---|
| `heading`, `paragraph`, `bulletList`, `orderedList`, `table`, `codeBlock` (with language), `blockquote`, `rule` | Markdown equivalents |
| `panel` | Blockquote with its type as a label ("Note: …") |
| `expand` / `nestedExpand` | Title, then body inline |
| `mention` | `attrs.text` |
| `inlineCard` / `blockCard` | The URL |
| `status`, `emoji`, `date` | Their text |
| `media` / `mediaSingle` / `mediaGroup` | `[attachment: filename]` when resolvable, otherwise dropped |
| `taskList` / `decisionList` | Markdown checkboxes / list |
| Marks (`strong`, `em`, `code`, `link`, `strike`) | Markdown marks |
| Unknown node | Its children's text |

**Chunking and breadcrumb.**
- Issue documents use `ChunkingStrategy.Record`, the chunker built for GitHub issues. Comment documents use the default chunker.
- Every document starts with the breadcrumb `Project name > KEY-123 summary`. A `>` inside a title is replaced so it can't forge a level.
- The breadcrumb goes in `jira:breadcrumb` metadata.

**Daily comment sweep (conditional).** If the live site shows that changing a comment's visibility doesn't bump the issue's `updated` (checklist item 4), the connector re-reads every issue's comments once per day per source. The issue document's signature then also folds in a hash of its comments' restrictions. Until the live site answers, the sweep is built and enabled.

## 3. Search-time permission checks

**Pre-filter.** `AtlassianSearchScopeResolver` is unchanged. An unlinked user gets no `atlassian://` results and costs no API calls.

**Dispatch.**
- `AtlassianSearchResultVerifier` parses each hit's product. Confluence hits go to `ConfluencePermissionChecker` unchanged; Jira hits are gathered per site for `JiraPermissionChecker`.
- The existing budget (3 seconds), the parallelism cap, unchecked-means-deny and backfill from lower-ranked candidates apply to both.

**Jira checks, per search and per site:**
1. **Bulk issue check.**
   - `POST /rest/api/3/permissions/check` with `{accountId, projectPermissions: [{permissions: ["BROWSE_PROJECTS"], issues: [...]}]}` over the distinct issue ids behind every Jira hit.
   - When any hit is an internal note, a second entry asks `SERVICEDESK_AGENT` for those issues.
   - Calls are split at 1,000 issues.
   - An issue absent from the answer is denied, because Jira silently drops invalid ids.
2. **Groups.** `GET /rest/api/3/user/groups?accountId=` once per account and site. Restrictions match on `groupId`, never on the mutable name.
3. **Roles.**
   - `GET /rest/api/3/project/{projectId}/role` maps the role name to an id within the issue's project. Zero or several matches deny.
   - `GET /rest/api/3/project/{projectId}/role/{roleId}` lists user actors (`accountId`) and group actors (`groupId`).
   - The user passes if their account or one of their groups is an actor.
4. **Live comment check.**
   - A restricted comment's or internal note's URI carries the visibility it had at the last sync, so a restriction tightened in Jira would otherwise be enforced only after the next sync, or later when syncs are rate limited or failing.
   - So each comment hit that passed the checks above is read again as the service account, `GET /rest/api/3/issue/{issueId}/comment/{commentId}`, and routed by the connector's own rule. It is admitted only if Jira still gives it exactly the URI's internal flag and restriction.
   - A comment now public, restricted differently, deleted or unreadable (404, 403, any other non-200, an unparseable answer, a timeout) is denied. A public comment is shown through the issue document, which follows the next sync.
   - A role-restricted comment also needs its issue to be in the role's project still: `GET /rest/api/3/issue/{issueId}?fields=project` must answer the URI's project id. An issue moved to another project is denied until the next sync, because the same role name there is a different rule.
   - Up to 10 reads run at once, within the search's budget.
5. **Combine:**
   - Issue: browse.
   - Restricted comment: browse **and** membership **and** the live check.
   - Internal note: browse **and** agent **and** the live check, **and** membership if it also has `visibility`.
   - Attachment: browse, **and** its comment's rule if it is tied to one. A tied attachment's URI names its comment (`.../attachment/{a}/comment/{c}/...`), and that comment gets the same live check, plus the live-project check for a role. A deny-all attachment needs no read, and an untied one carries no restriction.
   - The sync records every restricted comment that has ever tied an attachment, in its own file in the source's state directory (`attachment-history.json`, by attachment id), apart from the issue records. A fetch that leaves an attachment out, an answer without the attachment field, an issue that drops out of the listing, attachments being switched off, or a resync does not lose it; it goes only with the source's whole state. A restricted comment that is listed but gone by the time its rendered body is read may have linked any of the issue's files, so it is recorded for every attachment of that issue, as is a comment showing a file Connapse cannot name.
   - An attachment with any history is deny-all until re-verified. Each cycle that fetches its issue (a change or the daily sweep) decides it afresh, and releases it only on positive evidence: exactly one comment has ever tied it, Jira returns that comment, its rendered body is read this cycle, its restriction is unchanged and readable, and it shows no file Connapse cannot name. If the comment is still restricted and links the file, the attachment takes its restriction. If it is now public, the attachment follows the issue, as Jira lifts the restriction too, but its URI still names the comment (`.../attachment/{a}/comment/{c}` with no restriction): search admits it to issue browsers only while a live read finds the comment still public, so a comment restricted again before the next sync hides the file at once. A file no comment ever tied keeps the plain URI and needs no read. Anything else keeps it deny-all: a missing comment, a failed read, an unnameable file reference, or a restricted comment that no longer links it. A file tied by more than one comment never releases, even when they agree, because its URI can name only one comment for the live check. Release changes the state; the history itself is never shortened.
   - State written before the history existed is upgraded at the start of the next sync, before any record changes. The history is seeded from every stored record's ties and deny-all flags, and from its attachment tombstones, as deny-all until re-verified. A legacy record kept at most its first tie, not the comment that made it deny-all, and a tombstone names no comment at all. Such an entry is marked unresolvable and stays deny-all for good, whatever later ties appear: the comment behind it may stay hidden from the service account forever, so a surviving tie proves nothing. A legacy single tie with no deny-all releases under the normal rule.

**Account guard.** `JiraPermissionChecker` refuses to send any request whose `accountId` is null, empty, whitespace or `anonymous`. Without `accountId`, Jira evaluates the calling service account, which fails fully open.

**Cache** (`IMemoryCache`):

| Answer | Key | Lifetime |
|---|---|---|
| Browse or agent | `(jira, cloudId, accountId, permission, issueId)` | 5 minutes |
| A user's groups | `(jira, cloudId, accountId)` | 5 minutes |
| Role name to id per project | `(jira, cloudId, projectId)` | 5 minutes |
| Role actors | `(jira, cloudId, projectId, roleId)` | 5 minutes |
| A comment's live visibility (not per user) | `(jira, cloudId, issueId, commentId)` | 1 minute |
| An issue's live project (not per user) | `(jira, cloudId, issueId)` | 1 minute |
| Any failure | — | 30 seconds, as deny |

Every key includes the product. Confluence's existing key gains it too, so ids can't collide.

**Fail-closed rules.** A hit is allowed only when every check it needs returned an explicit yes. Everything else denies:
- any non-200 response, a 429 or a timeout;
- budget exhaustion;
- an undecryptable secret or a failed token exchange;
- an unknown cloudId, or an unparseable URI;
- a missing document row or missing/invalid restriction metadata;
- a group id the user lacks, a group that no longer exists, or a role name that resolves to zero or several roles.

**Other surfaces.**
- Blazor, REST and MCP search already route through `HybridSearchService`.
- Direct document reads (MCP `get_document`, REST fetch by id or path) run the same verifier for Jira documents.
- Jira sources are admin-only in source listings.

## 4. Setup and site test

**Service account.** `docs/atlassian-setup.md` gains a Jira step:
- Give the service account Jira product access and the **Administer Jira** global permission, which `permissions/check` for another account, other users' groups and role actors all need.
- Add the Jira scopes below to its OAuth 2.0 credential. When a site serves both products, one credential carries both scope groups.
- **To index restricted comments, the service account must belong to every group and project role your team restricts comments to.** The API returns only comments the caller can see, so a comment hidden from the service account is never indexed (fail closed) and Connapse can't detect it. The guide states this in plain words.

**Jira granular scopes** (from the Jira v3 OpenAPI spec, 2026-10-12; all marked Beta there):
`read:application-role:jira`, `read:attachment:jira` (optional, attachments only), `read:audit-log:jira`, `read:avatar:jira`, `read:comment:jira`, `read:comment.property:jira`, `read:field:jira`, `read:field-configuration:jira`, `read:group:jira`, `read:issue:jira`, `read:issue-details:jira`, `read:issue-meta:jira`, `read:issue-security-level:jira`, `read:issue-type:jira`, `read:issue-type-hierarchy:jira`, `read:issue.changelog:jira`, `read:issue.vote:jira`, `read:permission:jira`, `read:project:jira`, `read:project-category:jira`, `read:project-role:jira`, `read:project-version:jira`, `read:project.component:jira`, `read:project.property:jira`, `read:status:jira`, `read:user:jira`.
The plan re-derives this list from the spec for the exact endpoint set, and the site test proves it.

**Site test: Jira probes** run after the Confluence probes, in order, naming the failing one:
1. `GET /rest/api/3/myself`, the credential works for Jira;
2. `GET /rest/api/3/project/search?maxResults=1`, it can see projects;
3. one `search/jql`, one `bulkfetch` and one issue's comments, it can read content;
4. **admin probe:** `permissions/check` for another account, where 403 means not Administer Jira;
5. **membership probes:** `/user/groups` for another account and one project's role actors.

Jira passes only when all five pass. Troubleshooting anchors follow the Confluence pattern (`cant-read-jira`, `not-jira-administrator`, `cant-read-jira-membership`).

**Connections page.**
- The site card shows per-product status.
- The scope list has a Confluence group and a Jira group, each with its own copy button.
- `AtlassianSiteScopes` gains a product tag per scope.

**New source.**
- When the chosen site passed both products, a product choice comes first.
- The Jira form multi-selects live projects the service account can browse (`project/search?action=browse&status=live`).
- It sets the attachments toggle (off when the last test didn't confirm `read:attachment:jira`) and the size cap.
- It creates one source per project, and rejects duplicates as the Confluence form does.

## 5. Testing

- **`FakeJira`** (partial of `FakeAtlassianApi`, routed on `^/ex/jira/[^/]+/rest/api/3/`), stateful:
  - projects, issues with `updated`, comments with `visibility`/`jsdPublic`/`renderedBody`, attachments;
  - JQL id paging with `nextPageToken`, the `updated >=` filter, and bulkfetch;
  - per-account browse and agent tables, groups per account, role actors per project;
  - switches for 429, 5xx, timeouts and "not admin".
- **`AdfRenderer` unit tests:** one fixture per table row, plus unknown nodes and marks.
- **Sync integration tests** (`SharedWebAppFixture`):
  - first sync, edit, delete, move to another project;
  - a public comment lands in the issue document;
  - a restricted comment and an internal note each become their own document;
  - a restriction change moves a comment between homes;
  - an attachment linked from a restricted comment gets its restriction;
  - oversized and unsupported attachments are skipped and counted;
  - `includeAttachments` off removes existing attachments;
  - the deletion guard trips on mass disappearance;
  - the breadcrumb appears in chunks;
  - the epoch-millisecond JQL bound is sent unquoted.
- **Enforcement integration tests:**
  - browse allowed/denied, and an issue absent from the answer is denied;
  - 1,001 issues split into two calls;
  - the null/empty/`anonymous` accountId guard;
  - group member and non-member, with matching by id after a rename and a deleted group;
  - a role reached directly, a role reached through a group, an ambiguous role name;
  - an internal note for an agent and for a customer;
  - a restricted attachment;
  - 403, 429, timeout and budget exhaustion;
  - direct document read;
  - unlinked user (no API call);
  - Confluence regression under the dispatch.
- **bUnit tests:** the product choice, the Jira project picker, and per-product site status.
- **Live test-site checklist (before release).** Add Jira (free tier) to the `connapse-test` site, and seed projects, restricted comments, an internal note and attachments with a script beside `eval/tools/seed_confluence.py`.
  1. Can a service account with Administer Jira and client credentials read other users' groups and role actors?
  2. What does a role restriction's `visibility.identifier` hold, and how do team-managed project roles resolve?
  3. Does the comment author see their own restricted comment? This only affects how much stricter Connapse is.
  4. Does changing a comment's visibility bump the issue's `updated`? If yes, disable the daily comment sweep.
  5. Does the service account receive attachments from restricted comments it can't see, in `fields.attachment`? If yes, file a fix to skip such issues' untied attachments before release.
  6. Does `SERVICEDESK_AGENT` deny a portal customer?
  7. Record the rate-limit headers on Jira client-credentials traffic.

## 6. Delivery order

Phase PRs target `epic/540-jira`. The implementation plan sizes them.

1. **Open shared seams** with no behaviour change: the API client's product, `AtlassianUri` product parsing, verifier dispatch, and the product in cache keys.
2. **Site test and setup:** the Jira probes, per-product health, the scope groups on the Connections page, and the setup guide.
3. **`AdfRenderer`.**
4. **Jira sync:** the connector, the state store, `JiraRecordRenderer`, `JiraSourceScope`, and the New-source product choice and project picker.
5. **Restricted comments and internal notes** as their own documents, plus the daily comment sweep.
6. **Enforcement:** the bulk check, membership lookups, the agent check, and direct reads.
7. **Attachments**, including their tie to restricted comments.
8. **Live-site validation** against the checklist, then epic to main.

## Open questions (resolved on the test site, not by design)

These are the checklist items 1–6 above. If item 1 fails for role actors, role-restricted comments stay indexed but deny for everyone (fail closed), and a follow-up evaluates Forge impersonation for them alone.
