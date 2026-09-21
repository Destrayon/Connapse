# How should Connapse implement the GitHub connector: connection-less public sources, GitHub App private sources, per-object permission filtering, a record-shaped interface, and graph edges?
**Date:** 2026-09-09
**Status:** Reviewed
**Built on:** next-connectors-rag-shape-graphrag-2026-09-09.md (which chose GitHub first and scoped it to issues, pull requests, discussions, and markdown docs, leaving code for the GraphRAG phase); connector-source-split epic #348 (the Connection/Source/Container model this builds on)

## Executive summary

A GitHub connector fits Connapse's existing model with two schema changes and one new interface. First, make a Source's connection optional so a public repository can be a Source with no credential behind it, its safety resting on a per-sync visibility re-check rather than on a connection boundary. Second, authenticate private repositories through a customer-registered GitHub App whose private key is delivered to Connapse automatically by the App manifest flow, never pasted, exchanged for one-hour installation tokens, which matches the owner's no-stored-long-lived-credential rule exactly as AWS IAM Roles Anywhere does. Third, add a record-shaped sibling to the file-shaped connector interface, because an issue or pull request is assembled from several API calls and has no byte stream. Per-object permission filtering reduces to one effective-permission call per distinct repository in a search's hit set, evaluated live at query time and failing closed, because the repository is GitHub's own permission unit and one endpoint already aggregates repo, team, organization, and enterprise grants.

Two findings are load-bearing and not settled by documentation alone. One, the unauthenticated 304 behavior, was confirmed by a spike on 2026-09-09 (a 304 did decrement the 60-per-hour cap); the other still needs a smoke test before the design is locked. The GitHub docs are internally inconsistent about whether the collaborator-permission endpoint needs only `metadata:read` or actually `push` access when called with an installation token; if push is truly required, the read-only posture breaks. And an unauthenticated conditional request that returns 304 still counts against the 60-requests-per-hour public cap, so ETags do not conserve quota for a credential-less public source. The practical consequence of the second is that public-repo docs should sync by git clone, which has no API cap, and issue and pull-request sync for a public repo runs far better through an installed App token than unauthenticated.

## Research brief

**Question:** How should Connapse implement a GitHub connector that indexes issues, pull requests, discussions, and markdown docs, with connection-less public sources, GitHub App authentication for private sources, per-user identity linking, per-object fail-closed permission filtering, a record-shaped connector interface, and graph edges captured for a later GraphRAG phase?

**Sub-questions investigated:**
1. Authentication and identity: GitHub App registration for a self-hosted vendor, installation-token lifecycle, per-user identity linking, rate limits, Enterprise Server parity, Octokit.NET support.
2. Record modeling: how production systems turn an issue, pull request, or discussion into a retrievable document; the synthetic path scheme; rendering; which graph edges to store; the record chunker.
3. Incremental sync: per-kind delta APIs, cursor design, deletion and visibility detection, conditional requests, webhooks versus polling.
4. Per-object permission enforcement: the per-repo read check, batching, identity-mapping traps, the fail-closed decision table, freshness.
5. How all of this maps onto Connapse's existing `IConnector`, `ISyncCursorConnector`, `IConnectorFactory`, `Source`, and `Connection` types.

**Out of scope:** indexing repository code (deferred to the GraphRAG phase, per the connector-choice report); GitLab and Gitea (later connectors sharing the record interface); the Atlassian connector; GraphRAG retrieval itself.

**Success criteria:** a decision-ready implementation design with the concrete endpoints, the schema and interface changes, the fail-closed rules, and an explicit list of what must be verified empirically before building.

## Findings by sub-question

### Sub-question 1: Authentication and per-user identity

This section claims that the GitHub App manifest flow lets each self-hosting customer register their own App with zero pasted secrets, that installation tokens are the short-lived-credential equivalent of AWS Roles Anywhere, and that an installation token also lifts the public-repo rate cap.

The **App manifest flow** solves the self-hosted-vendor problem cleanly (primary, GitHub docs; high confidence). Connapse cannot ship one shared App, because each customer needs their own. Instead Connapse renders a form that POSTs a JSON manifest (name, redirect URL, webhook URL, requested read permissions) to GitHub's App-creation URL; the admin clicks "Create GitHub App"; GitHub redirects back with a temporary code valid one hour; Connapse POSTs `/app-manifests/{code}/conversions` and receives, in one response, the App id, client id, client secret, webhook secret, and the RSA private key (PEM). The admin pastes nothing. Store the PEM encrypted on the host, exactly as the AWS Roles Anywhere key and the SFTP key are stored today. Keep a manual fallback (register the App by hand, upload the PEM) for air-gapped installs.

