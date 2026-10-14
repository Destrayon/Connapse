# A generic custom source protocol: customer-built middle-man servers that supply content and live per-user permissions
**Date:** 2026-10-14
**Status:** Reviewed (inline)
**Built on:** per-user-search-permission-design-2026-08-27.md (identity join-key findings), azure-blob-connector-parity-2026-09-08.md (no reverse-ACL API precedent). Connapse MCP was not available in this session.

## Executive summary

A custom source protocol that works for Slack, Discord, S3, Azure Blob, Jira, Confluence, Linear and similar systems is feasible as a small REST+JSON protocol with three halves: **content sync** (discover scopes, list, change feed, fetch bytes), **live permissions** expressed as a strict profile of the OpenID **AuthZEN 1.0** authorization API (Final, January 2026), and **discovery** (a well-known metadata document declaring capabilities). The permission design that satisfies "live at search time", "efficient" and "parity, fail closed" at once is **two-phase**: the middle man returns a condensed, cacheable list of **opaque access keys** the user holds, which Connapse applies as a Postgres pre-filter, and then Connapse sends the final ranked candidates to a **batched live check** that is authoritative. Because the final check decides, the condensed list may be cached and slightly stale without ever over-granting — staleness only costs recall. Path prefixes are not needed as a separate grant shape: every system studied fits opaque keys, and prefixes are a special case of them. The one unavoidable trade-off is that each document carries its keys from sync time (which channel, project or ACL it belongs to); what is live is which keys a user holds and the final per-document check. No shipping product does live document-level permission resolution against a customer-run endpoint while keeping an index, so this combination is Connapse's own design, assembled from well-documented parts.

## Research brief

**Question:** What protocol should a customer-built middle-man server implement so that Connapse can sync content from, and resolve live per-user permissions against, any system — efficiently and simply?

**Sub-questions investigated:**
1. How do enterprise search products attach permissions to documents for custom connectors, and does any resolve them live at query time?
2. Which techniques make live "what can user U access" answers cheap, and how do they combine with vector and keyword retrieval?
3. Can one condensed grant shape (path prefixes, opaque keys, or both) express each real system's permission model with parity?
4. What is the best prior art for a pull-based third-party content sync protocol?
5. Which transport, auth, versioning, error model and conformance testing should the protocol use, and how is the searching user identified?

**Out of scope:** building middle-man servers (customers do that); write access to sources; sync-time ACL capture with cached membership (rejected by the product owner); federated search without an index.

**Success criteria:** answers to the open design questions (grant shape, user identity, live-check efficiency, sync semantics, transport) with a recommended protocol sketch.

## Findings by sub-question

### 1. No enterprise search product resolves document permissions live from a customer endpoint while keeping an index

Every indexed product studied — Google Cloud Search, Microsoft Graph/Copilot connectors, Glean, Elastic connectors, Coveo, Amazon Kendra/Q Business, Vertex AI Search, Algolia — stores principals on the document at sync time ("early binding") [primary docs for all except Coveo, whose pages were unreachable]. Some resolve the *searcher's* side live: Azure AI Search's query-time ACL enforcement (preview) resolves the user's Entra groups through Graph per query, and on failure returns 5xx rather than partially filtered results — a citable fail-closed precedent. Coveo resolves a new user's identities during their first query. The only fully live designs, Microsoft federated connectors and Gemini Enterprise federated search, drop the index entirely and have the source run the search as the user.

How products condense grants: group indirection (Graph `externalGroups`, Glean groups, Coveo identity cache), ACL inheritance by reference (Google Cloud Search `inheritFrom` with `CHILD_OVERRIDE`/`PARENT_OVERRIDE`/`BOTH_PERMIT`), and group intersections (Glean `allowedGroupIntersections`). Every vendor warns against flattening group membership into per-item user lists.

Recorded fail-open shapes to avoid: Elastic treats a missing `_allow_access_control` as public; Kendra returns everything when no user context is sent; Azure AI Search may not enforce ACL entries beyond source per-item limits.

Late binding (checking each hit against the source after retrieval) is criticised by Sinequa (vendor marketing) for slowness on broad queries, broken result counts and facets, and leakage through autosuggest; ServiceNow uses it as a fallback. A patent describes "early followed by late binding", which is the shape Connapse already uses for Atlassian (prefix pre-filter, then `AtlassianSearchResultVerifier`).

### 2. Live permission checks are cheap when split into a condensed pre-filter and one batched check of the final candidates

