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

Once, when a signed-in user connects their AWS identity from the integrations page:

```
Connapse ──authorization code + PKCE──▶ Cognito ──SAML──▶ IAM Identity Center
   ▲                                       │                      │
   └──── ID token + refresh token ─────────┘            (user authenticates)
                    │
                    └──▶ refresh token stored, encrypted, against the Connapse user
```

Then on every search, with the user absent or present:

```
stored refresh token ──▶ a fresh Cognito ID token
   └──▶ CreateTokenWithIAM, jwt-bearer ──▶ identity context
   └──▶ AssumeRole(ProvidedContexts) ──▶ ListCallerAccessGrants ──▶ URI prefixes
   └──▶ prefix predicate pushed into the SQL query
```

Connapse sign-in is not part of either diagram. People log in as they do today.

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

**Interface.** `GrantScope.Parse(string grantScope, bool isObjectScope = false) -> GrantMatch`,
where `GrantMatch` is `readonly record struct GrantMatch(string Value, bool IsExact)` — the value to
compare against, and whether the comparison is equality or prefix.

**Depends on.** Nothing.

### 2. Connecting an AWS identity — `Connapse.Identity`

**Purpose.** Let an already-signed-in Connapse user prove which AWS identity they are, and leave
Connapse holding what it needs to prove it again later without them.

**Connapse sign-in is untouched.** This is an integration, not an authentication method. People log
in exactly as they do today; Cognito is what a logged-in user *connects*, from the integrations page,
the same shape as the existing AWS device-flow link. That rules out a fourth authentication scheme,
any change to the scheme router, and the whole class of questions about what happens when a
federated identity has no Connapse account — the account is already there and already signed in.
Tying Connapse sign-in itself to Cognito is a separate, later decision.

**Detail.** A plain OAuth 2.0 authorization-code flow with PKCE that Connapse drives: a redirect to
the pool's `/oauth2/authorize`, a callback endpoint, and a token exchange at `/oauth2/token`. Not
`AddOpenIdConnect` — that is a sign-in handler, and sign-in is not what this does. Scopes are
`openid email offline_access`; `offline_access` is what makes the connection outlive the visit.

**This requires a Cognito user pool domain.** The hosted `/oauth2/authorize` endpoint only exists on
a pool with one. The token *exchange* against Identity Center does not need a domain — the spike
confirmed that — but this flow does, so the domain belongs in the setup artifacts.

The trusted token issuer maps `ClaimAttributePath=email` to `IdentityStoreAttributePath=emails.value`;
AWS permits only user name, email, or external ID, so the opaque OIDC `sub` cannot be the join key.
Connapse must therefore treat the email claim as security-relevant: only verified emails are
accepted, and a changed email invalidates the link.

Callback URLs registered are the deployment's HTTPS base URL and `http://localhost:<port>`. Cognito
requires HTTPS for anything else, so plain-HTTP multi-user deployments are unsupported for this
feature and the provider page says so rather than failing obscurely.

**Depends on.** Provider configuration (component 4).

### 3. Token store and refresh — `Connapse.Identity`

**Purpose.** Make the connection permanent, so a user consents once and never again.

**Detail.** A new table holds one Cognito refresh token per user, encrypted with ASP.NET Core Data
Protection.

**The key ring is already persisted correctly** — `Program.cs` writes it to
`appdata/DataProtection-Keys`, which maps to the named `appdata` Docker volume, so keys survive
container restarts and no change is needed. (An earlier draft of this spec called for
`PersistKeysToDbContext`; that would have been churn.) One thing to weigh before storing per-user
secrets under it: the key ring is not itself encrypted at rest, so anyone with access to that volume
can decrypt every stored refresh token. `ProtectKeysWithCertificate` is the lever if that is not
acceptable, and it is a deployment decision rather than a code one.

Every resolution mints a **fresh** ID token, because Identity Center rejects any token it has
already exchanged. The rotated refresh token is persisted before the exchange is attempted, so a
crash between the two loses a resolution rather than the link. A weekly background job exercises each
stored token so provider idle-expiry clocks never fire; that is what turns "long-lived" into
"permanent in practice".

The integrations page is where the whole thing lives: connect, see status, disconnect. Connecting is
what grants `offline_access`, and that is what lets Connapse resolve a user's permissions while they
are away — which is what a background agent run needs. Disconnecting revokes it and deletes the
stored token.

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
| No cloud coordinate | `resource_uri` is null | Not governed by cloud permissions at all — falls back to Connapse's own access control (container/source reachability), same as before cloud filtering existed |

The third state matters most among the AWS-dependent ones. Failing closed follows XACML 3.0 §7.2.2 —
a deny-biased policy enforcement point denies without an explicit permit — but a user must never be
told "you have no access" when the truth is "this deployment has no Access Grants instance". The
fourth state is not a failure at all: a document with no cloud coordinate is simply outside the
cloud's permission model, so cloud filtering has nothing to say about it and does not exclude it.

### Design correction (2026-08-27)

