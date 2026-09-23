# How do other GitHub connectors handle rate limits and auth at scale, and what is the most scalable approach for Connapse?
**Date:** 2026-09-23
**Status:** Reviewed
**Built on:** github-connector-implementation-2026-09-09.md

## Executive summary

No comparable product syncs GitHub issues and pull requests anonymously. Onyx, Airbyte, LlamaIndex, LangChain, Elastic, Glean, Unstructured, and Sourcegraph all require a credential for the API. Sourcegraph is the only one that permits anonymous access, and it syncs only git repositories. The credential-free alternatives are dead ends: GH Archive lacks pull request bodies and later edits and has dropped events, web scraping conflicts with GitHub's Acceptable Use Policies, and GitHub tightened unauthenticated limits in May 2025 without publishing new numbers. The scalable design combines four pieces:

1. **An optional GitHub App**, whose installation token (5,000–12,500 requests an hour) is used for public-repository reads too.
2. **Conditional-request probes.** An authenticated 304 costs no primary budget, so idle repositories become nearly free.
3. **A budget shared across the whole server**, driven by GitHub's rate-limit headers.
4. **A JWT signed remotely by Key Vault or KMS**, so no long-lived key sits on the host.

The key uncertainty is whether an installation token reads *arbitrary* public repositories at the installation rate. The indirect evidence says yes, and a five-minute live test settles it.

## Research brief

**Question:** How do products that index GitHub for search/RAG handle authentication and rate limits when syncing issues, pull requests, comments, and docs at scale, and what is the most scalable approach for Connapse?

**Sub-questions:**
1. How do existing connectors authenticate and handle rate limits?
2. Which GitHub API mechanics cut call volume (GraphQL, conditional requests, webhooks, events, secondary limits)?
3. Can public-repository issue data be obtained without a credential, outside the REST cap?
4. Which credential models raise the limit while honouring "no long-lived stored credential"?

**Out of scope:** private-repository permission filtering (covered by the 2026-09-09 report); Enterprise Server.

**Success criteria:** a ranked recommendation for Connapse, with the evidence behind it.

## Findings by sub-question

### 1. How existing GitHub connectors authenticate and handle rate limits

Every connector that syncs GitHub issues and pull requests requires a credential. Airbyte has the most complete rate-limit handling.