Every relationship-based authorization engine documents the same split between "list everything a user can see" (expensive) and "check these N candidates" (cheap):
- **Zanzibar** (USENIX ATC 2019): a check takes ~3 ms p50 / 15 ms p99; search clients issue "tens to hundreds of checks" per results page; Expand runs ~10x Check's p99.
- **SpiceDB**: LookupResources is a cursored "heavy request"; the docs advise pre-filtering with it only up to ~10k results, then post-filtering with CheckBulkPermissions.
- **OpenFGA**: ListObjects defaults to a 3 s deadline and 1,000 results and silently truncates; its search-with-permissions guide recommends "list IDs then search" only below ~1,000 accessible objects, "search then check" otherwise, plus a live check on the final set to catch revocations.

Policy engines (OPA partial evaluation, Oso `list_local`, Cedar residuals) return a structured predicate rather than IDs. An OPA SQL-injection fix in 1.19.0 is the reason Connapse must compile any condensed grant into parameterised SQL itself and never accept filter text from the middle man.

Search side: pgvector applies filters after the approximate index scan, so a filter matching 10% of rows with `hnsw.ef_search=40` returns about 4 rows — the recall cliff. pgvector 0.8 iterative scans (`ivfflat.iterative_scan = relaxed_order`, bounded by `ivfflat.max_probes`) fix this for narrow grants. Post-filtering RAG retrievers (LangChain SpiceDB, Auth0 FGARetriever) over-fetch 2–3x and drop candidates whose check errors.

Consistency: Zanzibar and SpiceDB accept seconds of staleness by default and name the "new enemy" risk. In the two-phase design that risk disappears from the pre-filter, because the final check is live and authoritative.

### 3. Opaque access keys express every system studied; prefixes are a special case, and fine-grained exceptions need the live check

Per-system stress test (primary docs unless noted):

| System | Natural key | Exceptions finer than the key | Verdict |
|---|---|---|---|
| Slack | channel, DM, plus a `workspace-member` key excluding guests | none below channel | keys; private channels need the user's own token |
| Discord | channel, private thread | denies resolve per channel per member | keys; overwrites computed by the middle man |
| Jira Cloud | composite project + security level | reporter/assignee/custom-field grants; restricted comments | composite keys + live check; comments as separate documents |
| Confluence | space + restriction-chain fingerprint | inherited page restrictions narrow access | composite/fingerprint keys + live check |
| Linear | team | Enterprise issue sharing (rare) | keys + live check |
| S3 | bucket/prefix grant | explicit deny on sub-prefix; IP/time conditions | keys; conditional denies excluded (fail closed) |
| Azure ADLS Gen2 | effective-ACL fingerprint | per-file ACLs, first-match implicit deny, mask | fingerprint keys |
| Google Drive | shared drive / folder | per-file sharing is common; link sharing | keys + heavy live check |
| SharePoint/OneDrive | site + each unique-permission scope | sharing links, broken inheritance | keys + live check |
| GitHub | repository | none | keys |
| Notion | page subtree | — | not implementable: no API reports what a user can access |

Two techniques make keys universal. **Composite keys**: where access needs two conditions at once (Jira project AND security level; Confluence space AND every restriction above a page), the middle man emits one key per combination and grants it to a user only when they satisfy both — the conjunction is resolved by the middle man, so Connapse only needs "any one key matches". **ACL fingerprints**: the middle man hashes a document's normalised effective ACL into a key, so documents with identical ACLs share one key (ADLS, SharePoint unique scopes, Confluence restriction chains). This stays condensed while distinct ACLs are far fewer than documents; where they are not (heavily shared Drive or OneDrive), the live check keeps correctness. Denies are harmless at key granularity because grants are computed per user; a deny finer than a key must be handled by the middle man splitting keys or excluding the document.

### 4. Content sync should be pull-based with a required full listing and an optional change feed

