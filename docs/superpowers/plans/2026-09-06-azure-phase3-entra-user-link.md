# Azure Phase 3 — Entra User Identity Link Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a signed-in Connapse user link their Microsoft Entra identity — a one-time OIDC (auth-code + PKCE + certificate) proof that captures `oid` + `tid`, stores only those, and is user-revocable.

**Architecture:** Mirror the AWS SAML link (`UserAwsIdentityLinkEntity` / `AwsIdentityLinkStore` / `AwsIdentityLinkService` / the `CloudIdentityEndpoints` `/aws/connect`+`/aws/acs` flow / `SamlSignInRequests` / the `ProfileIntegrations.razor` AWS card), but with OIDC instead of SAML. It is a **secondary account-link** inside the user's existing Connapse session — NOT app login — so no `Microsoft.Identity.Web`, no second ASP.NET auth scheme. The Entra token-endpoint call is isolated behind `IOidcTokenExchanger` so the callback's state/PKCE/id_token-validation/store logic is testable without live Entra.

**Tech Stack:** .NET 10, EF Core (`ConnapseIdentityDbContext`), `Microsoft.IdentityModel.Tokens`/`.JsonWebTokens` (already present via ITfoxtec — id_token validation + JWKS), `System.Security.Cryptography` (PKCE + client-assertion signing), `HttpClient`, xUnit + FluentAssertions + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-09-06-azure-phase3-entra-user-link-design.md` (parent: `docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md` §B).

## Global Constraints

- **Link only.** Establish/store/revoke the link. Do NOT build the Graph `accountEnabled` deprovisioning check, transitive group resolution, or any search filtering — those are Phase 4 (#479).
- **Permanent key = `oid` + `tid`** (both stored). Display name is display-only (mutable), never a key. Discard all tokens after extracting claims.
- **Explicit endpoints, auth-code + PKCE (S256) + certificate client-assertion.** PKCE does NOT replace the client credential (confidential client) — a certificate signs the client assertion. NO `Microsoft.Identity.Web`, NO second auth scheme, NO OIDC implicit flow.
- **No new NuGet package** for id_token validation/JWKS — use `Microsoft.IdentityModel.Tokens`/`.JsonWebTokens` (present via ITfoxtec). Only add `Microsoft.IdentityModel.Protocols.OpenIdConnect` if it is ALREADY transitively available; otherwise fetch JWKS manually with `HttpClient` + `JsonWebKeySet` (see Task 5).
- **Fail closed:** any state/PKCE/signature/issuer/audience/nonce/expiry failure stores nothing and surfaces an error.
- Config in a dedicated `Identity:AzureAd` settings record — separate from Phase 2's `Providers:Azure`.
- .NET conventions (CLAUDE.md): file-scoped namespaces, records for DTOs/settings, primary constructors for DI, `IDbContextFactory<T>` short-lived contexts, no `var` for primitives, parameterized SQL. Never touch Azure OpenAI/AI Foundry or the AWS path except to mirror it. Tag tests `[Trait("Category","Unit")]` / `[Trait("Category","Integration")]`.
- Build: `dotnet build`. Tests: `dotnet test --filter "Category=Unit"` / `"Category=Integration"` (Docker). Migrations: `dotnet ef migrations add <Name> --project src/Connapse.Identity`.

---

### Task 1: `UserAzureIdentityLinkEntity` + DTOs + migration

**Files:**
- Create: `src/Connapse.Identity/Data/Entities/UserAzureIdentityLinkEntity.cs`
- Modify: `src/Connapse.Core/Models/AuthModels.cs` (add `AzureIdentityLinkDto`, `AzureIdentityRef`)
- Modify: `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs` (DbSet + mapping)
- Modify: `src/Connapse.Identity/Data/Entities/ConnapseUser.cs` (navigation)
- Migration: `AddUserAzureIdentityLinks`

**Interfaces:**
- Produces: `UserAzureIdentityLinkEntity { Guid Id; Guid UserId; string ObjectId; string TenantId; string DisplayName; DateTime ConnectedAt; ConnapseUser User; }`; `record AzureIdentityLinkDto(string ObjectId, string TenantId, string DisplayName, DateTime ConnectedAt)`; `record AzureIdentityRef(string ObjectId, string TenantId)`.

- [ ] **Step 1: Write the entity** (mirror `UserAwsIdentityLinkEntity`'s shape/remarks)

```csharp
namespace Connapse.Identity.Data.Entities;

