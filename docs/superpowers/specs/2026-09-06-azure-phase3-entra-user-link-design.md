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

Add to the existing `CloudIdentityEndpoints` group (which already holds the AWS `/aws/*` routes), mirroring their structure:

- **`GET /api/v1/auth/cloud/azure/connect`** (authenticated): builds the Entra authorization URL for the configured tenant — `authorize` endpoint, `client_id`, `redirect_uri`, `response_type=code`, `scope=openid profile`, `response_mode=query`, a random `state`, a `nonce`, and a **PKCE** `code_challenge` (S256). Stash `{state → (code_verifier, nonce, connapse userId)}` in a short-lived server-side pending store (mirror `SamlSignInRequests` → `AzureSignInRequests`, in-memory with expiry). Redirect the browser to the authorization URL.
- **`GET /api/v1/auth/cloud/azure/callback?code=&state=`**: look up + remove the pending entry by `state` (reject unknown/expired → error redirect); POST the `code` to Entra's **token** endpoint with `client_id`, `redirect_uri`, `code`, `grant_type=authorization_code`, the **PKCE `code_verifier`**, and the **client certificate assertion** (`client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer` + a signed JWT client assertion from the cert) to obtain the `id_token`; **validate** the id_token (Entra JWKS signature, issuer for the tenant, audience == client_id, `nonce` match, expiry) using `Microsoft.IdentityModel.Tokens` (already present via ITfoxtec — no new package); extract `oid` + `tid` (+ display name); `StoreAsync(userId, oid, tid, displayName)`; discard all tokens; redirect back to the integrations page (success). All failures → integrations page with an error message (mirror the AWS `ApplyAwsErrorFromQuery` pattern). **Fail closed:** any validation failure stores nothing.
- **Disconnect:** the existing `DELETE /{provider}` route already dispatches by provider — add the `Azure` branch calling `AzureIdentityLinkService.DisconnectAsync` (Phase 1 removed the old Azure arm; re-add it pointing at the new service).

Use `HttpClient` (a named client) for the token-endpoint call. The client-assertion JWT is signed with the configured certificate (`Microsoft.IdentityModel.Tokens` `SigningCredentials`).

### D. UI (Connapse.Web)

Add an **Azure card** to `ProfileIntegrations.razor` mirroring the AWS card: shows connected state (`oid`/tenant/display name/`ConnectedAt`) via `IAzureIdentityLinkService.GetAsync`, a **Connect** button (navigates to `/azure/connect`), and a **Disconnect** button (with confirm, calling the delete route). Reuse the AWS card's structure/styling.

### E. DI + settings wiring (Connapse.Identity / Connapse.Web)

- Register `IAzureIdentityLinkService`/`AzureIdentityLinkService`, `IAzureIdentityLinkReader`/`AzureIdentityLinkStore`, `AzureSignInRequests` (singleton pending store), `Configure<AzureAdSignInSettings>("Identity:AzureAd")`, and the named `HttpClient` for the token endpoint.
- `DatabaseSettingsProvider.CategoryPrefixMap` += `["azuread"] = "Identity:AzureAd"`.
- Documented empty `Identity:AzureAd` block in `appsettings.json`.

## Testing

- **Unit:** `AzureIdentityLinkService`/store round-trip (store → get → disconnect); `AzureAdSignInSettings.IsConfigured`; the authorization-URL builder (correct params, PKCE S256 challenge derived from verifier, state/nonce present); the id_token validator (a test-signed token with the right issuer/aud/nonce validates and yields oid/tid; wrong nonce/aud/expired → rejected, nothing stored); the PKCE verifier/challenge helper.
- **Integration (`WebApplicationFactory` + shared Postgres fixture):** `GET /azure/connect` (configured) redirects to the tenant authorize endpoint with the expected query params and creates a pending entry; `/azure/callback` with an unknown/expired `state` → error redirect, no row; disconnect deletes the row and the migration applies cleanly. The **live token exchange** against Entra can't run in tests — factor the token-endpoint call behind a small `IOidcTokenExchanger` seam so the callback's validate-and-store logic is integration-tested with a faked exchange returning a test-signed id_token (mirrors how the connector's Azurite seam isolates the un-testable dependency).
- Fail-closed matrix: bad state, bad nonce, bad signature, wrong audience, expired → each stores nothing and surfaces an error.

## Testable seams (design-for-isolation)

- `IOidcTokenExchanger.ExchangeAsync(code, codeVerifier) → rawIdToken` — the confidential-client token-endpoint POST + client-assertion signing; real impl uses `HttpClient` + the cert, tests inject a fake returning a signed test token. Keeps the callback's state/PKCE/validation/store logic unit- and integration-testable without Entra.
- `AzureSignInRequests` (pending PKCE/nonce/state store) — a small, testable in-memory store with expiry (mirror `SamlSignInRequests`).
- Reuse Phase 2's PEM/PFX cert loader.

## Non-goals (deferred)

- The Graph `accountEnabled` deprovisioning gate and transitive group resolution (Phase 4, #479 — they're query-time enforcement).
- Any per-user search filtering / scope resolution (Phase 4).
- A guided Providers setup page for the Entra app registration + admin-consent generation (a later polish; Phase 3 documents the required registration in the spec/PR, config via settings).
- Multi-tenant guest-user edge handling beyond storing the resource-tenant `oid`/`tid` (the correct key per the Phase-1 research); no cross-tenant correlation.
- `Microsoft.Identity.Web`, a second auth scheme, and the OIDC implicit flow.