**Credential requirements:**
- **Onyx:** requires a personal access token. It fetches issues and PRs sorted by update time, then sleeps until the rate limit resets plus a minute. It keeps a checkpoint cursor and does not index comments ([connector.py](https://github.com/onyx-dot-app/onyx/blob/main/backend/onyx/connectors/github/connector.py), primary).
- **LlamaIndex:** requires a PAT or a GitHub App ([github_client.py](https://github.com/run-llama/llama_index/blob/main/llama-index-integrations/readers/llama-index-readers-github/llama_index/readers/github/issues/github_client.py), primary).
- **LangChain:** requires a PAT ([github.py](https://github.com/langchain-ai/langchain-community/blob/main/libs/community/langchain_community/document_loaders/github.py), primary).
- **Unstructured:** requires a PAT, and ingests repository files only (primary).
- **Elastic:** accepts a PAT or a GitHub App. It uses GraphQL with comments nested inside each issue, and sleeps until the reset time ([client.py](https://github.com/elastic/connectors/blob/main/app/connectors_service/connectors/sources/github/client.py), primary).
- **Glean:** uses an organisation-installed GitHub App plus per-user OAuth, crawls at 4 queries per second, "strongly recommends" webhooks, and runs incremental crawls every 10 minutes ([docs](https://docs.glean.com/connectors/native/github/about), primary).
- **Sourcegraph:** clones git repositories only. It needs no token scopes for public repositories but still advises a token for github.com ([docs](https://sourcegraph.com/docs/admin/code_hosts/github), primary).

**Airbyte, the most scalable design found:** it accepts several PATs and rotates between them. For each token it tracks the REST and GraphQL budgets separately from the `X-RateLimit-*` headers, and it retries on `Retry-After` or on secondary-limit messages. It fetches comments across the whole repository with a per-repo `since` cursor, and runs 4 concurrent workers by default ([manifest.yaml](https://github.com/airbytehq/airbyte/blob/master/airbyte-integrations/connectors/source-github/source_github/manifest.yaml), primary).

**How Connapse compares:** the Phase 3 issues source already uses Airbyte's repository-wide comment sweeps with a `since` cursor. What it lacks is a credential and a budget shared across sources.

### 2. GitHub API mechanics that cut call volume

With any credential, idle-repository polling can cost almost nothing; without one, no technique helps. This section cites GitHub's [REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api), [GraphQL limits](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api), and [REST best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api), all primary.

**Primary limits:**

| Credential | Requests an hour | Counted per |
|---|---|---|
| None | 60 | IP address |
| PAT or OAuth user token | 5,000 | User |
| GitHub App installation | 5,000, plus 50 per repository past 20 (up to 12,500); 15,000 on Enterprise Cloud | Installation |
| Actions `GITHUB_TOKEN` | 1,000 | Repository |

REST and GraphQL budgets are separate.

**Conditional requests:** an authenticated request that returns 304 does not count against the primary limit; an unauthenticated one does, which a 2026-09-09 spike confirmed. A 304 needs unchanged request parameters, so a poll whose `since` value advances never gets one. The working design is a fixed-parameter probe (for example, sorted by update, one per page) against issues and both repository-wide comment endpoints. The full `since` fetch runs only when a probe's ETag changes. Idle repositories then cost 0 primary requests. That ETags stay stable when nothing changes is an inference; confidence medium.

**GraphQL:** it costs points rather than requests. 100 issues with 100 comments each is about 2 points, and adding pull-request review threads makes it about 22. It mainly saves the per-parent sub-issue calls, roughly 2 to 5 times fewer calls rather than 100 times, because repository-wide REST comment sweeps are already efficient. GraphQL is unavailable anonymously. Since July 2025, queries that time out count against the budget ([changelog](https://github.blog/changelog/2025-07-21-including-timeouts-in-primary-rate-limits/)).

**Secondary limits:**
- 100 concurrent requests.
- 900 REST points a minute; a GET costs 1 point.
- 90 seconds of CPU time per 60 seconds of real time.

Documented backoff order: honour `retry-after`; otherwise, if `x-ratelimit-remaining` is 0, wait until `x-ratelimit-reset`; otherwise wait at least a minute and back off exponentially. Requests should be serialised rather than run concurrently.

**Webhooks:** they cover issues, comments, reviews, and sub-issues. A GitHub App cannot receive events for repositories it is not installed on, and a repository webhook needs admin access, so webhooks do not apply to third-party public repositories. GitHub advises against smee.io in production ([docs](https://docs.github.com/en/webhooks/using-webhooks/handling-webhook-deliveries), primary).

**Events API:** it keeps at most 300 events over 30 days, arrives 30 seconds to 6 hours late, and is "not built to serve real-time use cases" ([docs](https://docs.github.com/en/rest/activity/events), primary). It is usable only as a "something changed" hint.

### 3. Getting public-repository issue data without a GitHub credential

No credential-free channel is viable as the main sync source. The only one that is even partially usable is GH Archive.

**GH Archive** (hourly dumps of the public events feed) carries full issue and comment bodies about 5 minutes after each hour. Its gaps:
- **Pull request title and body:** since GitHub trimmed Events API payloads on 7 October 2025, pull-request events carry only `id`, `number`, `url`, `base`, and `head` ([changelog](https://github.blog/changelog/2025-08-08-upcoming-changes-to-github-events-api-payloads/), primary; the field list was measured).
- **Edits:** later changes to a body are not events at all.
- **Lost events:** a community report puts losses at about 20% in October 2025 ([discussion](https://github.com/orgs/community/discussions/178788), secondary).
- **Download volume:** about 400 MB a day, with filtering done client-side.

**BigQuery:** the same data, but it needs a Google credential and is billed per terabyte scanned.

**Web endpoints:** github.com serves no issues Atom feed (406). Scraping HTML falls under the [Acceptable Use Policies](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies) (primary), which permit only research and archival scraping and prohibit copying the Service without permission.

**Other sources:** the Events API shares the 60-an-hour cap. Git references carry code only, and export APIs need an owner credential. Software Heritage archives code only, and the published datasets are stale snapshots.

**Unauthenticated limits are tightening:** GitHub lowered them on 8 May 2025, citing scraping, without publishing numbers ([changelog](https://github.blog/changelog/2025-05-08-updated-rate-limits-for-unauthenticated-requests/), primary).

### 4. Credential models under "no long-lived stored credential"

GitHub has no secretless token exchange for workloads outside Actions, so something must hold a secret. The closest fit is a GitHub App whose private key lives non-exportably in Azure Key Vault or AWS KMS, with the JWT signed remotely, mirroring the IAM Roles Anywhere pattern.

**GitHub App private key:**
- Keys never expire. An App can hold up to 25 keys, so rotation causes no downtime.
- GitHub itself recommends a sign-only key vault ([docs](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps), primary).
- GitHub always generates the key, so it must be imported into the vault once, as non-exportable.
- Remote-signing implementations exist for Azure Key Vault ([blog](https://jeffwilcox.blog/2026/03/github-app-remote-jwt-signing/), secondary) and for AWS KMS ([kms-import](https://github.com/chinmina/kms-import), secondary).
- None of Sourcegraph, Backstage, Renovate, Grafana, or GitLab documents KMS-held keys, so Connapse would lead here.

**Ranked options:**

| Rank | Option | Limit | Stored secret | Setup |
|---|---|---|---|---|
| 1 | App key in Key Vault or KMS, remote signing | 5,000–12,500 an hour | None on the host | Medium-high |
| 2 | App key encrypted on the host (current design) | 5,000–12,500 an hour | Never expires; rotatable | Low-medium |
| 3 | Device-flow user token | 5,000 an hour, shared with that user | A refresh token that rolls every 6 months | Low, but tied to one person |
| 4 | Fine-grained PAT | 5,000 an hour | A static secret, possibly never-expiring | Lowest |
| 5 | Scheduled Actions workflow | 1,000 an hour per repository | None | High, and runs outside Connapse |

## Recommendation for Connapse

1. **Keep anonymous public sync as a zero-setup starter for a few repositories.** Do not promise scale: the anonymous limit has already tightened, and nobody else relies on it. The Sources dialog should state the per-repository cost honestly: about 6 requests an hour, roughly 10 repositories.
2. **Build the GitHub App next, and route public-repository reads through its installation token.** One installation lifts every public source from 60 to 5,000–12,500 requests an hour. Verify first that a token installed on one repository reads arbitrary public repositories at that rate, and whether an install with zero repositories is possible.
3. **Add conditional-request probes whenever a credential is present.** Idle repositories then cost no primary budget, and a single installation covers hundreds to thousands of repositories.
4. **Replace per-source rate-limit handling with one budget shared across the whole server.** Read `X-RateLimit-*` from every response, run requests one at a time, and back off in GitHub's documented order. Even at 60 an hour this stops sources from starving each other, and with polling that stretches idle repositories to hourly checks it roughly doubles how many repositories fit.
5. **Offer Key Vault or KMS remote signing for the App key** as the setting that fully meets the no-stored-key rule. Keep the encrypted-on-host key as the default, as with AWS.
6. **Do not build on GH Archive or on scraping.**

## Conflicts and uncertainties

- **Can an installation token read public repositories outside the installation at the installation rate?** The docs are silent. Indirect primary evidence says yes: Actions' `GITHUB_TOKEN` reads other public repositories. Uncited search summaries said no. This is load-bearing for recommendation 2 and needs a live test.
- **Can an App be installed with zero repositories?** Unknown. A throwaway empty repository may be needed.
- **The current unauthenticated limit:** the docs still say 60, but the May 2025 changelog says it was lowered, with no numbers.
- **Enterprise Cloud installation limit:** 15,000 on GitHub's REST page versus 10,000 on its GraphQL page.
- **304s and the rate limit:** the events page says a 304 leaves the rate limit untouched without the authentication condition; the best-practices page requires authentication. The 2026-09-09 spike supports the best-practices page.
- **Elastic's claims:** its documentation says "incremental syncs", but its queries have no `since` filter. Its documentation also lists no comments, but its code fetches them.
- **GH Archive completeness** rests on one unanswered community report.

## Gaps: what we did not find

- No primary material on how Dify, Ragie, Vectara, Coveo, Greptile, or DeepWiki call GitHub.
- No published time-to-first-sync or repositories-per-token figures from any vendor.
- No documentation of `Last-Modified` behaviour on list endpoints called with `since`.
- No primary confirmation that `gh webhook forward` is development-only.

## Source quality assessment

Most of the synthesis rests on primary sources: GitHub's documentation and changelogs, the source code of Onyx, Airbyte, LlamaIndex, LangChain, and Elastic, and the Glean and Sourcegraph documentation. Measurements taken on 2026-09-23 cover Events API payload fields, GH Archive timing, and feed status codes. Secondary sources support four points: remote-signing implementations (blog and GitHub repository), GH Archive event loss (community discussion), BigQuery cost figures, and DeepWiki. No recommendation rests on a tertiary source. The load-bearing uncertainty, whether installation tokens can read public repositories outside the installation, rests on inference from primary evidence and should be tested before building on it.

## Sources

**Primary: GitHub documentation and changelogs**
- [REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)
- [GraphQL limits](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api)
- [REST best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)
- [Events](https://docs.github.com/en/rest/activity/events)
- [Issue events](https://docs.github.com/en/rest/issues/events)
- [Webhook events](https://docs.github.com/en/webhooks/webhook-events-and-payloads)
- [Webhook types](https://docs.github.com/en/webhooks/types-of-webhooks)
- [Handling deliveries](https://docs.github.com/en/webhooks/using-webhooks/handling-webhook-deliveries)
- [App private keys](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps)
- [App best practices](https://docs.github.com/en/apps/creating-github-apps/about-creating-github-apps/best-practices-for-creating-a-github-app)
- [Installation authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation)
- [User tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app)
- [Refreshing user tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens)
- [PATs](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)
- [Acceptable Use Policies](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies)
- [Migrations](https://docs.github.com/en/rest/migrations/orgs)
- [Unauthenticated limits changelog (2025-05-08)](https://github.blog/changelog/2025-05-08-updated-rate-limits-for-unauthenticated-requests/)
- [Events payload changelog (2025-08-08)](https://github.blog/changelog/2025-08-08-upcoming-changes-to-github-events-api-payloads/)
- [Timeouts changelog (2025-07-21)](https://github.blog/changelog/2025-07-21-including-timeouts-in-primary-rate-limits/)
- [Events retention changelog](https://github.blog/changelog/2024-11-08-upcoming-changes-to-data-retention-for-events-api-atom-feed-timeline-and-dashboard-feed-features/)
- [Fine-grained PATs GA](https://github.blog/changelog/2025-03-18-fine-grained-pats-are-now-generally-available/)

**Primary: connector code and vendor documentation**
- Onyx GitHub connector and `rate_limit_utils.py`
- Airbyte source-github `manifest.yaml` (v2.7.1)
- LlamaIndex `github_client.py`
- LangChain `github.py`
- Elastic `client.py` / `query.py` and its [GitHub connector docs](https://www.elastic.co/docs/reference/search-connectors/es-connectors-github)
- [Glean GitHub docs](https://docs.glean.com/connectors/native/github/about)
- [Sourcegraph GitHub docs](https://sourcegraph.com/docs/admin/code_hosts/github)
- [Unstructured GitHub docs](https://docs.unstructured.io/open-source/ingestion/source-connectors/github)
- [Backstage](https://backstage.io/docs/integrations/github/github-apps)
- [Renovate](https://docs.renovatebot.com/modules/platform/github/)
- [GitLab importer](https://docs.gitlab.com/user/project/import/github/)
- [Grafana](https://grafana.com/docs/plugins/grafana-github-datasource/latest/setup/token/)
- [gharchive.org](https://www.gharchive.org/)
- [OSSInsight API](https://ossinsight.io/docs/api)
- [Software Heritage FAQ](https://docs.softwareheritage.org/user/faq/index.html)

**Secondary**
- [Remote JWT signing (Wilcox, 2026)](https://jeffwilcox.blog/2026/03/github-app-remote-jwt-signing/)
- [chinmina/kms-import](https://github.com/chinmina/kms-import)
- [gardener/github-oidc-federation](https://github.com/gardener/github-oidc-federation)
- [GH Archive loss report](https://github.com/orgs/community/discussions/178788)
- [OpenDigger mirror](https://github.com/igrigorik/gharchive.org/issues/323)
- [BigQuery pricing](https://cloud.google.com/bigquery/pricing)
- [DeepWiki guide](https://codersera.com/blog/deepwiki-complete-guide-2026/)
