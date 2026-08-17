# How should Connapse safely allow external data-source configuration to be created programmatically?

**Date:** 2026-08-17
**Status:** Reviewed
**Built on:** no prior corpus material (Connapse MCP was not available in this session; `docs/research/highest-value-connectors-2026-08-13.md` covers connector selection, not authorization)

## Executive summary

Connapse should expose source creation over REST behind an admin role, keep connection creation out of the API entirely, and put nothing that mutates configuration on MCP. Infrastructure-as-Code is preserved because the thing IaC needs to declare — sources — stays API-creatable; the credential boundary that IaC should *not* be able to invent stays file- and admin-only. This is the shape Grafana, Snowflake, Databricks, and Kubernetes all converged on independently: the object that names a credential is created through a lower-trust channel than the credential itself.

Two server-side constraints must exist regardless of which surface is enabled. First, `allowedRoot` must be selected from a configured allowlist rather than supplied by a caller, matching Elasticsearch `path.repo`. Second — and this is a live defect in already-merged code, not a design question — Connapse's filesystem path confinement is **empirically bypassable by a directory junction**, verified during this research against the exact .NET APIs the product uses.

The most significant correction to the team's prior thinking: AWS session policies, which looked like the elegant answer to bucket scoping, cannot be the primary control. They apply only when `roleArn` is set and have **no Azure equivalent**, so they cannot provide a uniform boundary across both clouds.

## Research brief

**Question:** How should a self-hosted RAG platform allow external data-source configuration to be created programmatically (REST/CLI, for IaC) without turning that capability into a security hole?

**Sub-questions investigated:**
1. What patterns do comparable self-hosted products use to reconcile IaC with programmatic data-source creation?
2. Is "allowlist roots in config, select by reference via API" the established answer for filesystem-root authority scopes, and what are its failure modes?
3. How do these systems separate "declare configuration" from "activate/grant authority"?
4. What is best practice for exposing mutating tools on an MCP server given prompt-injection risk from ingested content?
5. How do products constrain which buckets a scope may name, when the credential itself is broad?

**Out of scope:** Connapse UI design; the Phase 4 Sources/Connections screens; regulatory compliance; multi-tenant SaaS tenancy models (Connapse is self-hosted, single-org).

**Success criteria:** A per-surface recommendation (Admin UI / REST+CLI / MCP) for connections vs sources, the server-side constraints required regardless of surface, and an IaC story that does not weaken the boundary.

## Findings by sub-question

### 1. IaC patterns in comparable self-hosted products

**Claim: the established pattern is channel-scoped authority — the file/config channel can create objects the HTTP API cannot.**

Grafana is the clearest precedent and the closest analogue to Connapse. Datasources are provisioned from YAML in `provisioning/datasources/`, read at startup, with version control as the stated motivation. The security-relevant part is the asymmetry: a provisioned datasource carries `editable` (default false), surfacing as `readOnly`, and **`readOnly` cannot be set through the HTTP API at all**. Grafana PR #19006 ignores the field on create/update with the explicit rationale that "one should not be able to create a readonly data source via the API." The filesystem is treated as the high-trust channel because writing it already implies host access. [primary]

That boundary has leaked in practice — Grafana issue #32556 reports a `PUT` on a provisioned datasource flipping `readOnly` back to false. Worth noting as evidence that this pattern needs test coverage, not merely intent. [primary, single issue]

Grafana does expose `POST /api/datasources`, gated by a `datasources:create` permission. The OSS default role mapping for that permission was not confirmed. [primary for the permission name; unverified for the default role]

Airbyte takes the opposite approach — full creation over its public API, with the guardrail in RBAC granularity rather than channel. It has a **Source editor** role distinct from Destination editor, explicitly so teams can start syncing "without being able to reconfigure the destinations they write to." Reading data and writing data are separate privileges. Its Terraform provider is Speakeasy-generated directly from the OpenAPI spec, adding no separate authority model. [primary]

Kubernetes states the underlying principle most directly: permission to create a workload "implicitly grants access to many other resources… such as Secrets, ConfigMaps, and PersistentVolumes that can be mounted." Generalized to CRDs, **create-rights on a custom resource are equivalent to the controller's rights over whatever that resource can name** — the exact confused-deputy shape Connapse has, where the sync service holds the credential and the source names the target. The `secretRef` pattern splits this: the declarative object *references* a credential it cannot read. [primary]