/// <summary>A Connapse user's linked Microsoft Entra identity (oid + tid). Holds no token —
/// Entra attests the identity once at link time; permissions are later read with Connapse's own
/// identity. One row per user (unique index); connecting again replaces the row.</summary>
public class UserAzureIdentityLinkEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Entra object id (oid) — immutable per-tenant user identifier; the permanent key.</summary>
    public string ObjectId { get; set; } = string.Empty;
    /// <summary>Entra tenant id (tid). Stored with ObjectId as the fully-qualified key.</summary>
    public string TenantId { get; set; } = string.Empty;
    /// <summary>Display name/UPN from the id_token — display only, mutable, never a key.</summary>
    public string DisplayName { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public ConnapseUser User { get; set; } = null!;
}
```

- [ ] **Step 2: Add the DTOs** to `AuthModels.cs` (beside `AwsIdentityLinkDto`):

```csharp
public record AzureIdentityLinkDto(string ObjectId, string TenantId, string DisplayName, DateTime ConnectedAt);
public record AzureIdentityRef(string ObjectId, string TenantId);
```

- [ ] **Step 3: Map it** — in `ConnapseIdentityDbContext`, add `public DbSet<UserAzureIdentityLinkEntity> UserAzureIdentityLinks => Set<...>();`, and in `OnModelCreating` configure the table + a UNIQUE index on `UserId` + the FK to `ConnapseUser` (cascade), mirroring the `UserAwsIdentityLinkEntity` mapping. Add a `ICollection<UserAzureIdentityLinkEntity>` navigation to `ConnapseUser` mirroring the AWS one.

- [ ] **Step 4: Create the migration**

Run: `dotnet ef migrations add AddUserAzureIdentityLinks --project src/Connapse.Identity`
Open it: confirm `Up()` creates `user_azure_identity_links` with the columns + unique `UserId` index + FK, `Down()` drops it, and the snapshot includes the entity. Do not hand-edit beyond what EF generates.

- [ ] **Step 5: Build + commit**

Run: `dotnet build` → 0 errors.
```bash
git add -A
git commit -m "feat(azure): UserAzureIdentityLinkEntity + DTOs + migration (#478)"
```

---

### Task 2: Link store, reader, and service

**Files:**
- Create: `src/Connapse.Identity/Services/AzureIdentityLinkStore.cs`
- Create: `src/Connapse.Core/Interfaces/IAzureIdentityLinkReader.cs`
- Create: `src/Connapse.Identity/Services/IAzureIdentityLinkService.cs` + `AzureIdentityLinkService.cs`
- Test: `tests/Connapse.Identity.Tests/AzureIdentityLinkServiceTests.cs` (or an integration test using the shared Postgres fixture if the store needs a real DbContext)

**Interfaces:**
- Consumes: `UserAzureIdentityLinkEntity`, `AzureIdentityLinkDto`, `AzureIdentityRef`, `IDbContextFactory<ConnapseIdentityDbContext>`.
- Produces:
  - `interface IAzureIdentityLinkReader { Task<AzureIdentityRef?> GetLinkAsync(Guid userId, CancellationToken ct = default); }` (Core — what Phase 4 will consume)
  - `class AzureIdentityLinkStore : IAzureIdentityLinkReader` with `Task SaveAsync(Guid userId, string oid, string tid, string displayName, CancellationToken ct)`, `Task<UserAzureIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct)`, `Task<bool> DeleteAsync(Guid userId, CancellationToken ct)` — mirror `AwsIdentityLinkStore`'s `IDbContextFactory` pattern; `SaveAsync` upserts (replace the existing row for the user).
  - `interface IAzureIdentityLinkService { Task<AzureIdentityLinkDto?> GetAsync(Guid, CancellationToken); Task StoreAsync(Guid userId, string oid, string tid, string displayName, CancellationToken); Task<bool> DisconnectAsync(Guid, CancellationToken); }` + `AzureIdentityLinkService` delegating to the store.

- [ ] **Step 1: Write the failing test** (round-trip; use the pattern the AWS link store tests use — if they're integration tests against the shared Postgres fixture, mirror that)

```csharp
[Trait("Category", "Integration")]
public class AzureIdentityLinkServiceTests(SharedWebAppFixture fx) : IClassFixture<SharedWebAppFixture>
{
    [Fact]
    public async Task Store_Get_Disconnect_RoundTrips()
    {
        using var scope = fx.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IAzureIdentityLinkService>();
        var userId = /* seed or use an existing test user id */ Guid.NewGuid();
        await svc.StoreAsync(userId, "oid-1", "tid-1", "Ada Lovelace", default);

        var dto = await svc.GetAsync(userId, default);
        dto!.ObjectId.Should().Be("oid-1"); dto.TenantId.Should().Be("tid-1"); dto.DisplayName.Should().Be("Ada Lovelace");

        var reader = scope.ServiceProvider.GetRequiredService<IAzureIdentityLinkReader>();
        (await reader.GetLinkAsync(userId, default))!.Should().Be(new AzureIdentityRef("oid-1", "tid-1"));

        (await svc.DisconnectAsync(userId, default)).Should().BeTrue();
        (await svc.GetAsync(userId, default)).Should().BeNull();
    }
}
```
> Match the AWS link tests' fixture/user-seeding approach exactly (read `AwsIdentityLinkStoreTests`/`AwsIdentityLinkServiceTests` first). If those are unit tests with an in-memory/factory double, mirror that instead.

- [ ] **Step 2: Run → FAIL** (`dotnet test --filter "FullyQualifiedName~AzureIdentityLinkServiceTests"`).
- [ ] **Step 3: Implement** the reader, store (mirror `AwsIdentityLinkStore` — `IDbContextFactory`, short-lived contexts, upsert in `SaveAsync`, `DeleteAsync` returns whether a row was removed), and service.
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** `feat(azure): Entra identity link store + reader + service (#478)`

---

### Task 3: `AzureAdSignInSettings`

**Files:**
- Create: `src/Connapse.Core/Models/AzureAdSignInSettings.cs`
- Test: `tests/Connapse.Core.Tests/Settings/AzureAdSignInSettingsTests.cs`

**Interfaces:**
- Produces: `record AzureAdSignInSettings { const string SectionName = "Identity:AzureAd"; string? TenantId; string? ClientId; string? RedirectUri; string? ClientCertificatePath; string? ClientCertificatePassword; bool IsConfigured; }` where `IsConfigured` is true iff `TenantId`, `ClientId`, `RedirectUri`, and `ClientCertificatePath` are all non-blank.

- [ ] **Step 1: Failing test** — `IsConfigured` true only when all four required fields are set; false when any is blank. (4-5 cases.)
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the record + `IsConfigured` computed property.
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** `feat(azure): AzureAdSignInSettings (#478)`

---

### Task 4: PKCE + pending-request store + authorization-URL builder

**Files:**
- Create: `src/Connapse.Identity/Services/OidcPkce.cs` (pure helpers), `src/Connapse.Identity/Services/AzureSignInRequests.cs` (in-memory pending store), `src/Connapse.Identity/Services/AzureAuthorizationUrl.cs` (URL builder)
- Test: `tests/Connapse.Identity.Tests/OidcPkceTests.cs`, `AzureAuthorizationUrlTests.cs`, `AzureSignInRequestsTests.cs`

**Interfaces:**
- Produces:
  - `static class OidcPkce { static (string verifier, string challenge) Create(); }` — verifier = base64url(32 random bytes); challenge = base64url(SHA256(ASCII(verifier))).
  - `record AzurePendingSignIn(string State, string CodeVerifier, string Nonce, Guid UserId, DateTime ExpiresAtUtc);` and `class AzureSignInRequests { void Add(AzurePendingSignIn p); AzurePendingSignIn? TakeByState(string state); }` (thread-safe dictionary; `TakeByState` removes-and-returns, dropping expired) — mirror `SamlSignInRequests`.
  - `static class AzureAuthorizationUrl { static string Build(AzureAdSignInSettings s, string state, string nonce, string codeChallenge); }` → `https://login.microsoftonline.com/{tid}/oauth2/v2.0/authorize?client_id=..&response_type=code&redirect_uri=..&response_mode=query&scope=openid%20profile&state=..&nonce=..&code_challenge=..&code_challenge_method=S256` (all values URL-encoded).

- [ ] **Step 1: Failing tests**

```csharp
[Fact][Trait("Category","Unit")]
public void Pkce_ChallengeIsBase64UrlSha256OfVerifier()
{
    var (verifier, challenge) = OidcPkce.Create();
    using var sha = System.Security.Cryptography.SHA256.Create();
    var expected = Base64Url(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(verifier)));
    challenge.Should().Be(expected);
    challenge.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
}

[Fact][Trait("Category","Unit")]
public void AuthUrl_ContainsRequiredParams()
{
    var s = new AzureAdSignInSettings { TenantId = "t", ClientId = "c", RedirectUri = "https://app/azure/callback" };
    var url = AzureAuthorizationUrl.Build(s, "st", "no", "ch");
    url.Should().StartWith("https://login.microsoftonline.com/t/oauth2/v2.0/authorize?");
    url.Should().Contain("client_id=c").And.Contain("response_type=code")
       .And.Contain("code_challenge=ch").And.Contain("code_challenge_method=S256")
       .And.Contain("state=st").And.Contain("nonce=no")
       .And.Contain("redirect_uri=https%3A%2F%2Fapp%2Fazure%2Fcallback")
       .And.Contain("scope=openid%20profile");
}

[Fact][Trait("Category","Unit")]
public void Pending_TakeByState_RemovesAndDropsExpired()
{
    var store = new AzureSignInRequests();
    store.Add(new AzurePendingSignIn("s1","v","n",Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5)));
    store.TakeByState("s1").Should().NotBeNull();
    store.TakeByState("s1").Should().BeNull(); // removed
    store.Add(new AzurePendingSignIn("s2","v","n",Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-1)));
    store.TakeByState("s2").Should().BeNull(); // expired
}
```
(`Base64Url` helper in the test: `Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_')`.)

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the three helpers. `OidcPkce.Create`: `RandomNumberGenerator.GetBytes(32)` → base64url = verifier; challenge = base64url(SHA256). `AzureSignInRequests`: `ConcurrentDictionary<string,AzurePendingSignIn>`; `TakeByState` `TryRemove` then null-if-expired.
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** `feat(azure): OIDC PKCE + pending-request store + auth-URL builder (#478)`

---

### Task 5: id_token validation + `IOidcTokenExchanger`

**Files:**
- Create: `src/Connapse.Identity/Services/IOidcTokenExchanger.cs` + `AzureOidcTokenExchanger.cs` (real impl), `src/Connapse.Identity/Services/AzureIdTokenValidator.cs`
- Modify: `src/Connapse.Identity/Connapse.Identity.csproj` ONLY if `Microsoft.IdentityModel.Protocols.OpenIdConnect` is not already available (see Step 3)
- Test: `tests/Connapse.Identity.Tests/AzureIdTokenValidatorTests.cs`

**Interfaces:**
- Produces:
  - `interface IOidcTokenExchanger { Task<string> ExchangeAsync(string code, string codeVerifier, CancellationToken ct); }` — returns the raw id_token JWT. Real impl POSTs to `https://login.microsoftonline.com/{tid}/oauth2/v2.0/token` with form: `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `code_verifier`, `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`, `client_assertion=<signed JWT>`; reads `id_token` from the JSON response. The client-assertion JWT is signed with the cert (RS256): claims `aud`=token endpoint, `iss`=`sub`=client_id, `jti`=guid, `nbf`/`exp` (±minutes), header `x5t`/`kid` from the cert.
  - `class AzureIdTokenValidator { Task<AzureIdTokenResult> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct); }` returning `record AzureIdTokenResult(bool Ok, string? ObjectId, string? TenantId, string? DisplayName, string? Error)`.

- [ ] **Step 1: Failing tests** — sign a test id_token with an in-test RSA key, validate against a `TokenValidationParameters` whose `IssuerSigningKeys` is that key, asserting: valid token with matching `aud`/`iss`/`nonce` → `Ok`, oid/tid extracted; wrong `nonce` → not Ok; wrong `aud` → not Ok; expired → not Ok. (Inject the signing key / a metadata stub so the validator doesn't hit the network in tests — see Step 3's seam.)

```csharp
[Trait("Category","Unit")]
public class AzureIdTokenValidatorTests
{
    [Fact] public async Task ValidToken_ExtractsOidTid() { /* build signed JWT w/ oid,tid,nonce,aud,iss; assert Ok + values */ }
    [Fact] public async Task WrongNonce_Fails() { /* ... */ }
    [Fact] public async Task WrongAudience_Fails() { /* ... */ }
    [Fact] public async Task Expired_Fails() { /* ... */ }
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement.** For validation use `Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.ValidateTokenAsync` with `TokenValidationParameters { ValidIssuer = $"https://login.microsoftonline.com/{tid}/v2.0", ValidAudience = clientId, IssuerSigningKeys = <Entra JWKS keys>, ValidateLifetime = true }`, then read the `nonce` claim and compare; extract `oid`, `tid`, and a display name (`name` ?? `preferred_username`).
  - **JWKS without a new package:** make the signing-key source injectable so tests pass keys directly. For production, fetch keys manually: GET `https://login.microsoftonline.com/{tid}/v2.0/.well-known/openid-configuration` → `jwks_uri` → GET it → `new Microsoft.IdentityModel.Tokens.JsonWebKeySet(json).GetSigningKeys()`. Cache per tenant with a short TTL. (`JsonWebKeySet` is in `Microsoft.IdentityModel.Tokens`, already present — no new package.) ONLY if `Microsoft.IdentityModel.Protocols.OpenIdConnect` is already transitively present (check `dotnet list src/Connapse.Identity package --include-transitive`) may you use `ConfigurationManager<OpenIdConnectConfiguration>` instead; do not add it as a new dependency.
  - The client-assertion signer: `RsaSecurityKey`/`X509SigningCredentials` from the cert (reuse Phase 2's cert loader for loading the `X509Certificate2`); build the JWT with `JsonWebTokenHandler.CreateToken`.
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** `feat(azure): id_token validator + OIDC token exchanger seam (#478)`

---

### Task 6: `/azure/connect` + `/azure/callback` endpoints + disconnect branch

**Files:**
- Modify: `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs` (add the two Azure routes to the existing group; add the `Azure` branch to `MapDelete("/{provider}")`)
- Test: `tests/Connapse.Integration.Tests/CloudIdentityEndpointTests.cs` (add Azure cases)

**Interfaces:**
- Consumes: `AzureAdSignInSettings`, `AzureSignInRequests`, `AzureAuthorizationUrl`, `OidcPkce`, `IOidcTokenExchanger`, `AzureIdTokenValidator`, `IAzureIdentityLinkService`, `GetUserId(httpContext)`.
- Produces: `GET /api/v1/auth/cloud/azure/connect`, `GET /api/v1/auth/cloud/azure/callback`, and `DELETE /{provider}` accepting `Azure`.

- [ ] **Step 1: Failing integration tests**

```csharp
[Trait("Category","Integration")]
// /azure/connect (configured) → 302 to login.microsoftonline.com with code_challenge + state; a pending entry exists.
// /azure/callback?state=unknown → redirect to integrations page with an error; NO link row.
// /azure/callback happy path with a FAKE IOidcTokenExchanger (registered in the test host) returning a
//   test-signed id_token whose nonce matches the pending entry → link row stored (oid/tid), redirect success.
// DELETE /Azure → deletes the row, 200.
```
> Register a fake `IOidcTokenExchanger` + an injectable signing key in the test `WebApplicationFactory` so the callback runs end-to-end without Entra. Seed a pending entry by first calling `/azure/connect` (capture `state` from the redirect) or by injecting `AzureSignInRequests` directly.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the endpoints, mirroring the AWS `/aws/connect` + `/aws/acs` structure:
  - `connect`: require `GetUserId`; if `!settings.IsConfigured` → 400/redirect error; `var (v,ch)=OidcPkce.Create(); var state=rand; var nonce=rand;` `pending.Add(new(state,v,nonce,userId,ExpiresAtUtc));` redirect to `AzureAuthorizationUrl.Build(settings,state,nonce,ch)`.
  - `callback`: read `code`,`state`; `var p = pending.TakeByState(state)` → null → error redirect; `var raw = await exchanger.ExchangeAsync(code, p.CodeVerifier, ct);` `var r = await validator.ValidateAsync(raw, p.Nonce, ct);` `!r.Ok` → error redirect (store nothing); `await linkService.StoreAsync(p.UserId, r.ObjectId!, r.TenantId!, r.DisplayName ?? "", ct);` redirect to the integrations page success. Wrap in try/catch → error redirect (fail closed).
  - `MapDelete("/{provider}")`: add `Azure` to the accepted providers; for `Azure`, `await azureLinks.DisconnectAsync(userId.Value, ct)` and return the same success shape as AWS. Update the "Valid values: AWS." message to "AWS, Azure." (do NOT add scope-cache invalidation — that's Phase 4).
- [ ] **Step 4: Run → PASS** (needs Docker for the integration host).
- [ ] **Step 5: Commit** `feat(azure): Entra link connect/callback endpoints + disconnect (#478)`

---

### Task 7: DI + settings wiring

**Files:**
- Modify: `src/Connapse.Identity/IdentityServiceExtensions.cs` (registrations), `src/Connapse.Storage/Settings/DatabaseSettingsProvider.cs` (category map), `src/Connapse.Web/appsettings.json` (doc section)
- Test: add a DI-resolution assertion to the integration host test.

**Interfaces:** Produces the resolvable graph: `AzureIdentityLinkStore`, `IAzureIdentityLinkReader`, `IAzureIdentityLinkService`, `AzureSignInRequests` (singleton), `IOidcTokenExchanger`→`AzureOidcTokenExchanger`, `AzureIdTokenValidator`, `Configure<AzureAdSignInSettings>`, a named `HttpClient` for the exchanger.

- [ ] **Step 1: Failing test** — `GetRequiredService<IAzureIdentityLinkService>()`, `GetRequiredService<IAzureIdentityLinkReader>()`, and `GetRequiredService<IOidcTokenExchanger>()` all resolve from a scope of the shared host.
- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** — mirror the AWS link registrations (`AwsIdentityLinkStore` scoped; reader forwards to it; service scoped) at `IdentityServiceExtensions.cs:72-74`; add `services.AddSingleton<AzureSignInRequests>()` (beside `SamlSignInRequests`), `services.Configure<AzureAdSignInSettings>(configuration.GetSection(AzureAdSignInSettings.SectionName))` (beside the `SamlSignInSettings` Configure), `services.AddScoped<AzureIdTokenValidator>()`, and `services.AddHttpClient<IOidcTokenExchanger, AzureOidcTokenExchanger>()`. Add `["azuread"] = "Identity:AzureAd"` to `DatabaseSettingsProvider.CategoryPrefixMap`. Add a documented empty `Identity:AzureAd` block to `appsettings.json`.
- [ ] **Step 4: Run → PASS** (Docker).
- [ ] **Step 5: Commit** `feat(azure): DI + settings wiring for Entra user link (#478)`

---

### Task 8: Integrations-page Azure card

**Files:**
- Modify: `src/Connapse.Web/Components/Pages/ProfileIntegrations.razor`
- Test: none new required (Blazor handler; the AWS card isn't component-tested either) — build is the gate.

**Interfaces:** Consumes `IAzureIdentityLinkService`.

- [ ] **Step 1: Implement** an Azure card mirroring the AWS card: `@inject IAzureIdentityLinkService AzureIdentityLinkService`; in the load path call `GetAsync(currentUserId)` to populate an `azureIdentity` field; show connected state (display name, tenant, `ConnectedAt`) or a **Connect** button that navigates to `/api/v1/auth/cloud/azure/connect`; a **Disconnect** button (with a `confirmDisconnectAzure` toggle) that calls `DELETE /api/v1/auth/cloud/Azure`; surface `?error=` from the callback like `ApplyAwsErrorFromQuery` does. Reuse the AWS card's markup/styling.
- [ ] **Step 2: Build** — `dotnet build` → 0 errors.
- [ ] **Step 3: Unit tests still green** — `dotnet test --filter "Category=Unit"`.
- [ ] **Step 4: Commit** `feat(azure): integrations-page Entra link card (#478)`

---

### Task 9: Full verification

**Files:** none (verification only).

- [ ] **Step 1:** `dotnet build` → 0 errors, 0 warnings.
- [ ] **Step 2:** `dotnet test --filter "Category=Unit"` → all pass.
- [ ] **Step 3:** `dotnet test --filter "Category=Integration"` → the Azure link + DI-resolution + migration tests pass (Docker). Pre-existing Ollama-dependent failures (no local Ollama) are the known environmental gap — confirm any failures are only those.
- [ ] **Step 4:** Confirm scope discipline: `rg -n "getMemberGroups|transitiveMemberOf|accountEnabled|ISearchScopeResolver" src/` shows NO Phase-4 code was added; Azure OpenAI/AI Foundry + AWS path untouched (`git diff --stat epic/azure-blob-provider..HEAD` touches only Identity/Web/Core Azure-link files + the migration).
- [ ] **Step 5: Push + open PR to the epic branch** (outward action — perform in the finishing step after the whole-branch review, with the user's go-ahead):
```bash
git push -u origin feature/478-entra-user-link
gh pr create --repo Destrayon/Connapse --base epic/azure-blob-provider \
  --title "feat: Entra user identity link (#478)" \
  --body "Closes #478. Part of epic #475. Link-only: OIDC auth-code+PKCE+certificate captures oid/tid, stored + user-revocable; Graph checks deferred to Phase 4. Targets the epic branch. See docs/superpowers/specs/2026-09-06-azure-phase3-entra-user-link-design.md."
```

---

## Self-Review

**Spec coverage:** §A link storage → Tasks 1–2 (entity/DTOs/migration, store/reader/service). §B settings → Task 3. §C endpoints → Tasks 4–6 (PKCE/pending/URL, id_token validation + exchanger seam, the two endpoints + disconnect). §D UI → Task 8. §E DI/settings → Task 7. Testing (incl. the `IOidcTokenExchanger` seam and fail-closed matrix) → Tasks 5–6. Non-goals (Graph deprovisioning/groups, filtering, Microsoft.Identity.Web, second scheme, implicit flow, guided Providers page) are respected — no task builds them (Task 9 Step 4 asserts it).

**Placeholder scan:** the endpoint markup (Task 6) and razor card (Task 8) are described as exact params/flow + mirror-the-AWS-structure rather than pasted verbatim, because they clone existing files in-repo; every data shape (oid/tid/display name, the auth-URL params, the token-endpoint form fields) and the fail-closed branches are stated explicitly. The JWKS-package decision (Task 5) is a concrete conditional with a no-new-package default, not a "figure it out."

**Type consistency:** `AzureIdentityRef(ObjectId, TenantId)`, `AzureIdentityLinkDto(ObjectId, TenantId, DisplayName, ConnectedAt)`, `IAzureIdentityLinkReader.GetLinkAsync`, `IAzureIdentityLinkService.{GetAsync,StoreAsync,DisconnectAsync}`, `AzureAdSignInSettings` (SectionName `Identity:AzureAd`), `IOidcTokenExchanger.ExchangeAsync(code, codeVerifier, ct)`, `AzureIdTokenValidator.ValidateAsync(idToken, expectedNonce, ct) → AzureIdTokenResult`, `AzurePendingSignIn(State, CodeVerifier, Nonce, UserId, ExpiresAtUtc)`, and `AzureSignInRequests.{Add,TakeByState}` are used identically across tasks.
