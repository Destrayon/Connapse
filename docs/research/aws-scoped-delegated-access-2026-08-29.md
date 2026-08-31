# Can AWS grant Connapse scoped, revocable access to a user's S3 data without a per-user credential?

**Date:** 2026-08-29
**Status:** Reviewed
**Built on:** no prior corpus material — the Connapse MCP knowledge base was not available in this session, so no prior research reports were consulted.

## Executive summary

Two assumptions behind Connapse's current AWS per-user permissions design are wrong, and correcting them removes the stored per-user refresh token entirely. First, the identity-enhanced session Connapse obtains was never able to reach "the user's whole account": AWS automatically attaches a deny-all-but-allowlist policy to any role session carrying IAM Identity Center identity context, and `s3:GetObject` is denied to that session. Second, discovering what a user is permitted to read does not require acting as that user — `ListAccessGrants` accepts a grantee filter and needs only `s3:ListAccessGrants` on Connapse's own role, whereas the `ListCallerAccessGrants` named in Connapse's existing plan is the one variant that requires being the user. Independently, every major enterprise search product surveyed, including AWS's own Amazon Q Business, captures access control lists at ingest and filters at query time, using identity propagation only to establish who is asking. The recommendation is to keep an Identity Center sign-in as a one-time identity proof, store the resulting directory user name rather than a token, and resolve scopes at query time with Connapse's own credentials.

That recommendation carries four costs that must be accepted deliberately rather than discovered later. Using `ListAccessGrants` to answer "what may this *other* user read" is an unsanctioned use of an administrative API — AWS documents `ListCallerAccessGrants` for that question and offers no behavioural guarantee for the substitute. Group membership is almost certainly not expanded by the grantee filter, so Connapse must resolve groups through the Identity Store itself and reproduce work AWS currently does for it. CloudTrail attribution is lost, because all S3 access becomes Connapse's role rather than a named directory user. And disabling a directory user no longer severs access on its own, since no per-user credential remains to expire, which makes an explicit user-status check mandatory rather than optional.

## Research brief

**Question:** Can AWS grant Connapse a narrowly-scoped, explicitly grantable and revocable capability to read only what it needs on a user's behalf, instead of Connapse holding a long-lived credential carrying the user's full identity?

**Sub-questions investigated:**

1. What does the S3 Access Grants model itself permit, and does it actually require per-user identity propagation?
2. How much authority does an identity-enhanced session really carry, and how can it be narrowed?
3. What non-identity mechanisms let an admin grant a Connapse principal narrow, revocable read access?
4. Does AWS offer any consent-shaped or externalised-authorization model that fits?
5. How do production enterprise search and RAG products enforce per-user document permissions?
6. In a multi-connector product, what is the provisioning UX — must a user act per source before searching it?

**Out of scope:** Azure and other non-AWS clouds as implementation targets; write permissions, since Connapse has no write surface against a source; the Cognito refresh token lifetime question, settled separately (rotation cannot extend a refresh token; only `RefreshTokenValidity`, up to 10 years, moves the deadline).

**Success criteria:** a recommendation on whether to keep or remove the per-user credential, with the security tradeoff stated explicitly and the UX consequence for future connectors resolved.

## Findings by sub-question

### 1. S3 Access Grants does not require per-user identity propagation

The S3 Access Grants service supports three grantee types — `DIRECTORY_USER`, `DIRECTORY_GROUP`, and `IAM` — and `CreateAccessGrant` states that "the grantee can be an IAM user or role or a directory user, or group" ([Grantee](https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_Grantee.html), [CreateAccessGrant](https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_CreateAccessGrant.html)) [PRIMARY]. Associating an Identity Center instance is optional: `IdentityCenterArn` on `CreateAccessGrantsInstance` is documented "Required: No", and Identity Center is needed only when grantees are directory identities ([CreateAccessGrantsInstance](https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_CreateAccessGrantsInstance.html), [concepts](https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-grants-concepts.html)) [PRIMARY].