Ten rules from Microsoft Graph delta, Google Drive changes, Airbyte, Fivetran, Glean bulk indexing and Dropbox:
1. Cursors are opaque; the server owns their contents (Graph, Airbyte).
2. A page says either "more pages" or "pass complete, here is the next cursor", never both (Graph `nextLink`/`deltaLink`, Drive). Connapse saves the cursor only when a pass completes.
3. The change cursor is taken before the full listing starts, so changes during listing are not lost (Drive `getStartPageToken`).
4. Deletes are explicit tombstones in the change feed; "updated since" feeds cannot see deletes (Airbyte, Jira JQL), so servers without tombstones declare it and Connapse schedules full listings and compares them against what it has indexed.
5. Listing-diff deletions apply only after a completed listing and behind a threshold guard (Glean pauses when more than 20% would be deleted) — Connapse's existing `IsFullListing` deletion guard.
6. "Start over" is a first-class signal (Graph 410 Gone, Dropbox reset) mapped to `RequiresFullResync`.
7. Operations are idempotent, items may repeat, and the last version wins; each document has a stable source-chosen id.
8. Metadata carries a content `version` (ETag or hash) separate from bytes, so unchanged documents skip download and re-embedding. (Inference.)
9. 429 + `Retry-After` maps to Connapse's existing `Deferred` result; the IETF `RateLimit` headers are still a draft (-11).
10. Pull for content; webhooks are only hints to sync sooner (Drive `changes.watch`, Graph). Pull keeps scheduling, guards and retries in Connapse, so a middle man is a stateless query server.

Added for permissions: a document's access keys must be versioned separately from its content, so a permission move (page restricted, issue moved between security levels) updates keys without re-embedding.

### 5. Transport: REST+JSON, AuthZEN for permissions, well-known capabilities, problem details, a conformance CLI

- **AuthZEN 1.0 is a Final OpenID specification** (approved 12 January 2026). HTTPS POST + JSON; `/access/v1/evaluation`, `/access/v1/evaluations` (batch, results in request order), and `/access/v1/search/resource` ("which resources can subject S act on") with opaque `page.token`/`next_token` pagination. The two permission phases map onto it directly: resource search over resources of type `access_key` returns the user's keys; batch evaluation is the final check. Existing AuthZEN servers can then serve as middle-man permission backends.
- **MCP is a poor fit** for this machine-to-machine protocol: no delta or tombstone semantics, no authorization queries, machine-to-machine auth only as an opt-in extension, and breaking revisions (the 2026-07-28 revision removed `initialize`).
- **Auth**: a high-entropy bearer token over TLS is the minimum, with OAuth 2.0 client credentials and mTLS optional. The user travels as the AuthZEN `subject` inside a mutually authenticated call; a signed user assertion (ID-JAG, still IETF draft -04) is reserved for later.
- **Versioning**: `v1` in the path, additive-only changes, unknown fields ignored, and a metadata document listing endpoints and capabilities, where an absent capability means unsupported (AuthZEN metadata, SCIM `ServiceProviderConfig`).
- **Errors**: RFC 9457 problem details with a `retryable` member; 429/503 with `Retry-After`. AuthZEN reports a failed batch item as `decision:false` with `context.error`; Connapse must treat anything other than an explicit `decision:true` as no access.
- **Conformance**: Airbyte's Connector Acceptance Tests are the model — capability-gated tests run against a live endpoint. AuthZEN ran interop rounds with 9–11 implementations.
- **User identity**: AuthZEN subject `{type, id, properties}`; SCIM separates server `id` from client `externalId`; Kendra, Q and Glean join on email; Google and Q use an explicit external-id mapping. The mapping belongs to the side that knows the target system.

## Recommended protocol

**Model.** A Connection holds the middle man's base URL and credential (admin-only, as today). The middle man advertises scopes; each scope an admin selects becomes a Source with its own sync state.

**Discovery.** `GET /.well-known/connapse-source` → `{ protocol_versions, endpoints, capabilities: { changes, tombstones, permissions: "none"|"check"|"keys+check", max_page_size, max_batch } }`. Absent means unsupported.

**Content (per scope).**
- `GET /v1/scopes` — the scopes it exposes, each `{ id, name, description }`.
- `GET /v1/scopes/{scope}/documents?page_token=` — full listing; items `{ id, path, title, content_type, size, version, modified_at, url, access_keys[], access_version }`; the last page carries `change_cursor`.
- `GET /v1/scopes/{scope}/changes?cursor=&page_token=` — optional; items `{ id, op: "upsert"|"delete", ...metadata }`; `410` means full resync, `429` means deferred.
- `GET /v1/scopes/{scope}/documents/{id}/content` — streamed bytes with `ETag`; `If-None-Match` → `304`; `404`/`410` → delete.