**Installation-token lifecycle** (primary; high): sign a JWT with the App private key, RS256, ten-minute maximum expiry; exchange it at `POST /app/installations/{id}/access_tokens` for an installation token that lives one hour and can be scoped to specific repository ids (max 500) and a subset of the App's permissions. The App should request read on metadata (mandatory), contents, issues, pull requests, and discussions, plus organization members read for identity and enterprise-membership checks.

**Per-user identity linking** uses the same App's user-to-server OAuth web flow (primary; high). The Connapse user is sent to GitHub's authorize URL, returns with a code, and Connapse exchanges it for a user token, calls `GET /user`, and stores only the mapping from Connapse user to GitHub numeric id and login. Discard the user token; Connapse keeps no GitHub credential for the user, only an opaque identity link, which matches how AWS identity linking already works. For customers on SAML SSO, the organization `externalIdentities` GraphQL query maps logins to SAML NameId and can auto-link by corporate email; offer it as an enterprise convenience, with OAuth as the universal path.

**Rate limits, the critical answer** (primary, single passage; high but smoke-test): unauthenticated REST is 60 requests per hour per IP; an installation token is at least 5,000 per hour, 15,000 for an App owned by an Enterprise Cloud org. The documentation states the higher limit applies to all operations the token can perform including reading public repositories, so when a customer has installed the App, Connapse should route even public-repo reads through the installation token to escape the 60-per-hour cap. Only App-less customers are stuck at 60 for public repos.

**Enterprise Server parity** (primary; high): the manifest flow, installation tokens, and user-to-server OAuth all work on GitHub Enterprise Server, with the base URL at `/api/v3` for REST and `/api/graphql` for GraphQL; the Enterprise-Cloud-only 15,000 limit does not apply.

**Octokit.NET** (primary; high) supports installation-token creation and a custom base address for Enterprise Server, but deliberately will not sign the App JWT, so Connapse must sign RS256 itself with the .NET token libraries or the small `GitHubJwt` helper. GraphQL is a separate, thinly maintained `Octokit.GraphQL` 0.4.0-beta; because discussions and the richest cross-reference edges are GraphQL-only, hand-rolling GraphQL POSTs to `/graphql` with the installation token is the safer choice than taking the beta dependency.

### Sub-question 2: Modeling issues, pull requests, and discussions as documents

This section claims that production connectors treat one record as one document with a field header, that GitHub content is already markdown so rendering is light, and that the record chunker should emit one record as one chunk unless oversized.

**Prior art converges** (primary, connector source code; high). Onyx, LlamaIndex, and Langchain all model one issue or pull request as one document: the title goes to a metadata or semantic-identifier field, the body is the text, and rich metadata is attached (state, author, labels, timestamps, and pull-request-only fields like merged, merged_by, files-changed count). Notably all three omit comment bodies from the indexed text. Because Connapse specifically wants to test whether including comments helps, the recommended assembly is a superset: a field-header block (title, author, state, labels, created and closed dates, linked references), then the body, then the comment thread joined with a per-comment delimiter carrying author and date. That superset is exactly what makes the comment-inclusion experiment possible.

**Synthetic path** (primary; high): give each record a virtual path mirroring GitHub's own URL structure, `/issues/{number}`, `/pulls/{number}`, `/discussions/{number}`, and use the real repo-relative path for docs. The number is stable across title edits, and the true html_url lives in metadata as the citation link.

**Rendering is light** (primary, Markdig and GitHub docs; high). Unlike Atlassian's ADF, GitHub bodies and comments are already GitHub-Flavored Markdown, so there is no structural conversion. Keep raw markdown as the embedded text so the existing DocumentAware chunker can still exploit headings and tables; use Markdig only as an optional local normalizer, and avoid GitHub's `POST /markdown` HTML render because it costs an API call per body.

**Graph edges to store now** (primary, GitHub REST and GraphQL docs; high), as record metadata so a future GraphRAG phase needs no re-crawl:

| Edge | Field or endpoint | API |
|---|---|---|
| Pull request closes issue | closingIssuesReferences | GraphQL |
| Issue closed by pull request or commit | closed event commit_id; issue closed_by | REST |
| Cross-reference or mention | cross-referenced timeline event | REST timeline |
| Explicit link or unlink | connected / disconnected events | REST |
| Sub-issue parent and child | sub-issues endpoints (GA April 2025) | REST |
| Pull request touches files | pulls/{n}/files | REST |
| Labels, milestone | issue fields | REST |