The decisive finding for Connapse is the difference between two list operations. `ListAccessGrants` accepts `granteetype`, `granteeidentifier`, `grantscope`, `permission` and `application_arn` as filters and requires only `s3:ListAccessGrants` on the caller's own role ([ListAccessGrants](https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_ListAccessGrants.html)) [PRIMARY]. An application holding only its own IAM role can therefore enumerate the grants belonging to a named Identity Center user by passing that user's UUID, with no impersonation, no user token and no identity-enhanced session. By contrast `ListCallerAccessGrants` lists only the grants that grant *the caller* access ([ListCallerAccessGrants](https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_ListCallerAccessGrants.html)) [PRIMARY]. Connapse's plan in `docs/plans/search-permission-filtering.md` specifies the latter, which is the sole reason the design needs a per-user credential at all.

Three qualifications matter, and none of them appeared in the first draft of this report. **This is an unsanctioned use of an administrative API.** AWS documents `ListCallerAccessGrants` as the answer to "what may this user read"; nothing in the documentation sanctions `ListAccessGrants` as an authorization oracle for a third party, and AWS therefore offers no behavioural guarantee that the two stay semantically aligned. **Group membership is almost certainly not expanded.** `ListCallerAccessGrants` is documented as considering the caller's group memberships, whereas `granteeidentifier` on `ListAccessGrants` is a literal match against the grant record with no expansion language — so grants held by a user's groups will be missed unless Connapse resolves membership through the Identity Store API and queries for each group as well. **`ApplicationArn` cuts both ways.** Each grant carries an application ARN of `NA`, `ALL`, or a specific application, and "if the grant includes an application ARN, the grantee can only access the S3 data through this application" [PRIMARY]. An unfiltered `ListAccessGrants` therefore returns grants the user could never exercise through Connapse, which is over-permissive; filtering by `application_arn` drops the `ALL` and `NA` grants, which is under-permissive. Correct behaviour requires reading the field per grant and deciding, not filtering in the query.

`GetDataAccess` returns temporary credentials with `durationSeconds` between 900 and 43,200 seconds, defaulting to 3,600, at `READ`, `WRITE` or `READWRITE`, and scope granularity is bucket, prefix or individual object ([GetDataAccess](https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_GetDataAccess.html)) [PRIMARY]. Quotas: one instance per Region per account, 1,000 locations per instance, 100,000 grants per instance ([limitations](https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-grants-limitations.html)) [PRIMARY]. There is no end-user consent primitive anywhere in the Access Grants API — every grant is created by an admin holding `s3:CreateAccessGrant` [PRIMARY, negative finding].

### 2. The identity-enhanced session was never able to reach the user's whole account