**Permissions (AuthZEN profile).**
- Phase 1, condensed grants: `POST /access/v1/search/resource` with the subject, action `read`, resource type `access_key` and the scope → the keys the user holds, paginated. Connapse caches the result per user and scope with an ETag and revalidates; staleness here can only change which candidates are considered, never what is shown.
- Phase 2, authoritative check: `POST /access/v1/evaluations` with the subject and the final candidate documents (over-fetched 2–3x, one refill) → only `decision:true` survives. A timeout, error or missing decision drops the candidate and shows a "results may be incomplete" notice.
- Simplest conforming server: `permissions: "check"` only. Connapse then post-filters with over-fetch — correct but lower recall for users who see little. `keys+check` adds the pre-filter.

**Identity.** Subject `{ type: "user", id: <stable Connapse user id>, properties: { email, email_verified, iss, sub } }`. The middle man maps these to its own system's identities; where it needs the user's own token (Slack private channels, Drive), it runs its own account-linking flow keyed by the Connapse user id, so Connapse never holds source credentials.

**Connapse side.** Store `access_keys text[]` per document with a GIN index; the pre-filter is `access_keys && @keys` compiled as a parameter, alongside the existing resource-URI prefix scopes. Enable `ivfflat.iterative_scan = relaxed_order` so narrow key sets do not hit the recall cliff. A document with no keys under a `keys+check` source matches no one.

**Ship with the spec:** an OpenAPI 3.1 document, a conformance CLI run against a URL (discovery, auth rejection, pagination stability, change round-trip, AuthZEN evaluation/batch/search, fail-closed under injected errors, `Retry-After`), and a small example server for testing, not a product adapter.

## Conflicts and uncertainties