Vault treats mount creation as admin-tier via `sys/mounts/*`. Whether the `sudo` capability is strictly required versus defensively included in the canonical admin policy could not be confirmed. [mixed: "admin-tier" solid, "sudo-required" unverified]

**Terraform support does force a write endpoint to exist** — a provider needs full CRUD plus stable IDs for drift detection, confirmed by both Airbyte (provider generated from API) and Grafana (provider works only against `/api/datasources`, not provisioning files). What it does *not* force is that the endpoint be reachable by ordinary tokens. [secondary, inferential]

### 2. Filesystem root allowlisting, and why it is necessary but not sufficient

**Claim: config-file allowlisting of filesystem roots is established practice, but it is blast-radius control, not confinement — every product using it still had traversal CVEs inside the allowlisted root.**

Elasticsearch `path.repo` is the canonical case: a **static** node setting listing filesystem paths usable for snapshot repositories, where each repository location "must resolve to a path under one of these entries." It is not settable via API; changing it requires a rolling restart. The snapshot API then selects a repository by location *under* an allowlisted root — precisely the "API selects among configured roots by reference" shape. OpenSearch inherits it verbatim. [primary]

PHP `open_basedir` is the same shape and ships with a warning Connapse should internalize: it "is just an extra safety net, that is in no way comprehensive, and can therefore not be relied upon when security is needed." It also documents the exact trap — it "specifies a directory name, not a prefix," so `/www/` must not match `/www-files/`. Notably, PHP **resolves symlinks before the check** and disables the realpath cache to do so. [primary] MySQL `secure_file_priv` follows the same startup-only pattern. [secondary]

The cautionary cases matter as much as the pattern. **CVE-2015-5531** (Elasticsearch ≤1.6.0) was an unauthenticated arbitrary file read where `path.repo` being configured was a *precondition* of the exploit rather than a defense — traversal happened relative to the allowlisted root. **CVE-2021-43798** (Grafana 8.x) was a pre-auth arbitrary file read caused by string-joining a plugin id onto a base directory with no canonicalization, introduced by a refactor. The nginx `alias` off-by-slash class (`location /images` + `alias /var/www/img/` → `/images../` escapes) is the canonical form of prefix-based confinement failure. [primary]

**Residual attacks against Connapse's current `Path.GetFullPath` + `StartsWith(root + separator)` check:**

