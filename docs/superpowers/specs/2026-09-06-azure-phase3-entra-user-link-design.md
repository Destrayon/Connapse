# Azure Phase 3 — Entra user identity link

**Status:** Design — approved in brainstorming, pending spec review
**Date:** 2026-09-06
**Milestone:** v0.4.0 · Issue #478 · Epic #475
**Branch:** `feature/478-entra-user-link` (off `epic/azure-blob-provider`; Phase 2 merged)
**Parent design:** `docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md` §B. This phase spec refines §B with the phase-specific decisions; where they differ, this document governs Phase 3.

## Goal

Let a signed-in Connapse user link their Microsoft Entra identity on the integrations page: a one-time OIDC proof that captures **`oid` + `tid`**, stores only those, and is user-revocable. **Permanent** (two GUIDs, nothing that expires). This mirrors the AWS SAML → `UserAwsIdentityLinkEntity` link. Nothing consumes the link yet — the per-user permission engine (Phase 4, #479) reads it.

## Scope decisions (from brainstorming)

1. **Link only.** Phase 3 establishes/stores/revokes the link. The query-time Graph checks it enables — the `accountEnabled` deprovisioning gate and transitive security-group resolution — are **deferred to Phase 4**, which consumes them. (Mirrors how AWS linking existed before `AwsSearchScopeResolver`.)
2. **New table.** A dedicated `UserAzureIdentityLinkEntity` mirroring `UserAwsIdentityLinkEntity`, with a fresh EF migration. The `user_cloud_identities` table dropped in Phase 1 stays gone.
3. **Explicit endpoints + auth-code + PKCE + certificate.** A secondary account-*link* flow (the user is already authenticated in Connapse), NOT app login — so **no** `Microsoft.Identity.Web`, **no** second ASP.NET auth scheme. Two plain endpoints mirror the AWS SAML `/aws/connect`→`/aws/acs`. Config lives in a dedicated `Identity:AzureAd` settings record, separate from Phase 2's `Providers:Azure` (the app's data-plane credential).
4. **Certificate, not client secret.** The `/azure/callback` code redemption is a confidential-client call, so Entra requires a client credential; PKCE is layered on top (not a replacement — verified against Microsoft docs). Use a **certificate** (Microsoft-recommended over a secret), stored DataProtection-encrypted, mirroring the AWS/Phase-2 stored-private-material pattern.

## Components

### A. Link storage (Connapse.Identity)

- **`UserAzureIdentityLinkEntity`** (`Data/Entities/`): `Id (Guid)`, `UserId (Guid)`, `ObjectId (string, the Entra oid)`, `TenantId (string, tid)`, `DisplayName (string?, from id_token name/preferred_username — display only, mutable)`, `ConnectedAt (DateTime)`. Unique index on `UserId` (one Azure link per user). Mirror `UserAwsIdentityLinkEntity`'s shape/conventions.
- **`AzureIdentityLinkStore`** + **`IAzureIdentityLinkReader`** (persistence; the reader interface is what Phase 4 will consume) and **`AzureIdentityLinkService`** + **`IAzureIdentityLinkService`** (`GetAsync(userId) → AzureIdentityLinkDto?`, `StoreAsync(userId, oid, tid, displayName)`, `DisconnectAsync(userId)`), mirroring `AwsIdentityLinkService`/`AwsIdentityLinkStore`. `AzureIdentityLinkDto` (oid, tid, displayName, ConnectedAt) in Core.
- **Migration:** `dotnet ef migrations add AddUserAzureIdentityLinks --project src/Connapse.Identity` (ConnapseIdentityDbContext). Add the `DbSet` + mapping + `ConnapseUser` navigation.

### B. Sign-in settings (Connapse.Core)

