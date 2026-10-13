# How do Atlassian Cloud rate limits apply to Connapse's Jira and Confluence connectors, and what should Connapse do?
**Date:** 2026-10-13
**Status:** Reviewed (inline adversarial pass)
**Built on:** atlassian-provider-lay-of-land-2026-09-30.md, jira-setup-verification-2026-10-13.md

## Executive summary

**Two regimes, and it's unknown which one applies to Connapse.** Atlassian runs a points quota and per-second burst limits:
- **The points quota** (65,000 points an hour per app, enforced since 2026-03-02) is documented for "Forge, Connect, and OAuth 2.0 (3LO) apps".
- **Connapse's credential is a service account using OAuth client credentials** (two-legged OAuth: the service account authenticates as itself, with no user signing in). No Atlassian page, changelog or staff post says which regime covers it.
- **Burst limits** (about 100 requests per second per endpoint) apply to everyone and are shared by the whole site.

**If the points quota does apply, Connapse would exceed it on a medium install.** Every 5-minute sync re-lists every issue id and every page. At Atlassian's documented 1 point per object, a 2,000-item source costs about 2,000 points per cycle, or 24,000 points an hour. Three such sources use the whole quota, and one at 10,000 items is 120,000 an hour on its own. Once the quota is hit, Atlassian blocks *every* request until the top of the hour, so the search-time permission checks fail too. That fails closed: Jira and Confluence results disappear from search until the hour resets.

**Search-time checks are cheap by comparison**, at roughly 10–50 points per search with cold caches.

**Recommendation.** First, log Atlassian's rate-limit headers: one real response settles which regime applies. Then do these regardless of the answer:
- stop re-listing everything every 5 minutes;
- handle a quota refusal by pausing sync for that site;
- let search outrank sync for the shared budget.

## Research brief

**Question:** How do Atlassian Cloud rate limits apply to Connapse's service-account Jira and Confluence traffic, and what should change?

**Sub-questions:**
1. Which rate-limit regime applies to client-credentials traffic.
2. The point costs of the calls Connapse makes.
3. Rate-limit headers, 429 semantics and backoff guidance.
4. Connapse's load at three install sizes.
5. How comparable products handle it, and design options.

**Out of scope:** Jira and Confluence Data Center; write-heavy workloads.

**Success criteria:** a ranked list of changes, and what to measure on the live site.

## Findings by sub-question

### 1. Which regime applies to Connapse's service-account traffic: undocumented

Atlassian documents two regimes and doesn't say which one covers service-account client-credentials traffic.

**The points quota.** The Jira and Confluence rate-limiting pages (Jira page updated 2026-10-13) say the points model applies to "all Forge, Connect, and OAuth 2.0 (3LO) apps", and that "API token-based traffic is not affected by this change, and will continue to be governed by existing burst rate limits" [primary: developer.atlassian.com, Jira and Confluence rate-limiting pages].
- Neither page mentions service accounts, client credentials or two-legged OAuth. Nor does the 2025-12 announcement blog [primary], the service-account support pages [primary], or any of the 174 posts in Atlassian's 2026 points thread [secondary: community.developer.atlassian.com/t/97828].
- Atlassian staff in that thread say "65,000 pts/hr is per app, shared across all tenants". A higher tier, up to 500,000 an hour per tenant, needs an Atlassian review through a Marketplace support ticket [secondary].
- A service-account credential has no Developer Console app or Marketplace listing behind it. That *suggests* it falls outside the points pool, but doesn't establish it.

**Burst limits apply regardless.**
- About 100 requests per second per endpoint for GET and POST [primary].
- Atlassian's Principal PM for Jira APIs says they're enforced "at the tenant level rather than per token", so every integration on a site shares the same budget [secondary: community.atlassian.com].
- Confluence adds unspecified "additional burst protections" on its Permissions and Search endpoints [primary]. That's exactly where Connapse's search-time checks go.

