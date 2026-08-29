# AWS per-user permissions: Identity Center sign-in, no Cognito

Part of epic #436. Supersedes `2026-08-28-per-user-permissions-5b-connect-aws-identity.md`
and the `ListCallerAccessGrants` decision in `search-permission-filtering.md` phase 6.
Retires most of the premise of PR #439.

## Why

The sign-in exists to answer one question: which IAM Identity Center user is this
Connapse account? Everything else was built to obtain a credential Connapse does not need.

`ListAccessGrants` accepts a grantee filter and requires only `s3:ListAccessGrants` on
Connapse's own role, so scopes resolve without acting as the user.
`ListCallerAccessGrants`, which phase 6 specified, is the one variant that requires being
the user — and requiring it pulled in the trusted token issuer, the customer-managed
application, the token exchange, and a stored refresh token that expires after 30 days
and cannot be extended by rotation.

With the credential gone, Cognito has no remaining job. Today the chain is Identity Center
→ (SAML) → Cognito → (OIDC) → Connapse, and Cognito contributes no identity information:
it receives the assertion, maps `${user:subject}` onto `preferred_username`, and re-emits
it as a JWT claim. Pointing the SAML application's ACS URL at Connapse reads the same
value from the same signed assertion, one hop earlier.

Research: `docs/research/aws-scoped-delegated-access-2026-08-29.md`.

## End state

A person clicks Connect, signs in at Identity Center, and Connapse records their directory
`UserId`. Search scopes come from Connapse's own IAM identity. No Cognito, no token
exchange, no stored credential, nothing that expires.

## What we accept

Connapse becomes the SAML service provider, so it owns assertion validation — signature,
audience, destination, lifetime with clock skew, and replay. A forged assertion is full
impersonation of another person's search scope; nothing downstream mitigates it. This is
the whole risk of the change, and it is why the work uses a maintained library rather than
hand-rolled XML handling.

`s3:ListAccessGrants` is instance-wide: it lets Connapse enumerate every user's grants,
not only the signed-in user's. Narrower in blast radius than a per-user credential, wider
in reach. The setup page states it rather than leaving an admin to find it in a policy.

## Phases

### 1 — Connapse as the SAML service provider

Goal: an Identity Center sign-in returns a signed assertion Connapse validates and trusts.

Add `ITfoxtec.Identity.Saml2` (BSD-3-Clause, .NET 10, actively maintained). An ACS endpoint
accepts the HTTP-POST binding; a connect endpoint issues the AuthnRequest. Configuration
holds the Identity Center metadata (signing certificate, entity id, SSO URL), Connapse's
own entity id, and its ACS URL. Validation covers signature, audience, destination,
`NotBefore`/`NotOnOrAfter` with bounded clock skew, and a replay cache keyed on assertion
id. The assertion never appears in a log or an exception message.

`UserAwsIdentityLinkEntity` gains `DirectoryUserId`, which is the operative key;
`DirectoryUserName` and `Email` stay for display. `ProtectedRefreshToken` is dropped by
migration. `identitystore:GetUserId` runs once here, at connect, mapping the asserted
`userName` to the UUID — storing the UUID rather than the name means a directory rename
does not force a reconnect.

Done when: signing in at Identity Center writes a link row with a directory UUID, and a
tampered or expired assertion is refused with nothing sensitive in the message.

### 2 — Retire Cognito

Goal: nothing in Connapse knows what a user pool is.

Delete `CognitoSettings`, `CognitoIdTokenValidator`, the OIDC connect and callback
endpoints, `CognitoSettingsTab` and its guard test, and the Cognito resources in the
CloudFormation template — pool, domain, identity provider, client, managed login branding.
The client secret leaves the product entirely.

Done when: `grep -ri cognito src/` returns nothing outside migration history.

### 3 — Setup, trimmed to what is left

Goal: three short steps, none of which can half-succeed in silence.

Step one stays: discover the Identity Center instance and its region. Step two creates an
S3 Access Grants instance and a location covering `s3://` — an admin who already has one
skips it. Step three is the console-only SAML application, pointing its ACS URL and
audience at Connapse, with the `Subject` row mapped to `${user:subject}`. That step stays
hand-guided because `CreateApplication` refuses SAML customer-managed applications and
`describe-application` will not read back the ACS URL or audience.

Done when: the generated script creates only Access Grants resources, and the page states
the two values an administrator must paste into the console.

### 4 — Resolve scopes from Connapse's own identity

Goal: `ISearchScopeResolver` returns real grants, and revocation is detected rather than
awaited.

`ListAccessGrants` filtered by `granteetype=DIRECTORY_USER` on the stored UUID gives direct
grants; `ListGroupMembershipsForMember` gives the groups, each queried as
`DIRECTORY_GROUP`, because the grantee filter does not expand membership.
`GrantScope.Parse` already normalises the scope strings.

Honour each grant's `ApplicationArn` by reading the field, not by filtering the query —
filtering drops `ALL` and `NA` grants, ignoring it admits grants exercisable only
elsewhere. Connapse presents no application identity, so this is a convention the resolver
respects; say so where it is implemented.

Nothing pushes from AWS, so revocation is checked here: `DescribeUser` returning
`ResourceNotFoundException` means deleted, `UserStatus: DISABLED` means suspended. Either
drops the link and returns `ResolverFailed` — never `Unrestricted`. Cache resolved scopes
briefly so a search is not three or four AWS calls; the cache lifetime is the revocation
delay, so keep it short and say what it is.

Done when: two users with different grants get different results from one corpus, a user
with no grants gets `NoGrants`, and deleting the directory user empties the next search.

### 5 — Connapse's own policy, and saying what it grants

Goal: the permissions are requested, documented, and honest about their reach.

`S3SetupPolicy` gains `s3:ListAccessGrants`, `identitystore:GetUserId`,
`identitystore:DescribeUser`, `identitystore:ListGroupMembershipsForMember`. The providers
page lists them with the instance-wide caveat above. `docs/architecture.md` describes
resolution without a token exchange.

Done when: the four permissions appear in the setup UI with the caveat, and no document
claims filtering happens through an identity-enhanced session.

## Not in scope

Azure. Write permissions. Connapse as an OIDC relying party of a customer's own IdP — the
cleaner answer for customers whose Identity Center federates to Entra or Okta, but it needs
external login Connapse does not have.

## Log

- 2026-08-29 — Plan written. First version targeted keeping Cognito and dropping only the
  token exchange; revised to remove Cognito entirely once it was established that the SAML
  assertion already carries the join key and that Identity Center accepts SP-initiated
  AuthnRequests, which the current Cognito flow proves.
- 2026-08-29 — **Phase 1 part done** (issue #445, branch `feature/445-identity-center-saml-signin`).
  Built: `SamlSignInSettings`, `SamlAssertionValidator` and `MemorySamlReplayGuard` on
  `ITfoxtec.Identity.Saml2` 4.20.1; `IDirectoryUserLookup` with an Identity Store
  implementation; the link entity now carries `DirectoryUserId` and no token, with migration
  `20260829184741_ReplaceAwsLinkTokenWithDirectoryUserId`; `AwsIdentityLinkStore` lost Data
  Protection and its compare-and-delete now keys on `ConnectedAt`; the setup script lost the
  trusted token issuer, the SSO application and all four `sso-admin` calls.
  Verified: solution builds clean, 1,453 unit tests pass, eight obsolete `CognitoSetupTests`
  removed and two adjusted.
  Next: the ACS and connect endpoints, which is the rest of phase 1 — the Cognito callback
  still exists and now resolves the directory id through `IDirectoryUserLookup` so it is
  correct in the meantime.