This section corrects the premise that motivated the research. When a role session carries Identity Center identity context, AWS STS **automatically attaches** the managed policy `AWSIAMIdentityCenterAllowListForIdentityContext`, whose effect is `Deny` with a `NotAction` allowlist over `Resource: "*"` ([managed policy reference](https://docs.aws.amazon.com/aws-managed-policy/latest/reference/AWSIAMIdentityCenterAllowListForIdentityContext.html)) [PRIMARY]. The only S3 actions outside that Deny are `s3:GetAccessGrantsInstanceForPrefix`, `s3:GetDataAccess` and `s3:ListCallerAccessGrants`. `s3:GetObject` and `s3:ListBucket` are denied to the identity-enhanced session itself; it can only ask Access Grants for separately-scoped credentials. One caveat on durability: the policy is at version 12, last edited 2024-10-01. AWS revises it as services adopt trusted identity propagation, so any security argument resting on its contents needs a periodic re-check rather than a one-time reading.

The session's authority is therefore the intersection of the IAM role's policy, AWS's implicit allowlist, and the user's grants — never the union, and never the account. An admin has several controls Connapse cannot widen: `sts:SetContext` must be granted in the role trust policy or the call fails outright; trust-policy conditions can pin `identitystore:UserId`, `identitycenter:ApplicationArn` and `identitycenter:InstanceArn`, the last existing specifically to "help prevent an IAM role from being accessed by an unexpected application" ([condition keys](https://docs.aws.amazon.com/singlesignon/latest/userguide/condition-context-keys-sts-idc.html)) [PRIMARY]; and Identity Center application access scopes are admin-set.

On revocation: disabling or deleting a user blocks new sessions immediately, existing application sessions die within roughly 30 minutes at next refresh, and existing IAM role sessions run to expiry of up to 12 hours ([authentication concepts](https://docs.aws.amazon.com/singlesignon/latest/userguide/authconcept.html)) [PRIMARY]. Critically, **no documented general API mints Identity Center identity context for a named user without an artifact of that user's authentication.** All four `CreateTokenWithIAM` grant types require one. The single admin-initiated exception, QuickSight's `GetIdentityContext`, returns a QuickSight-specific context provider ARN, works only for QuickSight-native and IAM-federated users, is usable with three snapshot APIs, and explicitly redirects Identity Center users to the token flow ([GetIdentityContext](https://docs.aws.amazon.com/quicksight/latest/APIReference/API_GetIdentityContext.html)) [PRIMARY]. If Connapse wants identity context, it must hold a user credential; the finding in section 1 is that it does not need identity context.

### 3. Non-identity delegation mechanisms mostly fail on scale, not on principle

Assessed against four criteria — per-user least privilege, admin revocability, no stored user credential, and scaling to hundreds of users — the alternatives to Access Grants are weak. Bucket policies naming a Connapse principal hit a hard, non-adjustable 20 KB per-bucket limit ([S3 quotas](https://docs.aws.amazon.com/general/latest/gr/s3.html#limits_s3)) [PRIMARY]; at 300–600 bytes for a realistic per-user statement that is roughly 30–70 users. S3 Access Points scale to 10,000 per account per Region with a 20 KB policy each ([access point restrictions](https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-points-restrictions-limitations.html)) [PRIMARY], making one-access-point-per-user numerically viable but operationally heavy; S3 Object Lambda Access Points are closed to new customers as of 7 November 2025 ([change notice](https://docs.aws.amazon.com/AmazonS3/latest/userguide/amazons3-ol-change.html)) [PRIMARY] and cannot be built on. AWS RAM cannot share buckets or prefixes at all — the only S3 entry in its shareable-resources table is the Access Grants instance ([shareable resources](https://docs.aws.amazon.com/ram/latest/userguide/shareable.html)) [PRIMARY], so RAM is the cross-account plumbing *for* Access Grants rather than an alternative to it.

Cross-account roles with an `ExternalId` address the confused-deputy problem for a *shared multi-tenant* deputy ([confused deputy](https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html)) [PRIMARY]; a self-hosted Connapse running in the customer's own account is not one, so the ceremony adds little, and it carries no per-user dimension. IAM Roles Anywhere replaces Connapse's *server* credential using X.509 certificates, not the per-user grant ([introduction](https://docs.aws.amazon.com/rolesanywhere/latest/userguide/introduction.html)) [PRIMARY]. S3 ACLs, the only owner-granted primitive, are disabled by default under Bucket owner enforced ([object ownership](https://docs.aws.amazon.com/AmazonS3/latest/userguide/about-object-ownership.html)) [PRIMARY]. Presigned URLs cap at 7 days and cannot be revoked short of revoking the signing credential ([presigned URLs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html)) [PRIMARY]. ABAC via session tags is the one mechanism giving per-user differentiation without per-user AWS objects, limited to 50 session tags ([session tags](https://docs.aws.amazon.com/IAM/latest/UserGuide/id_session-tags.html)) [PRIMARY] — but the tags are asserted by whoever calls `AssumeRole`, so unless they arrive in a token Connapse did not mint, the least-privilege claim collapses into trusting Connapse anyway.

### 4. AWS has no consent primitive that fits, and its own RAG service does not use one

The only consent-shaped, admin-revocable per-user grant in the Identity Center stack is **application assignment**. It genuinely gates the `jwt-bearer` exchange — when assignments are required, the matched user must be assigned directly or via a group or the request is denied ([trusted token issuers blog](https://aws.amazon.com/blogs/security/simplify-workforce-identity-management-using-iam-identity-center-and-trusted-token-issuers/)) [SECONDARY] — but it is binary per application with no per-resource dimension, and AWS warns against toggling the setting on applications used with trusted identity propagation, citing "unexpected behavior, including disrupted user access" [PRIMARY]. `PutApplicationAccessScope`'s `AuthorizedTargets` is a list of other applications or the Identity Center instance, not users or resources ([PutApplicationAccessScope](https://docs.aws.amazon.com/singlesignon/latest/APIReference/API_PutApplicationAccessScope.html)) [PRIMARY].

**Amazon Q Business is the reference implementation and it points away from per-user credentials.** Its S3 connector does not read S3 as the user: an admin supplies an ACL configuration file mapping key prefixes to allow/deny entries over users and groups, captured at ingest ([S3 user management](https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/s3-user-management.html)) [PRIMARY]. ACL information is indexed alongside the document into a User Store and responses are filtered at query time against the caller's identity ([connector concepts](https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/connector-concepts.html)) [PRIMARY]. Trusted identity propagation is used to establish who the caller is, not to read source data ([SigV4 calls](https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/making-sigv4-authenticated-api-calls.html)) [PRIMARY]. Note that Q Business is closed to new customers and superseded by Amazon Quick, which keeps the model but inverts the default from allow to deny [PRIMARY].

Amazon Verified Permissions is a poor fit as a per-document entitlement store: `BatchIsAuthorized` is capped at 30 requests per second per policy store ([BatchIsAuthorized](https://docs.aws.amazon.com/verifiedpermissions/latest/apireference/API_BatchIsAuthorized.html)) [PRIMARY], and filtering 100 hits needs several batch calls. Lake Formation applies to Data Catalog databases, tables and columns, with no path to arbitrary unstructured objects [PRIMARY]. Cognito identity pools with `${cognito-identity.amazonaws.com:sub}` require data laid out by user prefix, which no enterprise S3 estate is, and the `sub` is the identity-pool identity ID rather than the directory user's identifier [PRIMARY]. The one true three-legged consent flow in AWS is Bedrock AgentCore Identity, whose managed Token Vault is precisely the per-user token store this research is trying to eliminate ([runtime OAuth](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/runtime-oauth.html)) [PRIMARY].

### 5. Storing per-user source credentials for retrieval is the industry exception, not the norm

No major enterprise search vendor surveyed stores per-user source-system refresh tokens for retrieval. Glean ingests the permission model at crawl time and evaluates the signed-in user against mirrored permissions at query time ([security principles](https://docs.glean.com/security/security-principles)) [PRIMARY]. Elastic runs a separate access-control sync into hidden `.search-acl-filter-*` indices, matching a user's access-control document against each document's `_allow_access_control` field ([DLS overview](https://www.elastic.co/docs/reference/search-connectors/es-dls-overview)) [PRIMARY]. Azure AI Search documents the exact pattern Connapse already implements — a filterable `group_ids` collection filtered at query time — and recommends it for a "Custom identity system, non-Microsoft security framework, or any push-model index" ([security trimming](https://learn.microsoft.com/en-us/azure/search/search-security-trimming-for-azure-search)) [PRIMARY]. Google's Vertex AI Search embeds `acl_info` with reader principals in ingested data ([data source access control](https://docs.cloud.google.com/generative-ai-app-builder/docs/data-source-access-control)) [PRIMARY]. Microsoft Graph connectors write an ACL array onto each external item and explicitly advise against expanding group membership into item ACLs ([manage items](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-items)) [PRIMARY].

The trade is known in the field as early binding (ACLs in the index) versus late binding (check the source at query time). Sinequa argues against late binding on both performance and correctness grounds, warning it affects "the consistency of pagination, metadata counts, and many other navigation features" [SECONDARY, vendor blog]. A further argument matters more for Connapse: even a vendor sympathetic to user-credential passthrough concedes it cannot serve a vector index, because source APIs do not offer semantic retrieval — Paragon notes "Google Drive only supports keyword matching over file content" [SECONDARY]. A per-user credential can therefore only re-validate results the index already produced; it cannot produce them.

The main documented hazard of the ACL-at-ingest pattern is failing open. Elastic states plainly that "If a content document does not have access control fields, there will be no restrictions on who can view it" [PRIMARY], and AWS warns that with ACL crawling disabled, indexed content "will be considered accessible to users with access to the Amazon Q Business application" [SECONDARY, AWS blog].

### 6. No surveyed product requires an end-user action per source before searching it

Across Amazon Q Business, Microsoft 365 Copilot connectors, Google Agentspace, Glean, Elastic, Azure AI Search and Onyx, identity is mapped once, admin-side, and no end-user action is required for retrieval after an admin adds a data source. The mechanisms differ: Q Business derives aliases automatically from crawls and joins on email, case-insensitively and collapsing subaddresses, with `CreateUser`/`UpdateUser` as an admin override ([user store](https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/principal-store-hiw.html)) [PRIMARY]; Microsoft has the admin write a single mapping formula such as `{0}.{1}@contoso.com` with a five-user preview, noting "Only one mapping is supported for all users" and that it cannot be changed after the connection is published ([map Entra ID](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/map-entra-id)) [PRIMARY]; Google configures one identity provider per project and location [PRIMARY]; Glean separates single sign-on from people data, both admin-configured [PRIMARY].

Per-user OAuth survives only for **write actions**, not retrieval: Q Business plugins that write into Salesforce or Zendesk do redirect each user through OAuth on first use ([plugins](https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/using-plugins.html)) [PRIMARY]. The clearest statement of why per-user consent is undesirable comes from Anthropic's Enterprise Managed Auth documentation: "There is no browser redirect and no per-connector consent page. From the user's point of view, the connector is simply available as soon as their administrator enables it" ([Enterprise Managed Auth](https://claude.com/docs/connectors/building/enterprise-managed-auth)) [PRIMARY]. That is a vendor that shipped per-user OAuth first and then engineered a path out of it.

Permission-freshness cadences vary and are the main operational risk. Microsoft Copilot connectors sync ACLs on full crawls only — incremental crawls, defaulting to 15 minutes, do not pick up ACL changes, so a revocation can sit unenforced until the daily full crawl [PRIMARY]. Glean runs identity crawls roughly hourly, separate from content crawls [PRIMARY, cadence figures single-sourced]. Amazon Q refreshes ACLs only on data-source sync and instructs admins to "re-sync your data source regularly" [PRIMARY]. Azure AI Search's live token check has no staleness window at all [PRIMARY]. Q Business ships an **ACL Analyzer** that takes a document ID and user ID and reports whether access exists plus all aliases for that user ([ACL Analyzer](https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/acl-analyzer.html)) [PRIMARY] — a concession that alias-mapping failures are common enough to need a dedicated debugger. No vendor documents an end-user-facing banner for a source whose permissions failed to sync; the universal behaviour is silent fail-closed, which is indistinguishable from an empty index.

## Recommendation

Keep the Identity Center sign-in, but change what it produces. Today it yields a credential Connapse stores and replays; instead it should yield a one-time attestation that the Connapse account belongs to a named directory user. Connapse stores that name — which `UserAwsIdentityLinkEntity.DirectoryUserName` already holds — and thereafter resolves scopes at query time by calling `ListAccessGrants` filtered by grantee with its own role, expanding the user's groups through the Identity Store API. Nothing per-user expires, the 30-day problem disappears, `RefreshTokenValidity` becomes irrelevant, and new grants take effect on the next search with no user action.

The security objection — that this moves enforcement from AWS's STS into Connapse's own code — does not hold **for the search path specifically**, and the argument should be confined to that claim rather than generalised. Search results are drawn from Connapse's pgvector index and never fetched from S3 per query, so the identity-enhanced session was never in the query path at all. Connapse already reads every indexed document with its own credentials during ingestion, and `DocumentsEndpoints.cs:296` serves original file content through the container's connector using those same credentials. Under both the current and the proposed design, therefore, `SearchScopes` and its deliberate distinction between "this deployment does not filter" and "this user reaches nothing" is the real boundary. That reasoning justifies this one change; it is not a general licence to move enforcement inward, and it would not survive if Connapse ever served search results by fetching from S3 at query time.

**Four costs come with the change and need deliberate handling.** *Audit attribution is lost:* AWS presents trusted identity propagation partly as letting admins "audit who accessed what data" through CloudTrail, and under this design every S3 read is attributed to Connapse's role instead of a named directory user. Connapse's own audit log sits inside the trust boundary being relaxed, so it is not an equivalent substitute. *Revocation changes shape:* deleting a grant now takes effect on the next query, which is better than the current 12-hour worst case, but disabling the directory user no longer severs anything by itself, because no per-user credential remains to expire. The resolver must check the Identity Store user's status explicitly and fail closed when the user is disabled or absent. *Access Grants is not the whole truth:* grants are invisible to bucket policies, identity-based IAM policies and KMS key policies, and vice versa. This design assumes the S3 estate is modelled in Access Grants, and a deployment where access is expressed some other way will filter incorrectly — that assumption should be stated to administrators, not implied. *`ApplicationArn` must be honoured per grant*, as set out in the findings above, or the filter is over-permissive in the common case.

For future connectors, the same shape generalises: match on email as the join key with case and subaddress normalisation, keep an alias table mapping one Connapse user to N per-source identities with an admin override, give identity and group refresh its own schedule separate from the content crawl, and build the equivalent of the ACL Analyzer early, because silent fail-closed plus a mis-mapped alias looks exactly like an empty index.

## Conflicts and uncertainties

**Group expansion by `ListAccessGrants` almost certainly does not happen.** Adversarial review narrowed this from undetermined to near-settled: `ListCallerAccessGrants` is documented as considering the caller's group memberships, while `granteeidentifier` on `ListAccessGrants` is described as a literal match against the grant record, with no expansion language anywhere. Connapse must therefore resolve group membership through the Identity Store API and query per group. This is load-bearing and should still be confirmed empirically, since the finding rests on the *absence* of expansion language rather than an explicit statement.

**Using `ListAccessGrants` as an authorization oracle is undocumented, not merely undocumented-in-detail.** AWS provides `ListCallerAccessGrants` for the question "what may this user read". Substituting the administrative list operation is a design decision Connapse takes on its own authority; the two operations could diverge in a future revision without that being a breaking change from AWS's point of view.

**The S3 access scope string is resolved.** AWS EMR documentation gives `s3:read_write`, but the S3 directory-identities documentation specifies `s3:access_grants:read_write`, which is what Connapse's setup script already writes. No longer an open conflict.

**S3 Access Grants pricing is not confirmed.** The AWS S3 pricing page did not return an Access Grants rate across two fetches. An AWS regional pricing page confirms the line item "S3 Access Grants Requests (per 1,000 requests)" exists, but the commonly cited figure of $0.03 per 1,000 requests rests only on aggregators [TERTIARY]. If scopes are resolved per query this becomes a real per-query cost and should be cached.

**Application-assignment revocation latency is undocumented.** No AWS source states how quickly unassigning a user takes effect on subsequent token exchanges. Treat in-flight revocation as not guaranteed.

**Injected instruction text in fetched documentation.** Three subagents independently reported that `docs.aws.amazon.com` pages returned a trailing block titled "Skills for AI coding assistants (optional)" instructing the reader to run `aws agent-toolkit search-skills`. No agent acted on it. It is recorded here because it is content arriving through a tool that is addressed to an automated reader, and future research runs against AWS documentation will encounter it.

## Gaps — what we did not find

- No AWS documentation on whether the auto-attached `AWSIAMIdentityCenterAllowListForIdentityContext` policy can be overridden or extended.
- No documented per-bucket limit on S3 access points; the published quota is per account per Region only, so "unlimited per bucket up to the account quota" is inferred rather than stated.
- No vendor in the survey publishes a quantified staleness service level for permission propagation. Azure comes closest by admitting a lag exists without bounding it.
- No AWS security-blog treatment of the self-hosted, customer-owned-deputy case; the assessment that `ExternalId` is a poor fit for Connapse is a reading of the multi-tenant premise in the confused-deputy documentation, not an explicit AWS statement.
- Vectara's ACL propagation model could not be established from its documentation.
- Google Gemini Enterprise does expose `acquireAndStoreRefreshToken`; whether that is confined to agent actions rather than retrieval is inferred from adjacent documentation and needs one more primary confirmation.

## Source quality assessment

The load-bearing claims rest on primary sources. The two findings that overturn the original premise — the auto-attached allowlist policy and the `ListAccessGrants` grantee filter — each come from AWS API and managed-policy reference pages, which are the strongest tier available. The industry survey rests on vendors' own product documentation throughout, with two secondary sources used only for argumentation rather than fact: Sinequa's early-versus-late-binding blog and Paragon's assessment of user-credential passthrough. One important claim, that application assignment gates the token exchange, rests chiefly on an AWS security blog rather than API reference, and is flagged accordingly. Pricing is the weakest area, resting on tertiary aggregators, and is marked unresolved rather than reported as fact. Claims about Connapse's own behaviour were verified by reading the repository directly rather than inferred.

## Sources

**Primary — AWS API and service documentation**
- https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_Grantee.html
- https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_CreateAccessGrant.html
- https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_CreateAccessGrantsInstance.html
- https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_ListAccessGrants.html
- https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_ListCallerAccessGrants.html
- https://docs.aws.amazon.com/AmazonS3/latest/API/API_control_GetDataAccess.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-grants-concepts.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-grants-directory-ids.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-grants-limitations.html
- https://docs.aws.amazon.com/aws-managed-policy/latest/reference/AWSIAMIdentityCenterAllowListForIdentityContext.html
- https://docs.aws.amazon.com/singlesignon/latest/userguide/condition-context-keys-sts-idc.html
- https://docs.aws.amazon.com/singlesignon/latest/userguide/trustedidentitypropagation-identity-enhanced-iam-role-sessions.html
- https://docs.aws.amazon.com/singlesignon/latest/userguide/authconcept.html
- https://docs.aws.amazon.com/singlesignon/latest/OIDCAPIReference/API_CreateTokenWithIAM.html
- https://docs.aws.amazon.com/singlesignon/latest/APIReference/API_OidcJwtConfiguration.html
- https://docs.aws.amazon.com/singlesignon/latest/APIReference/API_PutApplicationAccessScope.html
- https://docs.aws.amazon.com/quicksight/latest/APIReference/API_GetIdentityContext.html
- https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/principal-store-hiw.html
- https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/connector-concepts.html
- https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/s3-user-management.html
- https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/acl-analyzer.html
- https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/using-plugins.html
- https://docs.aws.amazon.com/verifiedpermissions/latest/apireference/API_BatchIsAuthorized.html
- https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/runtime-oauth.html
- https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html
- https://docs.aws.amazon.com/IAM/latest/UserGuide/id_session-tags.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/access-points-restrictions-limitations.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/amazons3-ol-change.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/about-object-ownership.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html
- https://docs.aws.amazon.com/ram/latest/userguide/shareable.html
- https://docs.aws.amazon.com/rolesanywhere/latest/userguide/introduction.html
- https://docs.aws.amazon.com/general/latest/gr/s3.html#limits_s3
- https://docs.aws.amazon.com/cognito/latest/developerguide/amazon-cognito-user-pools-using-the-refresh-token.html

**Primary — other vendors**
- https://docs.glean.com/security/security-principles
- https://docs.glean.com/administration/identity/people-data/troubleshooting/sso-vs-people-data
- https://www.elastic.co/docs/reference/search-connectors/es-dls-overview
- https://www.elastic.co/docs/reference/search-connectors/es-connectors-security
- https://learn.microsoft.com/en-us/azure/search/search-security-trimming-for-azure-search
- https://learn.microsoft.com/en-us/azure/search/search-document-level-access-overview
- https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-items
- https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/map-entra-id
- https://docs.cloud.google.com/generative-ai-app-builder/docs/data-source-access-control
- https://docs.onyx.app/admins/connectors/overview
- https://claude.com/docs/connectors/building/enterprise-managed-auth

**Secondary**
- https://aws.amazon.com/blogs/security/simplify-workforce-identity-management-using-iam-identity-center-and-trusted-token-issuers/
- https://aws.amazon.com/blogs/machine-learning/enable-or-disable-acl-crawling-safely-in-amazon-q-business/
- https://aws.amazon.com/blogs/machine-learning/restrict-access-to-sensitive-documents-in-your-amazon-quick-knowledge-bases-for-amazon-s3/
- https://www.sinequa.com/resources/blog/data-access-security-management-the-enterprise-search-challenge/
- https://www.useparagon.com/learn/permissions-access-control-for-production-rag-apps/

**Tertiary — used only for flagged, unconfirmed claims**
- Aggregator reports of S3 Access Grants pricing at $0.03 per 1,000 requests (unverified)