**Undocumented limits partners have hit** [tertiary: partner posts in the points thread]:
- a per-user concurrency limit (`jira-max-concurrent-threads-across-all-instances-per-tenant-per-user`);
- `jira-cost-based`.

### 2. Point costs: 1 per call plus 1–2 per object, with many details unpublished

**The formula** [primary: rate-limiting pages]:
- Every request costs 1 point, plus 1 per core object (issues, pages, projects, attachments) and 2 per identity object (users, groups, roles, permissions).
- Writes cost only the 1 base point.
- Atlassian's examples: one issue = 2; one Confluence user = 3; group members for 8 users = 17.

**Unpublished** [primary, via its absence]:
- whether objects are counted as returned or as requested;
- whether nested objects count;
- whether downloads count;
- whether `permissions/check` (a POST that *reads* permissions) costs 1, or 2 per issue checked.

**Inferred costs of Connapse's calls:**

| Call | Cost |
|---|---|
| Jira issue id listing (`search/jql`, ids only) | 1 + N for N issues |
| Jira bulkfetch of 100 issues | ~101 |
| A comment page | 1 + comments |
| Jira `field` list | 1 + fields (often several hundred) |
| `user/groups` | 1 + 2 × groups |
| Role actors | 3 + 2 × actors |
| Confluence page listing (250 pages) | ~251 |
| Confluence `content/permission/check` | 1 or 3 |
| Jira `permissions/check` | 1, or up to 2 × issues × permissions |

There is no header that reports the cost of a single request. Watching the remaining-quota header before and after each call type is the only way to measure it.

### 3. Headers and 429 handling: Atlassian distinguishes burst from quota refusals

**Enforced headers** [primary]:
- `X-RateLimit-Limit` and `X-RateLimit-Remaining`;
- `X-RateLimit-Reset` (an ISO 8601 time, on 429s only);
- `X-RateLimit-NearLimit` (true below 20% remaining);
- `Retry-After` in seconds;
- `RateLimit-Reason` on 429s: `jira-burst-based`, `jira-quota-global-based`, `jira-quota-tenant-based`, `jira-per-issue-on-write`, and `confluence-quota-*`.

**The points model's own headers** are still marked beta: `Beta-RateLimit-Policy: "global-app-quota";q=65000;w=3600` and `Beta-RateLimit: …;r=…;t=…`. Their presence on a response is the clearest sign that the points model is metering that traffic.

