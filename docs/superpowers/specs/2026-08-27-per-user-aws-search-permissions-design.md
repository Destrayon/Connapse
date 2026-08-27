# Per-user AWS search permissions — design

Tracking: [#421](https://github.com/Destrayon/Connapse/issues/421), phases 5 and 6.
Research: `docs/research/per-user-search-permission-design-2026-08-27.md`.

## Goal

An administrator decides, in AWS, which documents each user may read. Connapse honours that
decision when the user searches. Nothing else.

Connapse never authors a permission, never stores a copy of one, and never decides anything. It
reads the administrator's decisions at query time and narrows the search accordingly.

## The control surface belongs to AWS

The administrator's decisions are **S3 Access Grants**. A grant names a grantee — an IAM Identity
Center directory user or group — and a scope, which may be a bucket, a prefix, or a single object.
That is the whole vocabulary the administrator needs, and it lives in the AWS console, the CLI, or
CloudFormation. Changing a grant changes what Connapse returns on the user's next search.

This is a deliberate consequence of a standing architectural decision: the cloud provider is the
source of truth and there is no shadow permission table. It also means Connapse honours only
permissions expressed as Access Grants. Access controlled solely by IAM policies or bucket policies
is invisible here, because no AWS API can enumerate those per principal — `SimulatePrincipalPolicy`
requires the resource ARNs you are trying to discover and refuses federated principals, and IAM
Access Analyzer is bucket-level and scan-based. A deployment that has not adopted Access Grants sees
an empty grant list, which the failure taxonomy below reports as configuration rather than denial.

## Why an identity provider is involved at all

To ask "what may *this person* read", Connapse needs an AWS session that carries the individual
user rather than a shared role. That is an identity-enhanced session, and it is reachable only
through IAM Identity Center's jwt-bearer grant, which requires a **trusted token issuer**: an OIDC
provider with a publicly reachable discovery endpoint and RS256 keys.

Connapse cannot be that issuer. It is deliberately not an OIDC provider, its own tokens are HS256,
and a self-hosted instance usually has no public URL. Two cheaper routes were tested against a live
AWS account on 2026-08-27 and both are closed: a device-flow token cannot be exchanged, because a
public client cannot obtain a token scoped to a customer-managed application, and the
`authorization_code` grant is rejected outright by `PutApplicationGrant`.

The resolution is that the **customer hosts the identity provider inside their own AWS account**.
An Amazon Cognito user pool is an OIDC provider, is publicly reachable, and signs with RS256. It
federates back to Identity Center over SAML, so Identity Center remains the only user directory:
people sign in with their existing Identity Center credentials and Cognito provisions a matching
profile automatically. No third-party vendor, no second set of passwords. Cost is 50 federated
monthly active users free per account, then $0.015 each.

## Architecture

```
Connapse ──redirect──▶ Cognito ──SAML──▶ IAM Identity Center
   ▲                      │                      │
   └──── ID token ────────┘              (user authenticates)

Connapse ──jwt-bearer──▶ CreateTokenWithIAM ──▶ identity context
   └──▶ AssumeRole(ProvidedContexts) ──▶ ListCallerAccessGrants ──▶ URI prefixes
   └──▶ prefix predicate pushed into the SQL query
```

Enforcement is unchanged from phase 4. `ISearchScopeResolver` already exists, both search stores
already push a prefix predicate, and `documents.resource_uri` already records each document's
absolute S3 location. This design supplies a real resolver behind that seam.

## Components

### 1. Grant-scope normalisation — `Connapse.Core`

**Purpose.** Convert an S3 grant scope into a URI prefix that `SearchScopes` can match.

**Why it is its own unit.** AWS returns grant scopes in at least four shapes, documented
inconsistently across two pages: `s3://bucket*` (no separating slash), `s3://bucket/*`,
`s3://bucket/prefix*`, and forms with no trailing asterisk. The slashless bucket form is an
authorization bug waiting to happen — stripping the asterisk and matching `LIKE 's3://bucket%'`
also matches `s3://bucket-secrets/`. Object-scoped grants must match by equality, or a grant for
`report.pdf` also admits `report.pdf.bak`.

This sits beside `SearchScopes.ToLikePattern` in Core for the same reason `ResourceUri` does: the
format is decided in one place and consumed in another, and this repository has been bitten three
times by that drift.

**Interface.** `GrantScope.ToUriPrefix(string grantScope, bool isObject) -> string` plus a
matching-mode flag so the predicate knows whether to compare by prefix or equality.

**Depends on.** Nothing.

### 2. Cognito OIDC sign-in — `Connapse.Identity`

**Purpose.** Authenticate the user against the customer's Cognito pool and obtain an ID token whose
`email` claim maps to their Identity Center user.

**Detail.** Standard `Microsoft.AspNetCore.Authentication.OpenIdConnect`, scopes
`openid email offline_access`. The trusted token issuer maps `ClaimAttributePath=email` to
`IdentityStoreAttributePath=emails.value`; AWS permits only user name, email, or external ID, so the
opaque OIDC `sub` cannot be the join key. Connapse must therefore treat the email claim as
security-relevant: only verified emails are accepted, and a changed email invalidates the link.

Callback URLs registered are the deployment's HTTPS base URL and `http://localhost:<port>`. Cognito
requires HTTPS for anything else, so plain-HTTP multi-user deployments are unsupported for this
feature and the provider page says so rather than failing obscurely.

**Depends on.** Provider configuration (component 4).

### 3. Token store and refresh — `Connapse.Identity`

**Purpose.** Make the connection permanent, so a user consents once and never again.

**Detail.** A new table holds one Cognito refresh token per user, encrypted with ASP.NET Core Data
Protection. The key ring is persisted via `PersistKeysToDbContext` against the existing identity
context — without persistence, every stored token becomes undecryptable on container restart.

Every resolution mints a **fresh** ID token, because Identity Center rejects any token it has
already exchanged. The rotated refresh token is persisted before the exchange is attempted, so a
crash between the two loses a resolution rather than the link. A weekly background job exercises each
stored token so provider idle-expiry clocks never fire; that is what turns "long-lived" into
"permanent in practice".

Consent for `offline_access` is captured on the integrations page, which exists to show connection
status and offer revocation — not as a separate connection wizard. Sign-in establishes identity;
the page only governs whether Connapse may act while the user is away, which is what background
agent runs need.

**Depends on.** Component 2.

### 4. Provider setup and detection — `Connapse.Web`

**Purpose.** Let an administrator provision the AWS side and see honestly whether it worked.

**Detail.** Two artifacts, because the AWS surface is split. A **CloudFormation stack** covers the
Cognito user pool, domain and app client, and the S3 Access Grants instance, location and location
role. A **CLI script** covers the Identity Center trusted token issuer, SAML application,
customer-managed OAuth application, its grants, access scope and authentication method — none of
which have CloudFormation resource types. With an organization instance these must run in the
management or delegated-admin account.

Detection polls read-only calls and reports per component. The strongest single check is
`GetAccessGrantsInstance.IdentityCenterInstanceArn` equalling the ARN from `ListInstances`, which
collapses three setup steps into one green or red signal; the `sso-admin` Get operations then narrow
amber to a specific missing piece.

Two diagnostics exist because their absence produces silent emptiness: whether an Access Grants
instance exists at all, and whether the user's grants carry an `ApplicationArn` naming a *different*
Identity Center application — `CreateAccessGrant` documents that such a grant is usable only through
that application, so grants scoped to another app are invisible to Connapse and look identical to no
access.

**Depends on.** Nothing at runtime.

### 5. The resolver — `Connapse.Storage`

**Purpose.** Answer "what may this user read" for one search.

**Detail.** `CognitoTipScopeResolver : ISearchScopeResolver`. Per resolution: mint a fresh ID token;
`CreateTokenWithIAM` under the jwt-bearer grant; `AssumeRole` with `ProvidedContexts` naming
`arn:aws:iam::aws:contextProvider/IdentityCenter`; then `ListCallerAccessGrants` **once per
(account, region) pair** the deployment holds sources for, because the API is scoped to one Access
Grants instance per call and only one instance may exist per region per account. Pass
`allowedByApplication` server-side rather than filtering `ApplicationArn` client-side. Keep `READ`
and `READWRITE`, discard `WRITE`. Convert scopes through component 1.

Resolved scopes are cached 60 seconds per user. Errors are cached for a few seconds only: not
caching them means one multi-call chain per search per user against undocumented throttling, while
caching them for a full minute locks a user out on one transient failure. Sixty seconds is
defensible rather than arbitrary — AWS's own S3 Access Grants plugin caches credentials for about 54
minutes, and a vended credential freezes an authorization decision for up to 12 hours, so zero
staleness does not exist on any path. Set the assumed role's maximum session duration to one hour so
the outstanding-credential window stays small.

**Depends on.** Components 1, 3, 4.

## Failure states

Four states, distinguishable, never collapsed into one another:

| State | Meaning | Behaviour |
|---|---|---|
| Not configured | No resolver registered | `SearchScopes.Unrestricted` — today's behaviour |
| No grants | Resolver answered, user has none | Empty results **and** a configuration message |
| Resolver error | Timeout, throttle, revoked token | Fail closed, distinguishable error, not empty results |
| Document predates filtering | `resource_uri` is null | Re-sync prompt naming the sources involved |

The third and fourth states matter most. Failing closed follows XACML 3.0 §7.2.2 — a deny-biased
policy enforcement point denies without an explicit permit — but a user must never be told "you have
no access" when the truth is "this deployment has no Access Grants instance" or "these documents
were indexed before coordinates were recorded".

## Two things that must happen before filtering can be switched on

**Re-sync for `resource_uri`.** Both enforcement predicates gate on `resource_uri IS NOT NULL`.
Every document indexed before migration `20260827062208_AddDocumentResourceUri` has a null value and
would vanish the moment filtering is enabled, indistinguishably from a denial. Phase 2 declined a
SQL backfill on purpose — the derivation is silently wrong for a re-pointed source — so the remedy
is a forced re-sync per source, and the admin UI must name which sources still hold documents with
no recorded coordinate.

**A null-principal rule.** `ISearchScopeResolver.ResolveAsync` takes a nullable user id, null when
the caller is not a person — the ordinary state for the MCP server and for personal-access-token
callers. `Unrestricted` there would reopen the hole for anyone holding a token; `None` would break
MCP and every background agent run. The rule: once filtering is configured, a surface that cannot
name a user returns `None`. MCP and PAT surfaces must therefore resolve a principal before a
deployment can enable filtering, and the provider page refuses to enable it until they do.

## Security notes

- Email is a join key into an authorization decision. Accept only verified emails; treat an email
  change as revocation of the link.
- The only documented access scope is `s3:access_grants:read_write`. A search-only product asking
  for a write-capable scope should say so on the consent screen rather than hope nobody reads it.
- Stored refresh tokens are per-user secrets at rest. Data Protection with a persisted key ring is
  the minimum; the key ring itself must not sit in an ephemeral container layer.
- Identity Center kills existing application sessions within about 30 minutes of a user being
  disabled or deprovisioned, at their next refresh attempt. The remaining exposure is an outstanding
  STS credential, bounded by the role's maximum session duration.

## Sequencing

- **5a — Enforcement correctness.** Grant-scope normalisation, the four failure states, the
  null-principal rule, the re-sync. No cloud dependency; ships alone; prevents the corpus-vanishing
  bug.
- **5b — Sign-in and token store.** Components 2 and 3.
- **5c — Provider setup and detection.** Component 4, opening with a spike that stands up a Cognito
  pool and proves the jwt-bearer exchange returns an identity context before the UI is built.
- **5d — The resolver.** Component 5.
- **6 — Enable.** Register the resolver, integration tests across all four surfaces.

## Testing

Unit tests carry the parts that can be tested honestly: all four grant-scope shapes including the
slashless bucket form and the cross-bucket leak it causes, object-versus-prefix matching, and the
four failure states through a resolver double. Integration tests prove user A's search excludes user
B's documents at every surface, against real SQL, because a unit test of a pattern string cannot
catch a missing `ESCAPE` clause.

The AWS chain cannot be integration-tested without a live account. It sits behind a thin adapter
with contract tests against recorded response shapes, and real verification is a manual run of the
5c spike.

## Risks

**Nobody has driven this exact chain end to end.** AWS documents Cognito as a trusted token issuer
for Amazon Q Business, not for a custom application. The identity context is the same artifact
regardless of which issuer produced it, which is the entire point of the trusted-token-issuer
abstraction, but that is reasoning rather than evidence. The 5c spike exists to close this before
any UI is built. This session has twice shown that the gap between an AWS document and AWS behaviour
is real: `PutApplicationGrant` lists `authorization_code` as valid and rejects it, and
`RegisterClient` documents an `authorizationEndpoint` it does not return.

**The setup burden is substantial** — Identity Center with provisioned users, a Cognito pool, SAML
federation, a trusted token issuer, a customer-managed application, an Access Grants instance and
location, and grants authored per user or group, with Identity Center and Access Grants in the same
region. Most of it is one-time and scriptable, but a customer who abandons setup halfway leaves a
half-provisioned account, so the detection page must be legible enough to resume from.

**Multi-region deployments are constrained.** One Access Grants instance per region per account, and
the Identity Center instance must share its region. Buckets spanning regions need an instance per
region, and only organization instances replicate across regions — so multi-region is effectively
organization-instance-only.