The row above originally read "Document predates filtering | `resource_uri` is null | Re-sync prompt
naming the sources involved", treating a null `resource_uri` as **denied**. Real-deployment testing
against issue #421 found 127,898 documents, of which 127,880 were uploads and 12 were SFTP-backed —
none of which have, or can ever have, a cloud address, because uploads have no external location by
design and only the S3 and Azure connectors report one at all. Switching that rule on would have
hidden 127,892 of those 127,898 documents, indistinguishably from a denial nobody could fix.

The corrected rule, and the one implemented: a document with no cloud coordinate is not governed by
cloud permissions. It falls back to Connapse's own access control instead — reachable through this
container the same as before cloud filtering existed. Cloud scope filtering only narrows the subset
of documents that actually carry a cloud address; it never removes one that doesn't. The
`DocumentCoordinateReport` re-sync prompt is retained as an optional operator aid for narrowing that
subset further, restricted to sources backed by an S3 or Azure connection — the only ones where a
re-sync could ever produce a coordinate — but it is no longer a precondition for enabling filtering.

## What must happen before filtering can be switched on

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
  null-principal rule, and the corrected no-cloud-coordinate rule (a document with no cloud address
  falls back to Connapse's own access control rather than being denied). No cloud dependency; ships
  alone; is what prevents the corpus-vanishing bug the original design would have shipped.
- **5b — Connecting an AWS identity, and the token store.** Components 2 and 3. Connapse sign-in is
  untouched; this adds an integration a signed-in user connects.
- **5c — Provider setup and detection.** Component 4. Its opening spike is **done** — see "What the
  Cognito spike settled" — so this starts directly on the setup artifacts and the detection page.
  The detection page must check application assignment explicitly, since its absence is the one
  failure in the chain that reports nothing useful.
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

## What the Cognito spike settled

Driven against a live AWS account (organization instance, `us-west-1`) on 2026-08-27. Everything it
created was deleted afterwards and the account verified clean.

**The architecture works.** A Cognito user pool registered as a trusted token issuer for a
**customer-managed** application does yield an identity context, and that context does assume an
identity-enhanced session:

```
Cognito ID token  (aud = the pool's app client id, email = the Identity Center user's)
  → CreateTokenWithIAM, grant urn:ietf:params:oauth:grant-type:jwt-bearer
  → awsAdditionalDetails.identityContext, scopes: sts:identity_context, s3:access_grants:read_write, openid, aws
  → sts:AssumeRole with ProvidedContexts, ProviderArn arn:aws:iam::aws:contextProvider/IdentityCenter
  → an assumed-role session
```

This was the one claim in this design that rested on reasoning rather than evidence, and the reason
was sound: AWS documents Cognito-as-issuer only for Amazon Q Business, an AWS-managed application.
It holds for a custom application too.

**Four things the spike found that the documentation does not lead with:**

**Application assignment is required by default, and its absence is unreadable.** A newly created
customer-managed application has `AssignmentRequired: true` and no assignees, and the exchange then
fails with a bare `AccessDeniedException` / `access_denied` that names nothing — not the missing
assignment, not the application, not the user. Everything else in the chain reports a specific
error, so this one will cost an operator an afternoon. Setup must either create an application
assignment per user or group, or set `AssignmentRequired: false`, and the detection page should
check it explicitly and say so in words.

**No Cognito domain is needed for the exchange.** The pool's OIDC discovery endpoint lives at
`https://cognito-idp.<region>.amazonaws.com/<poolId>/.well-known/openid-configuration` and exists
without one, so the trusted token issuer can be registered against a bare pool. A domain is still
needed for the hosted sign-in UI that the SAML federation flow uses — but the token exchange itself
does not depend on it, which makes the failure surfaces easier to separate during setup.

**The jwt-bearer grant returns no refresh token.** The response carried `refreshToken: None`. This
confirms the decision in component 3 rather than contradicting it: durability has to come from the
identity provider's refresh token, not an AWS one, and the AWS session is rebuilt per resolution.

**`SourceIdentity` on the assumed session was empty.** The user identity travels in the context
assertion rather than in `SourceIdentity`, so anything that expects to read the acting user from
that field — audit tooling, a trust policy condition — will find nothing there.

**Still untested, and deliberately so:** `ListCallerAccessGrants` at the end of the chain, because
this account has no S3 Access Grants instance; and Cognito federating back to Identity Center over
SAML, which needs a browser and is thoroughly documented. Neither was the unknown. The unknown was
whether a custom application could consume a Cognito token at all, and it can.

## Risks

**~~Nobody has driven this exact chain end to end.~~ Closed — the chain was driven end to end on
2026-08-27** against a live AWS account. See "What the Cognito spike settled" below. The remaining
risks are the setup burden and the region constraints that follow.

**The setup burden is substantial** — Identity Center with provisioned users, a Cognito pool, SAML
federation, a trusted token issuer, a customer-managed application, an Access Grants instance and
location, and grants authored per user or group, with Identity Center and Access Grants in the same
region. Most of it is one-time and scriptable, but a customer who abandons setup halfway leaves a
half-provisioned account, so the detection page must be legible enough to resume from.

**Multi-region deployments are constrained.** One Access Grants instance per region per account, and
the Identity Center instance must share its region. Buckets spanning regions need an instance per
region, and only organization instances replicate across regions — so multi-region is effectively
organization-instance-only.