The highest-value edges are closingIssuesReferences and cross-referenced, and both are GraphQL or timeline calls, reinforcing the hand-rolled-GraphQL decision.

**Record chunker** (primary, SeCom ICLR 2025; medium): emit one record as one document with the field header, and split only when it exceeds the token budget. When oversized, split into per-comment child chunks each prefixed with the parent title, number, and field header so every child is self-contained. No boundary-detection sophistication is warranted, because most records fit in one chunk and the prior connector-choice report already found no strong ablation favoring clever ticket boundaries. SeCom is the closest evidence: for long threads, grouping topically coherent turns beats both fixed-size and per-turn, which supports whole-thread-as-one-document until size forces a split.

### Sub-question 3: Incremental sync, cursors, and deletion detection

This section claims that issues and pull requests sync through the issues endpoint's since filter, discussions through GraphQL, and docs through git compare, and that hard deletes need a periodic full re-list.

**Per-kind sync** (primary, GitHub docs; high). The issues endpoint filters by last-updated with `since`, sorts by updated ascending, pages at 100, and returns pull requests too (each carrying a pull_request field), so one paged sweep covers both. The pulls endpoint has no since parameter, so pull-request-only metadata (merge state, changed files, head and base SHAs) is hydrated per pull request found through the issues sweep, and pull-request review comments have their own since-capable endpoint. Discussions are GraphQL-only, paged by updatedAt with an endCursor. Markdown docs sync best through the compare endpoint between the last-synced commit and head, whose files array marks each file added, removed, modified, or renamed, giving free deletion detection; the recursive Trees API is for the initial full listing only.

**Cursor design** (design plus primary; high on APIs, medium on the tie fix): the opaque cursor holds a last-updated timestamp for issues and pull requests, an updatedAt plus node cursor for discussions, and a commit SHA for docs. The one pitfall is timestamp ties, several records in the same second straddling a page boundary; the standard fix is to treat the boundary as greater-than-or-equal and dedup by id, or store the last id alongside the timestamp. Connapse's existing `SyncDelta.RequiresFullResync` flag is the right channel for a provider that says start over.

**Deletion, transfer, visibility** (primary; high except where noted). A since-based sweep cannot see a hard-deleted or transferred issue; it simply stops appearing, so periodic full re-listing (page all with state=all, diff the id set) is needed to reconcile, or accept drift between sweeps. Docs get exact deletions from compare. A renamed or transferred repository keeps a stable numeric id and GitHub redirects the old name, so key everything on the repo id, not its full name. The public-to-private flip shows up as an unauthenticated repository GET returning 404, which is the public-source safety re-check, though the exact 404-on-transition is inferred from GitHub's general privacy behavior rather than a single quoted line.

**Conditional requests, the second load-bearing caveat** (primary plus one community source; medium): GitHub supports ETag and If-Modified-Since, and a 304 is exempt from the rate limit only when the request was authenticated. For an unauthenticated public source, a 304 still decrements the 60-per-hour budget. So ETags conserve quota only once an App token is used, and a credential-less public source cannot poll its way around the cap; git clone for docs (no API cost) and App-token routing for issues are the real mitigations. Confirmed by a spike on 2026-09-09: an unauthenticated 304 decremented the remaining count (56 to 55), so this is settled, not inferred.

**Webhooks versus polling** (primary architectural; high): webhooks need inbound reachability a self-hosted instance behind a firewall usually lacks, so scheduled polling with cursors is the correct default, with webhooks an opt-in for internet-reachable installs.

### Sub-question 4: Per-object permission enforcement

This section claims that one endpoint gives the effective per-repo read decision, that the check is per distinct repository not per hit, and that the fail-closed rules must special-case public, internal, and unlinked users.

**The check** (primary, GitHub docs and Octokit source; high): `GET /repos/{owner}/{repo}/collaborators/{username}/permission` returns the effective permission after aggregating repo, team, organization base, and enterprise grants, so one call already accounts for org default read, team-nested grants, and outside collaborators. Octokit exposes it as `ReviewPermission`. A `none` means known-but-no-access; a 404 conflates two cases, the user not being a collaborator and the calling token not being able to see the repo, which is a fail-closed hazard handled below.

**Batching** (primary; high): there is no bulk "which of these repos can this user read" endpoint, but a search's 50 to 200 hits dedupe to a handful of distinct repositories, so cost is single-digit calls per search, trivial against 5,000 per hour, with a short per-user-per-repo cache around five minutes matching the AWS and Azure precedent.