- **`AzureAdSignInSettings`** (`Models/`), `SectionName = "Identity:AzureAd"`: `TenantId` (a specific tenant id, or `organizations`/`common`), `ClientId`, `RedirectUri` (the `/azure/callback` absolute URL Connapse advertises), and the certificate for code redemption — `ClientCertificatePath` (+ optional password) OR a DataProtection-encrypted stored cert (Phase 3 reads from config/path, consistent with Phase 2's `AzureProviderSettings` cert loading; a Providers-page-managed encrypted cert is a later concern). An `IsConfigured` helper. Bound from configuration; `DatabaseSettingsProvider.CategoryPrefixMap` gets `["azuread"] = "Identity:AzureAd"`.
- Reuse the Phase-2 cert-loading helper (PEM/PFX) rather than duplicating it.

### C. OIDC link endpoints (Connapse.Web)

Add to the existing `CloudIdentityEndpoints` group (which already holds the AWS `/aws/*` routes), mirroring their structure — **including the AWS `/acs` → `/confirm` two-step**, not just the outer connect/callback shape. This is a correction to this spec's original draft, which described a single callback that validated the id_token and stored the link directly; a security review during implementation (#478) found that shape lets one Connapse account get linked to a *different* Entra identity than the one it started sign-in with, and the fix is the same confirm hop AWS already uses for the identical reason:

- **`GET /api/v1/auth/cloud/azure/connect`** (authenticated): builds the Entra authorization URL for the configured tenant — `authorize` endpoint, `client_id`, `redirect_uri`, `response_type=code`, `scope=openid profile`, `response_mode=query`, a random `state`, a `nonce`, and a **PKCE** `code_challenge` (S256). Stash `{state → (code_verifier, nonce, connapse userId)}` in a short-lived server-side pending store (mirror `SamlSignInRequests` → `AzureSignInRequests`, in-memory with expiry). Redirect the browser to the authorization URL.
- **`GET /api/v1/auth/cloud/azure/callback?code=&state=`** (anonymous — mirrors why `/aws/acs` is anonymous, see rationale below): look up + remove the pending entry by `state` (reject unknown/expired → error redirect); POST the `code` to Entra's **token** endpoint with `client_id`, `redirect_uri`, `code`, `grant_type=authorization_code`, the **PKCE `code_verifier`**, and the **client certificate assertion** (`client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer` + a signed JWT client assertion from the cert) to obtain the `id_token`; **validate** the id_token (Entra JWKS signature, issuer for the tenant, audience == client_id, `nonce` match, expiry) using `Microsoft.IdentityModel.Tokens` (already present via ITfoxtec — no new package); extract `oid` + `tid` (+ display name). **Does not call `StoreAsync` here.** Instead, park the validated `(startedByUserId, oid, tid, displayName)` under a one-time code (`AzureLinkConfirmations`, an `IMemoryCache`-backed park mirroring `SamlLinkConfirmations`), set that code as an `HttpOnly` cookie (`__connapse_azure_link`, path-scoped to `/api/v1/auth/cloud/azure`, `SameSite=Lax`, lifetime matching the park's 5 minutes), and redirect to `/azure/confirm`. All failures (including the exchange/validation ones above) → integrations page with a generic error message, and **discard the exception detail server-side only** (log it; never put it in the redirect a browser can carry in history). **Fail closed:** any failure — or any unhandled exception anywhere in this handler — stores and parks nothing.
- **`GET /api/v1/auth/cloud/azure/confirm`** (authenticated — this is where `StoreAsync` actually happens): reached by the same-site top-level redirect from `/callback`, so the session cookie is present alongside the confirm cookie. Read + delete the confirm cookie; require a session (401 if none); consume the parked entry by code (single-use; unknown/already-consumed → error redirect); **if the parked entry's `startedByUserId` does not equal the signed-in user's id, refuse and store nothing** (this is the check that closes the CSRF below); otherwise `StoreAsync(userId, oid, tid, displayName)` and redirect to the integrations page (success).
- **Disconnect:** the existing `DELETE /{provider}` route already dispatches by provider — add the `Azure` branch calling `AzureIdentityLinkService.DisconnectAsync` (Phase 1 removed the old Azure arm; re-add it pointing at the new service).