- **Sync-time keys versus "live only".** The product owner rejected sync-time permission capture. Any condensed grant, prefix or key, requires knowing at sync which grouping a document belongs to; the alternative is federated search with no index. The recommendation keeps grouping at sync time and makes both who-holds-which-key and the final per-document decision live. This needs the owner's explicit agreement.
- **Cached grants.** The transport research says never serve stale grants; the authorization research says bounded staleness is standard. Resolved by making the final check authoritative, so cached keys only narrow candidates. If a deployment disables the final check for speed, cached keys become a real staleness window and the cache TTL must be seconds.
- **Late binding costs.** Sinequa's objections (counts, facets, autosuggest leakage) apply to the final check. Connapse has no facets or autosuggest over source content today; result counts must be reported after the check.
- **Jira parity trap.** Granting Browse to Reporter/Assignee makes a project visible to every logged-in user (Atlassian JRACLOUD-37117, Won't Fix). A middle man copying the scheme reproduces Jira's behaviour, which is parity but surprising.

## Gaps — what we did not find

- No published specification for "condensed grants plus a batched exceptions check"; this is Connapse's own composition.
- Jira `permissions/check` request limits; whether Confluence's permission-check API resolves inherited restrictions.
- Whether AuthZEN's certification profile has published resource-search results.
- Coveo documentation was unreachable (503); Coveo claims rest on search excerpts.
- Slack Enterprise admin APIs and the S3 object-ACL default were not verified.
- Notion's lack of a per-user permission API rests on documentation silence.

## Source quality assessment

Most load-bearing claims rest on primary sources: the AuthZEN 1.0 Final spec, Zanzibar paper, SpiceDB and OpenFGA docs, pgvector README, Microsoft Graph and Google Drive API docs, and each system's permission documentation. Secondary sources: the MCP changelog mirror, AuthZEN interop retrospective, Sinequa's late-binding argument (vendor marketing), Pinecone/Auth0 RAG posts. Tertiary: Dropbox reset semantics (forum staff) and one OpenFGA GitHub discussion. The two-phase recommendation and the access-version split are inferences from primary material, labelled as such.

## Sources

**Primary:** openid.net/specs/authorization-api-1_0.html · openid.net/authorization-api-1-0-final-specification-approved · openid.net/specs/openid-sharedsignals-framework-1_0-final.html · usenix.org/system/files/atc19-pang.pdf · authzed.com/docs/spicedb/concepts/querying-data · authzed.com/docs/spicedb/concepts/consistency · openfga.dev/docs/interacting/relationship-queries · openfga.dev/docs/interacting/search-with-permissions · openfga.dev/docs/modeling/agents/rag-authorization · openpolicyagent.org/docs/filtering · osohq.com/docs/develop/enforce/list-filtering · docs.permify.co/api-reference/permission/lookup-entity · github.com/pgvector/pgvector · developers.google.com/workspace/cloud-search/docs/guides/acls · developers.google.com/workspace/cloud-search/docs/guides/identity-connector · learn.microsoft.com/en-us/graph/api/resources/externalconnectors-acl · learn.microsoft.com/en-us/graph/connecting-external-content-manage-items · learn.microsoft.com/en-us/microsoft-365/copilot/connectors/federated-connectors-overview · learn.microsoft.com/en-us/azure/search/search-query-access-control-rbac-enforcement · developers.glean.com/api-info/indexing/documents/permissions · developers.glean.com/api-info/indexing/documents/bulk-upload-model · elastic.co/docs/reference/search-connectors/es-dls-overview · docs.aws.amazon.com/kendra/latest/dg/user-context-filter.html · docs.aws.amazon.com/amazonq/latest/qbusiness-ug/connector-principal-store.html · docs.cloud.google.com/generative-ai-app-builder/docs/data-source-access-control · algolia.com/doc/guides/security/api-keys/how-to/user-restricted-access-to-data · learn.microsoft.com/en-us/graph/delta-query-overview · developers.google.com/workspace/drive/api/guides/manage-changes · docs.airbyte.com/understanding-airbyte/airbyte-protocol · fivetran.com/docs/connector-sdk/technical-reference/connector-sdk-operations · datatracker.ietf.org/doc/draft-ietf-httpapi-ratelimit-headers · modelcontextprotocol.io/specification/2025-11-25/server/utilities/pagination · modelcontextprotocol.io/extensions/auth/overview · datatracker.ietf.org/doc/html/draft-ietf-oauth-identity-assertion-authz-grant-03 · docs.slack.dev/reference/methods/users.conversations · docs.discord.com/developers/topics/permissions · developer.atlassian.com/cloud/jira/platform/rest/v3/api-group-permissions · jira.atlassian.com/browse/JRACLOUD-37117 · confluence.atlassian.com/confcloud/page-restrictions-724764891.html · developer.atlassian.com/cloud/confluence/rest/v1/api-group-content-permissions · linear.app/docs/private-teams · docs.aws.amazon.com/AmazonS3/latest/API/API_control_ListCallerAccessGrants.html · learn.microsoft.com/en-us/azure/storage/blobs/data-lake-storage-access-control · developers.google.com/workspace/drive/api/guides/manage-sharing · learn.microsoft.com/en-us/sharepoint/what-is-permissions-inheritance · docs.github.com (repository roles) · developers.notion.com/docs/authorization

**Secondary:** modelcontextprotocol.net changelog mirror · aserto.com/blog/openid-authzen-one-year-in-a-retrospective · pinecone.io/learn/rag-access-control · auth0.com/blog/building-a-secure-rag-with-python-langchain-and-openfga · sinequa.com (late binding) · docs.coveo.com excerpts · docs.airbyte.com/platform/operator-guides/refreshes

**Tertiary:** Dropbox forum (reset semantics) · OpenFGA GitHub discussion #105

## Addendum (same day): content-only mode and middle-man-owned account links

**Content-only sources are an established, admin-chosen setting.** Coveo offers a per-source choice of "Same users and groups as in your content system", "Everyone" or "Specific users and groups" (docs.coveo.com/en/1779, read via excerpts). Amazon Q Business lets ACLs be disabled only at data source creation, behind an extra IAM permission, and once ACL crawling is on it cannot be turned off (docs.aws.amazon.com/amazonq/latest/qbusiness-ug/connector-concepts.html). Microsoft Graph connectors require an ACL on every item, so "public" must be written explicitly as `everyone` (learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalitem). Amazon Kendra ("Documents without an ACL are public") and Elastic connectors (no access control fields means no restrictions) infer public from a missing field, the fail-open shape to avoid. No product derives the mode from what a connector advertises.

**The OAuth client holds the target-system tokens.** Alexa and Google account linking store tokens on the platform because the platform is the OAuth client; MCP says a server calling upstream APIs acts as its own OAuth client and must not pass tokens through; LTI 1.3 launches a tool with the platform's user id as `sub`, and the tool keeps the mapping. A middle man that performs the target-system OAuth should therefore hold the link and tokens itself.

**A launch token alone allows an account-binding swap.** LTI 1.3 (imsglobal.org/spec/security/v1p0, sections 5.1.1 and 7.3.1) round-trips through the platform so it can check the logged-in user before issuing the id_token, and returns the token by form POST. RFC 9700 sections 4.2 and 4.3 describe tokens in URLs leaking through Referer headers and browser history. Atlassian Connect deprecated query-string JWTs. A design where Connapse only hands the middle man a signed token lets an attacker forward an unopened link to a victim who completes it, so the link must be confirmed against the Connapse session before it activates.