**Identity-mapping trap** (primary; high): the endpoint keys on the current login, but logins are mutable and only the numeric id is durable, so Connapse must store the id and resolve id to current login at query time via the by-id user endpoint, refreshing on a short TTL. Storing the login at link time and reusing it risks checking a renamed or recycled username.

**Fail-closed decision table** (primary, with two medium-confidence rows flagged):

| Visibility | User link state | Permission result | Decision |
|---|---|---|---|
| public | any, including anonymous | not consulted | ALLOW |
| private | unlinked | not consulted | DENY |
| private | linked | read, write, or admin | ALLOW |
| private | linked | none | DENY |
| private | linked | 404, app can see repo | DENY |
| private | linked | 404, app cannot see repo | DENY, flag coverage gap |
| internal | unlinked | not consulted | DENY |
| internal | linked, enterprise member | any, including none | ALLOW |
| internal | linked, not enterprise member | read or higher | ALLOW |
| internal | linked, not enterprise member | none or 404 | DENY |
| any | any | visibility call itself fails | DENY |

Order of operations: resolve id to current login, GET the repo for visibility and app-visibility, then branch (public allow, internal membership-aware, private permission check), then cache about five minutes. The two flagged uncertainties are whether the permission endpoint returns `none` for an enterprise member who is not an explicit collaborator on an internal repo (close it with a separate enterprise-membership check) and whether an installation token with only `metadata:read` may call the endpoint at all or needs push access (verify empirically; if push is required, the read-only App posture must change).

**Freshness** (primary; high): evaluate live at query time with no ingest-time capture, matching AWS and Azure; the App may optionally subscribe to member, team, and repository webhooks to invalidate the cache faster than the TTL, an optimization not required for correctness.

### Sub-question 5: Fit against the existing Connapse types

This section claims the design needs two schema changes, one new interface, and a factory overload, and otherwise reuses the Connection, Source, and sync machinery unchanged.

The current `IConnector` in `src/Connapse.Core/Interfaces/IConnector.cs` is file-shaped: it lists `ConnectorFile` items and reads a byte stream per path. `ISyncCursorConnector` adds `GetChangesAsync(cursor)` returning a `SyncDelta` of upserts, deleted paths, a next cursor, and a full-resync flag, which is exactly the shape GitHub incremental sync needs. `IConnectorFactory.Create(Source, Connection, secret)` today takes a non-null Connection and a nullable secret.

Four concrete changes follow. First, the `Source` record's `ConnectionId` becomes nullable and the provider moves onto the source, so a connection-less public GitHub source can exist; the factory gains an overload that builds a connector from a source with no connection. This generalizes correctly, since a future web-crawl or public-package-docs source has the same connection-less shape and today cannot be expressed. Second, a new record-shaped read interface sits beside `IConnector`, returning assembled records (id, kind, title, body, comments, metadata, edges) rather than `ConnectorFile` streams; the GitHub connector implements both, the record interface for issues, pull requests, and discussions, and the file interface for docs. Third, the per-source `ContainerSettingsOverrides.Chunking` already lets each source pick its chunker, so the record chunker is a new `ChunkingStrategy` enum member and needs no schema change. Fourth, a connection-less public source must be pinned to github.com; an Enterprise Server public repository still needs a connection because the host is deployment-specific and an arbitrary host URL is a server-side-request-forgery surface.

## Conflicts and uncertainties

This section lists every load-bearing item that documentation did not fully settle, each of which should be smoke-tested before the design is locked.

- **Collaborator endpoint token requirement.** GitHub's docs list `metadata:read` as sufficient for fine-grained tokens but also carry a legacy sentence that the caller must have push access. If push is genuinely required for an installation token, the read-only App posture is wrong and the App would need broader grants. Highest-priority verification.
- **Unauthenticated 304 and the rate limit (resolved).** The rate-limit exemption for a 304 is documented only for authenticated requests; one community source said an unauthenticated 304 still counts. A spike on 2026-09-09 confirmed it: an unauthenticated 304 decremented the limit (56 to 55). So credential-less public polling cannot use ETags to stay under 60 per hour, which is why public docs sync by git clone.
- **Installation token reading public repos at 5,000 per hour.** Rests on a single doc passage; high confidence but worth a live check, since it is the escape hatch that makes public sources performant when an App is installed.
- **Internal-repo permission value for a non-collaborator enterprise member.** Undocumented; assume it may return `none` and close the gap with an enterprise-membership check rather than trusting the permission call.
- **Public-to-private returns 404 unauthenticated.** Inferred from GitHub's general privacy behavior, not a quoted line for this exact transition.
- **Comment inclusion.** All three prior-art connectors omit comment bodies from indexed text; Connapse's plan to include them is a deliberate experiment, not contradicted but not corroborated either.
- **Octokit.GraphQL maturity.** The discussions and richest-edge paths depend on either a 0.4.0-beta package or hand-rolled GraphQL; the report recommends hand-rolling, which is a build-cost the owner should weigh.