**Why the confirm hop is required even though `/callback` is a same-site redirect (unlike AWS's cross-site POST).** `state` only proves the callback belongs to a sign-in this deployment started — it does not prove the browser completing it is the browser that started it. Anybody with a Connapse account (the attacker) can call `/azure/connect` and capture the resulting Entra authorization URL **without following it**, then send that URL to a colleague (the victim). The colleague authenticates at Entra with their own credentials; PKCE binds the authorization code to the *verifier*, not to a person, so the code redemption at `/callback` succeeds regardless of who completes it; the id_token's `nonce` matches because it genuinely was in the request the colleague completed. Every check on the token passes, because the token is real — the forgery is in the pairing between "who started this sign-in" and "whose Entra identity it resolved to." Storing at `/callback` directly would link the attacker's Connapse account to the colleague's Entra identity, which is a privilege-escalation vector once Phase 4 resolves search scope from these links. Parking the outcome and requiring the *session* at a follow-up same-site GET to match `startedByUserId` closes it: the attacker never receives the `HttpOnly` confirm cookie (it lands in the colleague's browser), and the colleague, who does hold it, is not the user the sign-in was started by — so neither can complete the link.

Use `HttpClient` (a named client) for the token-endpoint call. The client-assertion JWT is signed with the configured certificate (`Microsoft.IdentityModel.Tokens` `SigningCredentials`).

### D. UI (Connapse.Web)

Add an **Azure card** to `ProfileIntegrations.razor` mirroring the AWS card: shows connected state (`oid`/tenant/display name/`ConnectedAt`) via `IAzureIdentityLinkService.GetAsync`, a **Connect** button (navigates to `/azure/connect`), and a **Disconnect** button (with confirm, calling the delete route). Reuse the AWS card's structure/styling.

### E. DI + settings wiring (Connapse.Identity / Connapse.Web)

- Register `IAzureIdentityLinkService`/`AzureIdentityLinkService`, `IAzureIdentityLinkReader`/`AzureIdentityLinkStore`, `AzureSignInRequests` (singleton pending store), `Configure<AzureAdSignInSettings>("Identity:AzureAd")`, and the named `HttpClient` for the token endpoint.
- `DatabaseSettingsProvider.CategoryPrefixMap` += `["azuread"] = "Identity:AzureAd"`.
- Documented empty `Identity:AzureAd` block in `appsettings.json`.

## Testing

- **Unit:** `AzureIdentityLinkService`/store round-trip (store → get → disconnect); `AzureAdSignInSettings.IsConfigured`; the authorization-URL builder (correct params, PKCE S256 challenge derived from verifier, state/nonce present); the id_token validator (a test-signed token with the right issuer/aud/nonce validates and yields oid/tid; wrong nonce/aud/expired → rejected, nothing stored); the PKCE verifier/challenge helper.
- **Integration (`WebApplicationFactory` + shared Postgres fixture):** `GET /azure/connect` (configured) redirects to the tenant authorize endpoint with the expected query params and creates a pending entry; `/azure/callback` with an unknown/expired `state` → error redirect, no row; the happy path (same user starts and confirms) routed through `/callback` → `/confirm` stores the row and redirects to the integrations page; disconnect deletes the row and the migration applies cleanly. The **live token exchange** against Entra can't run in tests — factor the token-endpoint call behind a small `IOidcTokenExchanger` seam so the callback's validate-and-park logic is integration-tested with a faked exchange returning a test-signed id_token (mirrors how the connector's Azurite seam isolates the un-testable dependency).
- **CSRF regression (added post-implementation, #478):** a pending entry started by user A, a valid id_token (via the faked exchanger) for a *different* Entra identity reached over a *different* Connapse user B's session at `/confirm`, must store nothing for either user and must burn the one-time confirm code (a second attempt with the same code, from any session, also fails). This is the test that would have caught the original single-callback design.
- Fail-closed matrix: bad state, bad nonce, bad signature, wrong audience, expired, and now also **`startedByUserId` ≠ confirming session's user id** → each stores nothing and surfaces a generic error.

## Testable seams (design-for-isolation)

- `IOidcTokenExchanger.ExchangeAsync(code, codeVerifier) → rawIdToken` — the confidential-client token-endpoint POST + client-assertion signing; real impl uses `HttpClient` + the cert, tests inject a fake returning a signed test token. Keeps the callback's state/PKCE/validation/park logic unit- and integration-testable without Entra.
- `AzureSignInRequests` (pending PKCE/nonce/state store) — a small, testable in-memory store with expiry (mirror `SamlSignInRequests`).
- `AzureLinkConfirmations` (parked validated-outcome store, keyed by one-time code) — mirrors `SamlLinkConfirmations`; this is the seam that lets the callback's park step and the confirm step's `startedByUserId` check be exercised independently in tests, including the cross-user CSRF case.
- Reuse Phase 2's PEM/PFX cert loader.

## Non-goals (deferred)

- The Graph `accountEnabled` deprovisioning gate and transitive group resolution (Phase 4, #479 — they're query-time enforcement).
- Any per-user search filtering / scope resolution (Phase 4).
- A guided Providers setup page for the Entra app registration + admin-consent generation (a later polish; Phase 3 documents the required registration in the spec/PR, config via settings).
- Multi-tenant guest-user edge handling beyond storing the resource-tenant `oid`/`tid` (the correct key per the Phase-1 research); no cross-tenant correlation.
- `Microsoft.Identity.Web`, a second auth scheme, and the OIDC implicit flow.