- **Symlinks and junctions — confirmed exploitable, see below.** `Path.GetFullPath` is purely lexical and does not dereference reparse points. Java's `getCanonicalPath` does resolve symlinks; .NET requires `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` explicitly. (CWE-59) [primary, and empirically verified here]
- **Bind mounts and hardlinks — not fixable by canonicalization.** The resolved path genuinely *is* beneath the root; there is no string to inspect. Only OS-level controls help: read-only bind mounts, a dedicated low-privilege service account, and not co-locating the app's own config or DataProtection key ring anywhere reachable. [primary]
- **TOCTOU (CWE-367).** Connapse validates the root once at config time, then a background service walks it every five minutes for the life of the process. A directory swapped for a symlink after validation wins. Atomic resolution at open time is the only real fix — Linux `openat2` with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS`; on .NET, re-resolve per file and refuse reparse points during enumeration. [primary]
- **Comparison correctness.** `root + separator` fixes the nginx class, but the comparison must be `OrdinalIgnoreCase` on Windows/macOS and ordinal on Linux, and `\\?\` device paths, UNC shares, trailing dots or spaces, and NTFS alternate data streams (`file::$DATA`) need rejecting before normalization. [primary]

**Empirical verification performed during this research.** Against .NET 7.6.4 on Windows, using the exact APIs in `ConnectorFactory.CombineUnderRoot` and `FilesystemConnector`:

| Step | Result |
|---|---|
| `mklink /J root\escape secret\` (no elevation required) | Junction created |
| `Path.GetFullPath(root\escape)` | Stays lexically under root — junction not resolved |
| `combined.StartsWith(rootFull + separator)` | **True — the guard passes** |
| `Directory.EnumerateFiles(root, "*", AllDirectories)` | **Returns `root\escape\keyring.txt`** — content from outside the root |

Both halves fail: the guard admits the path, and the walk reads through it. `FilesystemConnector.cs:79` uses the bare `SearchOption.AllDirectories` overload with no `EnumerationOptions`, so nothing skips reparse points.

### 3. Separating "declare" from "activate"

**Claim: three distinct separations appear across the products studied, and none of them is dry-run or approval workflow.**

1. **Channel-scoped authority** (Grafana): the filesystem is high-trust; the API structurally cannot produce the protected object shape.
2. **Reference, not embed** (Kubernetes `secretRef`, Vault mounts): the declarative object names a credential it cannot read. This maps almost exactly onto the existing Connapse connection/source split — a source declares `connectionId` and a prefix, never a `roleArn`.
3. **Split the verb** (Airbyte source-editor vs destination-editor; Vault sudo vs read): creating a *scope inside* an existing credential boundary is a materially smaller privilege than creating the boundary, and all four products treat it as a separate grant.

Notably, **none of the four products gates on dry-run, approval, or two-person rules.** That pattern lives in the Git pull request, not in the product. For a GitOps workflow, the PR review *is* the approval step — which means the product's job is to make the declarative artifact reviewable, not to build an approval engine. [primary/secondary]

### 4. Mutating tools on an MCP server

**Claim: read-only MCP is a strong default rather than a formal consensus, but every relevant guidance converges against putting configuration mutation on an agent-reachable surface.**

The MCP specification (rev 2025-06-18) states hosts "**must** obtain explicit user consent before invoking any tool" and that "tools represent arbitrary code execution and must be treated with appropriate caution" — while conceding the spec "cannot enforce these security principles at the protocol level." Consent is a *host* obligation, so a server relying on it is relying on a control it does not own. [primary]

The Security Best Practices page names **Scope Minimization** explicitly, listing "treating claimed scopes in token as sufficient without server-side authorization logic" as a common mistake. That is precisely Connapse's current state: `/mcp` has a single transport-level `RequireAgent` gate with no per-tool role checks, so any agent token that can call one tool can call all of them. [primary]

`readOnlyHint` and `destructiveHint` annotations exist as a risk vocabulary, but the official MCP blog (16 Mar 2026) is explicit that hints "are not enforcement mechanisms." [primary]

Willison's **lethal trifecta** — private data access, untrusted content, external communication — applies with unusual directness here: a RAG server *is* leg two by construction, since ingested documents are attacker-influenceable text delivered into the model's context. Adding a source-creation tool is worse than a pure exfiltration channel, because injected text could point a new source at an arbitrary path, making the config surface both an exfiltration channel and a corpus-poisoning channel. [secondary — domain-expert blog, 16 Jun 2025]

Documented incidents: **GitHub MCP** (Invariant Labs, 26 May 2025) — a malicious public issue drives the agent to read private repos and exfiltrate via a PR, which the researchers called architectural rather than a code bug; **Atlassian MCP** (Cato Networks, 19 Jun 2025) — an external support ticket carries the injection, executed with internal privileges; **Supabase MCP** (General Analysis, ~6 Jul 2025) — support-ticket injection plus a `service_role` key bypassing RLS, where the vendor's own mitigation was a `--read-only` flag. Asana's June 2025 cross-tenant leak was a tenant-isolation bug, **not** prompt injection, and should not be cited as one. [secondary/tertiary]

The two vendors hit hardest — Supabase and GitHub — both shipped read-only modes as the fix. No authoritative document states flatly that configuration-mutating operations must never be agent-reachable; the strongest support is the spec's consent requirement combined with scope minimization. [uncertainty flagged]

### 5. Constraining which bucket a source may name

**Claim: declare an allowed bucket/prefix list on the connection, backed by narrowly-scoped IAM — the allowlist is layered on top of a scoped credential, never a substitute for one.**

**Snowflake's storage integration is a near-exact precedent for the Connapse object model.** An admin-created integration holds the role ARN, and `STORAGE_ALLOWED_LOCATIONS` "explicitly limits external stages that use the integration to reference one or more storage locations (i.e. S3 bucket, GCS bucket, or Azure container)." A user-created stage's URL must fall inside that list. Snowflake shipped this *in addition to* the IAM role, not instead of it. [primary] Databricks Unity Catalog is the same shape with different nouns — a storage *credential* separated from external *locations*, where ordinary users are granted the location, not the credential. [primary]

The managed RAG analogues do **not** do this: Bedrock Knowledge Bases, Kendra, and Glue crawlers all rely on the IAM role as the entire boundary, with a per-data-source role scoped to one bucket. Bedrock's inclusion prefix is convenience, not security. [primary]

That difference is the crux, and it is what makes the allowlist necessary rather than redundant for Connapse: **those are managed services where each data source gets its own role, whereas Connapse shares one connection role across many user-created sources.** IAM alone therefore cannot distinguish one Connapse source from another.

**On session policies — the correction to the team's prior hypothesis.** Session policies passed to `AssumeRole` are mechanically sound: the resulting permissions are the *intersection* of role policy and session policy, so they can only restrict. But two limits disqualify them as the primary control. They apply **only when `roleArn` is set** — with ambient credentials and no assume-role there is nothing to attach one to. And **Azure has no equivalent**: managed-identity tokens cannot be narrowed at request time, leaving RBAC assignment scope as the only lever, with container-level `Storage Blob Data Reader` as Microsoft's recommended granularity. Inline session policies also cap at 2,048 characters. They remain worth using opportunistically on the AWS assume-role path, where they convert an application bug into an AWS-side denial. [primary]

`ExternalId` solves a *different* problem — cross-account confused deputy where another tenant supplies someone else's role ARN. For a self-hosted single-org deployment assuming a role in the same account, it adds little. [primary]

## Recommendation

**Per surface:**

| | Admin UI | REST / CLI | MCP |
|---|---|---|---|
| **Connections** (credential + root boundary) | Read + edit of pre-declared entries | **Read-only** | **Read-only** |
| **Sources** (scope inside a connection) | Full | **Full, admin-scoped role** | **Read-only** |

Connections come from configuration — appsettings/env, or a provisioning file — following Grafana's channel-scoped model and Elasticsearch's static `path.repo`. Sources are API-creatable because a source is a *reference* to a credential it cannot read, which is the Kubernetes `secretRef` shape, and because that is what IaC actually needs to declare. Split the verb per Airbyte: creating a source is a separate, lesser grant than creating a connection.

**Server-side constraints required regardless of surface:**

1. **`allowedRoot` selected by reference from a configured allowlist**, never supplied by a caller.
2. **Refuse reparse points during enumeration**, and re-resolve per file rather than trusting a one-time config-time check. The allowlist stops `allowedRoot: "/"`; it does not stop a symlink.
3. **`allowedLocations` on the connection** — a list of `bucket[/prefix]` or `container[/prefix]` validated on source create/update. Absent means deny, not allow-all.
4. **Keep IAM/RBAC narrow as the enforcing floor.** The allowlist is application config; IAM is what actually stops an application bug.
5. **Per-tool authorization on MCP**, not a single transport gate.

**IaC story:** sources are declarable in version control and applied through the admin-scoped REST API, so Terraform or a CLI apply works normally. Connections — the credential boundary — are declared in the deployment's own configuration, which the operator already version-controls alongside their compose file or Helm values. The Git pull request is the approval step, exactly as it is for the four products studied. Nothing about this requires the product to build a dry-run or approval engine.

## Conflicts and uncertainties

- **Managed RAG services contradict the recommendation.** Bedrock Knowledge Bases and Kendra use IAM as the sole boundary with no app-level allowlist, while Snowflake and Databricks add one. This is resolved by the per-data-source-role distinction rather than being a genuine disagreement, but it rests on that reasoning rather than on a source stating it directly.
- **No product was found that constrains bucket naming purely in app config with no IAM scoping.** The allowlist is always layered on a scoped credential. Confidence high; treat app-level allowlisting as defense in depth, never the only control.
- **No authoritative source says configuration mutation must never be agent-reachable.** The recommendation to keep it off MCP is an inference from the consent requirement, scope minimization, and vendor responses to real incidents.
- **Vault's `sudo` requirement for enabling a secrets engine is unverified** — the concepts page and the `/sys/mounts` API page disagree in emphasis.
- **Grafana's OSS default role for `datasources:create` is unconfirmed.**

## Gaps — what we did not find

- Elasticsearch/Logstash S3 input and Azure AI Search blob indexer were not verified; both are believed to follow the Kendra pattern but this is unconfirmed.
- No data on how often the Grafana `readOnly` bypass (issue #32556) was exploited in practice, or whether it is fixed.
- No peer-reviewed work was located on prompt-injection-driven configuration mutation specifically; the evidence is vendor advisories and security-researcher writeups.
- The empirical junction test was run on Windows only. The equivalent POSIX symlink behaviour for .NET on Linux was not tested, though `Path.GetFullPath` is lexical on all platforms so the guard bypass is expected to be identical.

## Source quality assessment

The core recommendation rests on primary sources: official documentation for Grafana, Airbyte, Kubernetes, Vault, Elasticsearch, PHP, Snowflake, Databricks, AWS, and Azure, plus the MCP specification, plus NVD/CVE entries. The MCP threat analysis leans more on secondary sources — a domain-expert blog and security-vendor writeups — because the incident record is where the evidence lives; these are marked inline. The single most consequential finding, the junction bypass, was verified empirically against the product's own APIs rather than taken from any source.

## Sources

**Primary**
- Grafana provisioning: https://grafana.com/docs/grafana/latest/administration/provisioning/
- grafana/grafana PR #19006: https://github.com/grafana/grafana/pull/19006
- grafana/grafana issue #32556: https://github.com/grafana/grafana/issues/32556
- Grafana datasource HTTP API: https://grafana.com/docs/grafana/latest/developers/http_api/data_source/
- Airbyte API access: https://docs.airbyte.com/platform/using-airbyte/configuring-api-access
- Airbyte RBAC: https://docs.airbyte.com/platform/access-management/rbac
- Kubernetes RBAC good practices: https://kubernetes.io/docs/concepts/security/rbac-good-practices/
- Vault policies: https://developer.hashicorp.com/vault/docs/concepts/policies
- Elasticsearch fs repository settings: https://www.elastic.co/docs/reference/elasticsearch/configuration-reference/fs-repository-settings
- PHP open_basedir: https://www.php.net/manual/en/ini.core.php#ini.open-basedir
- CVE-2015-5531: https://nvd.nist.gov/vuln/detail/CVE-2015-5531
- MCP specification 2025-06-18: https://modelcontextprotocol.io/specification/2025-06-18/index
- MCP Security Best Practices: https://modelcontextprotocol.io/specification/draft/basic/security_best_practices
- MCP tool annotations blog (16 Mar 2026): https://blog.modelcontextprotocol.io/posts/2026-03-16-tool-annotations/
- Snowflake CREATE STORAGE INTEGRATION: https://docs.snowflake.com/en/sql-reference/sql/create-storage-integration
- Databricks Unity Catalog cloud storage: https://docs.databricks.com/aws/en/connect/unity-catalog/cloud-storage/
- Bedrock Knowledge Bases permissions: https://docs.aws.amazon.com/bedrock/latest/userguide/kb-permissions.html
- Kendra IAM roles: https://docs.aws.amazon.com/kendra/latest/dg/iam-roles.html
- AWS AssumeRole session policies: https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_temp_control-access_assumerole.html
- AWS confused deputy: https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html
- Azure role assignment for blob data access: https://learn.microsoft.com/en-us/azure/storage/blobs/assign-azure-role-data-access
- OWASP MCP Security Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/MCP_Security_Cheat_Sheet.html
- SEI CERT FIO16-J: https://cmu-sei.github.io/secure-coding-standards/sei-cert-oracle-coding-standard-for-java/rules/input-output-fio/fio16-j

**Secondary**
- Willison, the lethal trifecta (16 Jun 2025): https://simonwillison.net/2025/Jun/16/the-lethal-trifecta/
- Willison, Supabase MCP (6 Jul 2025): https://simonwillison.net/2025/Jul/6/supabase-mcp-lethal-trifecta/
- Invariant Labs, GitHub MCP (26 May 2025): https://invariantlabs.ai/blog/mcp-github-vulnerability
- Cato CTRL, Atlassian MCP (19 Jun 2025): https://www.catonetworks.com/blog/cato-ctrl-poc-attack-targeting-atlassians-mcp/
- General Analysis, Supabase MCP: https://generalanalysis.com/blog/supabase-mcp-blog
- Elastic advisory CVE-2015-5531: https://discuss.elastic.co/t/elasticsearch-directory-traversal-vulnerability-cve-2015-5531/25737