**Guidance:**
- Retry only idempotent requests, wait at least `Retry-After`, then back off exponentially with ×0.7–1.3 jitter (Confluence's sample: 5 s start, 30 s cap, 4 retries).
- On a **burst** 429, slow down only that endpoint. On a **quota** 429, "pause all API requests until the window resets".
- Share rate-limit state across threads and nodes, and spread scheduled load across the hour.
- Once the quota is hit there is "no partial throttling… all requests are blocked until reset" [primary].

**What Connapse does today** [code reading]:
- A 429 is turned straight into an exception, with no retry and no reason-based handling.
- Sync defers that cycle to the next 5-minute tick.
- Search denies the hit and caches the failure for 30 seconds.
- Only `Retry-After` is read, and only into the error message. No other rate-limit header is read or logged.

### 4. Connapse's load: sync dominates, through full re-listing every cycle

Every 5-minute cycle, each source does the following, changes or not [code reading: JiraProjectConnector, ConfluenceSpaceConnector]:
- **Jira:** re-lists every issue id, fetches the project, the full `field` list and the change query.
- **Confluence:** re-lists every page and blog post. It also re-pages a whole day of comment and attachment changes, because the change query reaches back 24 hours to cover timezone uncertainty.

On top of that:
- **Jira's daily comment sweep** re-reads up to 500 issues per cycle.
- **Confluence's daily attachment sweep** re-reads every page's attachment list in one cycle, unsliced.

Estimates, assuming the points model applies at 1 point per returned object (inferred):

| Install | Sync per hour | Searches per hour | Total vs 65,000 |
|---|---|---|---|
| Small (2 sources × ~500 items, 5 users) | ~16,000 (2 × ~1,300 per cycle × 12) | <500 | ~25% |
| Medium (20 sources × ~2,000 items, 100 users, 2,000 searches a day) | ~550,000 | ~2,000–8,000 | **~8× over** |
| Large (50+ sources, 10,000+ items) | millions | ~10,000+ | far over |

Search-time cost per search: one Jira bulk check, plus `user/groups` and role reads when restricted hits appear, plus one comment read per restricted hit. Confluence adds one check per distinct page. That's roughly 10–50 points cold, and near 0 within the cache lifetimes. **Sync is at least 95% of the load** on any non-trivial install.

If the points model does *not* apply, only burst limits matter. Connapse's request rate is far below 100 per second per endpoint: sources sync one after another, and searches make a handful of calls. Even then, the site-wide sharing means another busy integration on the same site could crowd Connapse out.

### 5. Comparable products and design options

**None of the connectors examined does more than retry after a refusal** [primary: source code]:
- **Onyx (Confluence and Jira):** retries after `Retry-After`, or exponential backoff capped at 60 s, without a shared budget.
- **Elastic connectors:** sleeps `Retry-After`, then a 3-attempt retry, with fixed concurrency (Jira 5, Confluence 50).
- **Neither paces itself from headers.** Onyx and Elastic don't check permissions live at search time either: Onyx copies permissions into its index on a schedule.
- **Glean and Microsoft:** they publish nothing on this. Glean's Jira connector is a Marketplace app that also receives webhooks [secondary].

Patterns that fit Connapse, each grounded in Atlassian's guidance or standard practice:
- **One rate-limit state per site**, shared process- and node-wide [primary: Atlassian].
- **Interactive search checks outrank background sync**, with a reserve kept for search [primary: Google SRE "criticality"; Stripe load shedding].
- **Adaptive pacing** from `NearLimit` and `Remaining` [primary: Atlassian].
- **Branch on `RateLimit-Reason`:** back off only the endpoint on a burst refusal; open a site-wide circuit breaker until reset on a quota refusal [primary: Atlassian; secondary: Fowler].
- **Retry in a single layer**, with jitter and a retry budget [primary: AWS Builders' Library; Google SRE].
- **Merge identical concurrent permission checks** into one call, singleflight-style [primary: Go singleflight].
- **Less sync volume:** incremental queries each cycle, full reconciliation rarely, minimal fields, a jittered start [primary: Atlassian].

## Recommendation (ranked by value)

1. **Log and expose Atlassian's rate-limit headers.** On every Atlassian response, record `Beta-RateLimit-Policy`, `Beta-RateLimit`, `X-RateLimit-*`, `RateLimit-Reason` and `Retry-After`. Log a once-an-hour summary per site, and warn when `NearLimit` is true. One real response settles which regime applies; with the points model, the remaining-quota values measure Connapse's true point usage. *Cheap; do first.*
2. **Stop re-listing everything every 5 minutes.**
   - Each cycle: only the incremental change queries (JQL `updated >=`, Confluence CQL `lastmodified`).
   - Full listings, which detect deletions and moves, run on a slower reconcile schedule: hourly by default, adaptive to quota headroom.
   - Fetch Jira's `field` list hourly instead of every cycle.
   - Slice Confluence's daily attachment sweep, as Jira's comment sweep already is.

   This cuts sync points by roughly 10–12×.

   Trade-off: deletions and moves show up within the reconcile interval instead of 5 minutes. Permissions aren't affected, because Connapse checks them live.
3. **Handle refusals by reason, site-wide.**
   - A **quota** 429 pauses all sync for that site until `X-RateLimit-Reset`.
   - A **burst** 429 backs off that endpoint with jitter and `Retry-After` as the floor, retried in one layer only.
   - Search keeps failing closed as now. With sync paused, the remaining budget goes to search.
4. **Search before sync.** When `NearLimit` is true, or less than a set share of quota remains, skip or slow sync cycles for that site, so searches keep working.
5. **Jitter each source's sync start**, so sources and installs don't all fire on the same 5-minute boundary.
6. **Merge identical concurrent permission checks.** Concurrent searches checking the same issue or comment share one call.

**Measure on the live site, after step 1:**
- whether `Beta-RateLimit-Policy` / `global-app-quota` appears on the service account's responses;
- the remaining-quota change across one Jira sync cycle and one Confluence cycle;
- what one `permissions/check` and one Confluence permission check cost;
- whether any 429 carries `jira-cost-based` or the concurrency reason.

## Conflicts and uncertainties

- **Service-account regime:** undocumented. The quota's app-keyed definition suggests it's exempt; no source confirms it either way.
- **Quota enforcement:** staff say only "sustained overage" triggers hard limiting; a partner's tests saw an immediate cut-off at 65,000 points.
- **Confluence page wording:** it says "each app or token has a set quota" while also exempting API tokens.
- **Permission-check cost:** 1 point under the write rule, or 2 per object under the identity rule. A partner reports the permissions API allowing only about 30 checks an hour [tertiary].
- **Object counting:** returned versus requested is unpublished, so every load figure above is an inference.

## Gaps — what we did not find

- Any statement on rate limits for service accounts or client credentials.
- Numeric Confluence burst limits, including the Permissions and Search endpoints.
- The trigger conditions for the concurrency and cost-based 429 reasons.
- The original changelog entries (CHANGE-2958, CHANGE-3045); the changelog now shows only recent entries.
- Per-request cost reporting; Atlassian promises a fuller cost catalogue later.

## Source quality assessment

The regime definitions, header names, backoff guidance and point formula come from Atlassian's rate-limiting pages (primary). Quota scope and tiers come from Atlassian staff posts (secondary). The undocumented 429 reasons and permission-check costs come from partner reports (tertiary). Connapse's load model comes from direct code reading. Its point figures rest on the inferred one-point-per-returned-object reading, so they are orders of magnitude, not measurements.

## Sources

**Primary**
- Jira rate limiting: https://developer.atlassian.com/cloud/jira/platform/rate-limiting/
- Confluence rate limiting: https://developer.atlassian.com/cloud/confluence/rate-limiting/
- Evolving API rate limits (Atlassian blog, 2025-12-12): https://www.atlassian.com/blog/platform/evolving-api-rate-limits
- Create OAuth 2.0 credential for service accounts: https://support.atlassian.com/user-management/docs/create-oauth-2-0-credential-for-service-accounts/
- Understand service accounts: https://support.atlassian.com/user-management/docs/understand-service-accounts/
- AWS Builders' Library, timeouts, retries and backoff with jitter; AWS Architecture Blog, exponential backoff and jitter
- Google SRE book, "Handling Overload"
- Onyx source (onyx-dot-app/onyx: connectors/confluence/utils.py, jira/source_operations.py, cross_connector_utils/rate_limit_wrapper.py)
- Elastic connectors source (elastic/connectors: sources/atlassian/jira and confluence)
- Connapse code: JiraProjectConnector.cs, ConfluenceSpaceConnector.cs, AtlassianApiClient.cs, JiraPermissionChecker.cs, ConfluencePermissionChecker.cs, AtlassianSearchResultVerifier.cs, SourceSyncService.cs

**Secondary**
- 2026 point-based rate limits thread (Atlassian staff answers): https://community.developer.atlassian.com/t/2026-point-based-rate-limits/97828
- Jira Cloud burst rate limits, tenant-level enforcement (Atlassian PM): https://community.atlassian.com/forums/Jira-articles/Reminder-Please-ensure-your-Apps-comply-with-Jira-Cloud-Burst/ba-p/3150831
- Glean Jira Cloud connector docs; Fowler, "CircuitBreaker"

**Tertiary**
- Partner posts in the points thread (#163, #164, #167, #169, #177)
- Jira search cost question: https://community.developer.atlassian.com/t/jira-cloud-search-rate-limit-jira-api/98318