## Gaps, what we did not find

- No empirical confirmation of the collaborator-endpoint token requirement (documentation-inference). The other smoke-test item, the unauthenticated 304 behavior, was confirmed by a 2026-09-09 spike and is no longer a gap.
- No primary access to RAG4Tickets or Atlassian Rovo for the ticket-chunking evidence; only SeCom was reachable as a primary source.
- No measurement of initial-sync wall time for a large public repository under the 60-per-hour unauthenticated cap; expected to be hours, not quantified.
- No investigation of GitHub App installation across multiple organizations under one Connapse instance, which affects how installation ids are discovered and stored.
- No design for the record-shaped interface's exact method signatures; that is brainstorming and planning work, not research.

## Source quality assessment

The findings rest almost entirely on primary sources: GitHub REST and GraphQL documentation, the Octokit.NET repository, and the connector source code of Onyx, LlamaIndex, and Langchain read directly. The chunking recommendation rests on one reachable peer-reviewed primary source (SeCom, ICLR 2025) plus the prior Connapse report. Three load-bearing claims were single-source or documentation-inference; the unauthenticated-304 rate-limit behavior has since been confirmed by a 2026-09-09 spike, leaving the collaborator-endpoint token requirement (internally inconsistent official docs) and the installation-token public-repo rate limit (one doc passage) still to verify. No recommendation rests on a tertiary source. The codebase-fit findings are grounded in the actual interface files in `src/Connapse.Core/Interfaces`.

## Sources

**Primary, GitHub docs:** [Registering an App from a manifest](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest) · [Apps REST reference](https://docs.github.com/en/rest/apps/apps) · [Authenticating as an App installation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation) · [User access token for an App](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app) · [Rate limits for the REST API](https://docs.github.com/en/rest/overview/rate-limits-for-the-rest-api) · [Enterprise Server manifest flow](https://docs.github.com/en/enterprise-server@3.16/apps/sharing-github-apps/registering-a-github-app-from-a-manifest) · [Issues REST](https://docs.github.com/en/rest/issues/issues) · [Pulls REST](https://docs.github.com/en/rest/pulls/pulls) · [Pull review comments REST](https://docs.github.com/en/rest/pulls/comments) · [Compare commits](https://docs.github.com/en/rest/commits/commits) · [Conditional requests best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api) · [GraphQL for discussions](https://docs.github.com/en/graphql/guides/using-the-graphql-api-for-discussions) · [Collaborators REST](https://docs.github.com/en/rest/collaborators/collaborators) · [Repos REST (visibility)](https://docs.github.com/en/rest/repos/repos) · [Users REST (get by id)](https://docs.github.com/en/rest/users/users) · [Sub-issues REST](https://docs.github.com/en/rest/issues/sub-issues) · [Timeline events](https://docs.github.com/en/rest/issues/timeline) · [Webhook events](https://docs.github.com/en/webhooks/webhook-events-and-payloads)

**Primary, code and libraries:** [Octokit.NET GitHub Apps docs](https://github.com/octokit/octokit.net/blob/main/docs/github-apps.md) · [Octokit RepoCollaboratorsClient](https://github.com/octokit/octokit.net/blob/main/Octokit/Clients/RepoCollaboratorsClient.cs) · [Octokit NuGet](https://www.nuget.org/packages/Octokit) · [Octokit.GraphQL NuGet](https://www.nuget.org/packages/Octokit.GraphQL) · Onyx GitHub connector (onyx-dot-app/onyx, backend/onyx/connectors/github) · LlamaIndex GitHubRepositoryIssuesReader · Langchain GitHubIssuesLoader · [Markdig](https://github.com/xoofx/markdig) · Connapse `src/Connapse.Core/Interfaces/IConnector.cs`, `ISyncCursorConnector.cs`, `IConnectorFactory.cs`, `Models/SourceModels.cs`

**Primary, research:** [SeCom, ICLR 2025](https://arxiv.org/abs/2502.05589)

**Secondary and community:** [repository redirects](https://github.blog/news-insights/product-news/repository-redirects-are-here/) · [internal repositories GA](https://github.blog/news-insights/product-news/internal-repositories-are-now-generally-available-for-github-enterprise/) · [unauthenticated 304 still counts](https://dev.to/0012303/github-api-rate-limits-an-unauthenticated-304-still-costs-you-a-request-3af7)
