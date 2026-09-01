# AWS Roles Anywhere Storage & Wiring (PR 2a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist a Roles Anywhere credential configuration and make `ConnapseAwsCredentials` use the PR 1 signing engine to vend temporary AWS credentials from it — additively, without touching the existing IAM-user path or any UI.

**Architecture:** Purely additive. `ProviderCredentialEntity` gains nullable Roles Anywhere columns (cert PEM, protected private key, three ARNs, region) beside the existing access-key columns; a stored row is a Roles Anywhere config when its `TrustAnchorArn` is set. `ConnapseAwsCredentials` checks for a Roles Anywhere config first, and when present loads the cert and calls the PR 1 `RolesAnywhereClient` (over a named `HttpClient`) to get temporary credentials; otherwise it falls back to the existing access-key path, then the ambient chain. Nothing is removed — the IAM-user path keeps working and the removals + UI land in PR 3.

**Tech Stack:** .NET 10, EF Core (Npgsql) with `IDbContextFactory<KnowledgeDbContext>`, ASP.NET DataProtection (`ProviderCredential.v1` protector), `IHttpClientFactory`. Tests: xUnit, FluentAssertions, NSubstitute; storage round-trip is `Category=Integration` (Testcontainers Postgres via `SharedWebAppFixture`), the rest `Category=Unit`.

**Spec:** `docs/superpowers/specs/2026-09-01-aws-role-based-credentials-design.md` (delivery step 2, storage + wiring half; deletions and UI are PR 3).

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, records for DTOs, primary constructors, async all the way (never `.Result`/`.Wait()`), no `var` for primitive types, parameterized SQL only.
- **Additive only.** Do NOT remove or reshape the existing access-key members (`PublicId`, `SecretProtected`, `GetAsync`, `GetSecretAsync`, `SaveAsync`), do NOT delete `AwsIamUserSetup`, do NOT touch `Providers.razor` or `ProviderSetupReader`. Those are PR 3.
- **Mode signal:** a row is a Roles Anywhere config iff `TrustAnchorArn` is non-empty. Saving a Roles Anywhere config clears the access-key columns (to `""`); saving an access key clears the Roles Anywhere columns (to `null`). This keeps the runtime's mode choice unambiguous.
- DataProtection: private key stored via the existing `IDataProtectionProvider.CreateProtector("ProviderCredential.v1")` — reuse `PostgresProviderCredentialStore.Protector`.
- DbContext: always `await using var db = await factory.CreateDbContextAsync(ct)` (Blazor Server requirement).
- Migrations: `KnowledgeDbContext` history under `src/Connapse.Storage/Migrations/`; the newest existing migration is `20260827062208_AddDocumentResourceUri` — the new one chains after it. Create with `dotnet ef migrations add <Name> --project src/Connapse.Storage`.
- Tests: `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`; naming `MethodName_Scenario_ExpectedResult`. Integration tests use `[Collection("Integration Tests")]` and a `SharedWebAppFixture fixture` ctor param, resolving services via `fixture.Factory.Services.CreateAsyncScope()`.
- The PR 1 engine is available in `Connapse.Storage.CloudScope.RolesAnywhere`: `RolesAnywhereClient(HttpClient).CreateSessionAsync(X509Certificate2, RolesAnywhereParameters, DateTimeOffset, CancellationToken) → RolesAnywhereSession(ImmutableCredentials Credentials, DateTimeOffset Expiration)`; `RolesAnywhereParameters(TrustAnchorArn, ProfileArn, RoleArn, Region, DurationSeconds?, RoleSessionName?)`; `RolesAnywhereSigner.SignBytes`, `.RsaAlgorithm`.

---

## File Structure

- `src/Connapse.Core/Interfaces/IProviderCredentialStore.cs` — add `RolesAnywhereConfig` record + 3 interface methods.
- `src/Connapse.Storage/Data/Entities/ProviderCredentialEntity.cs` — add 6 nullable Roles Anywhere properties.
- `src/Connapse.Storage/Data/KnowledgeDbContext.cs` — map the 6 new columns (in `ConfigureConnections`).
- `src/Connapse.Storage/Migrations/*` — one new migration (generated).
- `src/Connapse.Storage/Connections/PostgresProviderCredentialStore.cs` — implement the 3 methods; clear Roles Anywhere columns in the existing `SaveAsync`.
- `src/Connapse.Storage/CloudScope/ConnapseAwsCredentials.cs` — Roles Anywhere branch + `ResolvedCredentials` shape + `RolesAnywhereHttpClientName` const.
- `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register the named `HttpClient`.
- `tests/Connapse.Integration.Tests/RolesAnywhereCredentialStoreIntegrationTests.cs` — storage round-trip (Integration).
- `tests/Connapse.Storage.Tests/CloudScope/ConnapseAwsCredentialsTests.cs` — wiring (Unit).
- `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignHelperVectorTests.cs` — signing-primitive golden vector (Unit).

---

## Task 1: Additive Roles Anywhere storage

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IProviderCredentialStore.cs`
- Modify: `src/Connapse.Storage/Data/Entities/ProviderCredentialEntity.cs`
- Modify: `src/Connapse.Storage/Data/KnowledgeDbContext.cs:465-498`
- Modify: `src/Connapse.Storage/Connections/PostgresProviderCredentialStore.cs`
- Generate: a migration under `src/Connapse.Storage/Migrations/`
- Test: `tests/Connapse.Integration.Tests/RolesAnywhereCredentialStoreIntegrationTests.cs`

**Interfaces:**
- Produces:
  - `record RolesAnywhereConfig(string CertificatePem, string TrustAnchorArn, string ProfileArn, string RoleArn, string Region)`
  - `Task<RolesAnywhereConfig?> IProviderCredentialStore.GetRolesAnywhereAsync(string provider, CancellationToken ct = default)`
  - `Task<string?> IProviderCredentialStore.GetRolesAnywherePrivateKeyAsync(string provider, CancellationToken ct = default)`
  - `Task<ProviderCredentialInfo> IProviderCredentialStore.SaveRolesAnywhereAsync(string provider, RolesAnywhereConfig config, string privateKeyPem, string? principalName, Guid? createdByUserId, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Integration.Tests/RolesAnywhereCredentialStoreIntegrationTests.cs
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class RolesAnywhereCredentialStoreIntegrationTests(SharedWebAppFixture fixture)
{
    private static readonly RolesAnywhereConfig Config = new(
        CertificatePem: "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----",
        TrustAnchorArn: "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
        ProfileArn: "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
        RoleArn: "arn:aws:iam::111:role/connapse",
        Region: "us-east-1");

    [Fact]
    public async Task SaveRolesAnywhere_ThenGet_RoundTripsConfigAndDecryptsPrivateKey()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-ra-{Guid.NewGuid():N}"[..16];

        await store.SaveRolesAnywhereAsync(provider, Config, "PRIVATE-KEY-PEM", "connapse-role", null);

        RolesAnywhereConfig? read = await store.GetRolesAnywhereAsync(provider);
        read.Should().Be(Config);
        (await store.GetRolesAnywherePrivateKeyAsync(provider)).Should().Be("PRIVATE-KEY-PEM");
    }

    [Fact]
    public async Task GetRolesAnywhere_WhenOnlyAccessKeyStored_ReturnsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-ak-{Guid.NewGuid():N}"[..16];

        await store.SaveAsync(provider, "AKIAEXAMPLE", "sekret", "connapse-reader", null);

        (await store.GetRolesAnywhereAsync(provider)).Should().BeNull();
        (await store.GetRolesAnywherePrivateKeyAsync(provider)).Should().BeNull();
    }

    [Fact]
    public async Task SaveRolesAnywhere_ClearsAnyPriorAccessKey()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
        string provider = $"aws-sw-{Guid.NewGuid():N}"[..16];

        await store.SaveAsync(provider, "AKIAOLD", "old-secret", "connapse-reader", null);
        await store.SaveRolesAnywhereAsync(provider, Config, "PRIVATE-KEY-PEM", "connapse-role", null);

        (await store.GetSecretAsync(provider)).Should().BeNullOrEmpty(); // access-key secret cleared
        (await store.GetRolesAnywhereAsync(provider)).Should().Be(Config);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj --filter "FullyQualifiedName~RolesAnywhereCredentialStoreIntegrationTests"`
Expected: FAIL to compile — `RolesAnywhereConfig`, `GetRolesAnywhereAsync`, `GetRolesAnywherePrivateKeyAsync`, `SaveRolesAnywhereAsync` do not exist. (Docker must be running for Testcontainers.)

- [ ] **Step 3a: Add the record and interface methods**

```csharp
// add to src/Connapse.Core/Interfaces/IProviderCredentialStore.cs (alongside ProviderCredentialInfo)

/// <summary>
/// A stored IAM Roles Anywhere configuration (non-secret). The private key is fetched separately via
/// <see cref="IProviderCredentialStore.GetRolesAnywherePrivateKeyAsync"/>, so a listing can never
/// render it.
/// </summary>
public record RolesAnywhereConfig(
    string CertificatePem,
    string TrustAnchorArn,
    string ProfileArn,
    string RoleArn,
    string Region);
```

```csharp
// add these three members inside the IProviderCredentialStore interface

/// <summary>The stored Roles Anywhere configuration, or null when the provider is not using one.</summary>
Task<RolesAnywhereConfig?> GetRolesAnywhereAsync(string provider, CancellationToken ct = default);

/// <summary>The Roles Anywhere private key, decrypted. Null when none is stored.</summary>
/// <exception cref="ProviderCredentialUnavailableException">Stored but undecryptable (lost key ring).</exception>
Task<string?> GetRolesAnywherePrivateKeyAsync(string provider, CancellationToken ct = default);

/// <summary>
/// Stores or replaces the provider's credential with a Roles Anywhere configuration, clearing any
/// access-key fields so the runtime's mode choice stays unambiguous.
/// </summary>
Task<ProviderCredentialInfo> SaveRolesAnywhereAsync(
    string provider, RolesAnywhereConfig config, string privateKeyPem, string? principalName,
    Guid? createdByUserId, CancellationToken ct = default);
```

- [ ] **Step 3b: Add the entity columns**

```csharp
// add to src/Connapse.Storage/Data/Entities/ProviderCredentialEntity.cs (after CreatedByUserId)

/// <summary>PEM of the Roles Anywhere end-entity certificate (public; stored in the clear). Null for the access-key shape.</summary>
public string? CertificatePem { get; set; }

/// <summary>DataProtection ciphertext of the Roles Anywhere private key, purpose "ProviderCredential.v1". Null for the access-key shape.</summary>
public string? PrivateKeyProtected { get; set; }

/// <summary>Roles Anywhere trust-anchor ARN. Its presence is the signal that this row is a Roles Anywhere config.</summary>
public string? TrustAnchorArn { get; set; }

/// <summary>Roles Anywhere profile ARN.</summary>
public string? ProfileArn { get; set; }

/// <summary>The role this configuration assumes.</summary>
public string? RoleArn { get; set; }

/// <summary>Region whose rolesanywhere endpoint is called.</summary>
public string? Region { get; set; }
```

- [ ] **Step 3c: Map the columns**

```csharp
// add inside ConfigureConnections' modelBuilder.Entity<ProviderCredentialEntity>(entity => { ... }),
// after the entity.Property(e => e.CreatedByUserId)... block (KnowledgeDbContext.cs:496-497)

entity.Property(e => e.CertificatePem).HasColumnName("certificate_pem");
entity.Property(e => e.PrivateKeyProtected).HasColumnName("private_key_protected");
entity.Property(e => e.TrustAnchorArn).HasColumnName("trust_anchor_arn").HasMaxLength(2048);
entity.Property(e => e.ProfileArn).HasColumnName("profile_arn").HasMaxLength(2048);
entity.Property(e => e.RoleArn).HasColumnName("role_arn").HasMaxLength(2048);
entity.Property(e => e.Region).HasColumnName("region").HasMaxLength(64);
```

- [ ] **Step 3d: Implement the store methods and clear-on-save**

```csharp
// add to src/Connapse.Storage/Connections/PostgresProviderCredentialStore.cs

public async Task<RolesAnywhereConfig?> GetRolesAnywhereAsync(string provider, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);

    var row = await db.ProviderCredentials
        .AsNoTracking()
        .Where(c => c.Provider == provider)
        .Select(c => new { c.CertificatePem, c.TrustAnchorArn, c.ProfileArn, c.RoleArn, c.Region })
        .FirstOrDefaultAsync(ct);

    // TrustAnchorArn is the mode signal: absent means this row is not a Roles Anywhere config.
    if (row is null || string.IsNullOrEmpty(row.TrustAnchorArn))
        return null;

    return new RolesAnywhereConfig(
        row.CertificatePem ?? string.Empty, row.TrustAnchorArn, row.ProfileArn ?? string.Empty,
        row.RoleArn ?? string.Empty, row.Region ?? string.Empty);
}

public async Task<string?> GetRolesAnywherePrivateKeyAsync(string provider, CancellationToken ct = default)
{
    await using var db = await factory.CreateDbContextAsync(ct);

    string? ciphertext = await db.ProviderCredentials
        .AsNoTracking()
        .Where(c => c.Provider == provider)
        .Select(c => c.PrivateKeyProtected)
        .FirstOrDefaultAsync(ct);

    if (string.IsNullOrEmpty(ciphertext))
        return null;

    try
    {
        return Protector.Unprotect(ciphertext);
    }
    catch (Exception ex)
    {
        throw new ProviderCredentialUnavailableException(provider, ex);
    }
}

public async Task<ProviderCredentialInfo> SaveRolesAnywhereAsync(
    string provider, RolesAnywhereConfig config, string privateKeyPem, string? principalName,
    Guid? createdByUserId, CancellationToken ct = default)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(provider);
    ArgumentException.ThrowIfNullOrWhiteSpace(config.CertificatePem);
    ArgumentException.ThrowIfNullOrWhiteSpace(config.TrustAnchorArn);
    ArgumentException.ThrowIfNullOrWhiteSpace(config.ProfileArn);
    ArgumentException.ThrowIfNullOrWhiteSpace(config.RoleArn);
    ArgumentException.ThrowIfNullOrWhiteSpace(config.Region);
    ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

    await using var db = await factory.CreateDbContextAsync(ct);

    var existing = await db.ProviderCredentials.FirstOrDefaultAsync(c => c.Provider == provider, ct);
    var now = DateTime.UtcNow;

    if (existing is null)
    {
        existing = new ProviderCredentialEntity { Provider = provider };
        db.ProviderCredentials.Add(existing);
    }

    existing.CertificatePem = config.CertificatePem;
    existing.PrivateKeyProtected = Protector.Protect(privateKeyPem);
    existing.TrustAnchorArn = config.TrustAnchorArn;
    existing.ProfileArn = config.ProfileArn;
    existing.RoleArn = config.RoleArn;
    existing.Region = config.Region;
    existing.PrincipalName = string.IsNullOrWhiteSpace(principalName) ? null : principalName.Trim();

    // Clear the access-key shape so GetRolesAnywhereAsync and the access-key reads are mutually exclusive.
    existing.PublicId = string.Empty;
    existing.SecretProtected = string.Empty;

    existing.CreatedAt = now;
    existing.CreatedByUserId = createdByUserId;
    existing.VerifiedAt = null;

    await db.SaveChangesAsync(ct);

    return new ProviderCredentialInfo(provider, existing.PublicId, existing.PrincipalName, now);
}
```

Then, in the existing `SaveAsync` (access-key), clear the Roles Anywhere columns so switching back to an access key is unambiguous. Add these lines just before `existing.CreatedAt = now;` (PostgresProviderCredentialStore.cs:91):

```csharp
        // Clear any Roles Anywhere config so the two shapes never coexist on one row.
        existing.CertificatePem = null;
        existing.PrivateKeyProtected = null;
        existing.TrustAnchorArn = null;
        existing.ProfileArn = null;
        existing.RoleArn = null;
        existing.Region = null;
```

- [ ] **Step 3e: Generate the migration**

Run: `dotnet ef migrations add AddRolesAnywhereCredentialFields --project src/Connapse.Storage`
Then open the generated `*_AddRolesAnywhereCredentialFields.cs` and confirm it only `AddColumn`s the six new nullable columns on `provider_credentials` (no alteration of `public_id`/`secret_protected`), and that `KnowledgeDbContextModelSnapshot.cs` was updated.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj --filter "FullyQualifiedName~RolesAnywhereCredentialStoreIntegrationTests"`
Expected: PASS (3 tests). Docker must be running.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Interfaces/IProviderCredentialStore.cs src/Connapse.Storage/Data src/Connapse.Storage/Migrations src/Connapse.Storage/Connections/PostgresProviderCredentialStore.cs tests/Connapse.Integration.Tests/RolesAnywhereCredentialStoreIntegrationTests.cs
git commit -m "feat: persist Roles Anywhere credential config (additive)"
```

---

## Task 2: Wire ConnapseAwsCredentials to the Roles Anywhere engine

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/ConnapseAwsCredentials.cs`
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/ConnapseAwsCredentialsTests.cs`

**Interfaces:**
- Consumes: `IProviderCredentialStore.GetRolesAnywhereAsync/GetRolesAnywherePrivateKeyAsync` (Task 1); `RolesAnywhereClient`, `RolesAnywhereParameters`, `RolesAnywhereSession` (PR 1).
- Produces: `const string ConnapseAwsCredentials.RolesAnywhereHttpClientName = "RolesAnywhere"`. Priority: Roles Anywhere config → access key → ambient chain.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/ConnapseAwsCredentialsTests.cs
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Amazon.Runtime;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ConnapseAwsCredentialsTests
{
    [Fact]
    public void GetCredentials_WithStoredRolesAnywhereConfig_ReturnsTemporaryCredentialsFromCreateSession()
    {
        (string certPem, string keyPem) = NewCertAndKeyPem();
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetRolesAnywhereAsync("aws").Returns(new RolesAnywhereConfig(
            certPem,
            "arn:aws:rolesanywhere:us-east-1:111:trust-anchor/ta",
            "arn:aws:rolesanywhere:us-east-1:111:profile/pf",
            "arn:aws:iam::111:role/connapse",
            "us-east-1"));
        store.GetRolesAnywherePrivateKeyAsync("aws").Returns(keyPem);

        const string sessionJson = """
        {"credentialSet":[{"credentials":{"accessKeyId":"ASIA_RA","secretAccessKey":"ra-secret","sessionToken":"ra-token","expiration":"2999-01-01T00:00:00Z"}}]}
        """;
        var factory = HttpClientFactoryReturning(HttpStatusCode.Created, sessionJson);

        var credentials = BuildCredentials(store, factory);

        ImmutableCredentials resolved = credentials.GetCredentials();
        resolved.AccessKey.Should().Be("ASIA_RA");
        resolved.Token.Should().Be("ra-token");
    }

    [Fact]
    public void GetCredentials_WithStoredAccessKeyAndNoRolesAnywhere_ReturnsTheAccessKey()
    {
        var store = Substitute.For<IProviderCredentialStore>();
        store.GetRolesAnywhereAsync("aws").Returns((RolesAnywhereConfig?)null);
        store.GetAsync("aws").Returns(new ProviderCredentialInfo("aws", "AKIAEXAMPLE", "connapse-reader", DateTime.UtcNow));
        store.GetSecretAsync("aws").Returns("static-secret");
        var factory = HttpClientFactoryReturning(HttpStatusCode.Created, "{}"); // never called

        var credentials = BuildCredentials(store, factory);

        ImmutableCredentials resolved = credentials.GetCredentials();
        resolved.AccessKey.Should().Be("AKIAEXAMPLE");
        resolved.SecretKey.Should().Be("static-secret");
    }

    private static ConnapseAwsCredentials BuildCredentials(IProviderCredentialStore store, IHttpClientFactory factory)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        services.AddSingleton(factory);
        ServiceProvider provider = services.BuildServiceProvider();
        return new ConnapseAwsCredentials(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConnapseAwsCredentials>.Instance);
    }

    private static IHttpClientFactory HttpClientFactoryReturning(HttpStatusCode status, string body)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new StubHandler(status, body)));
        return factory;
    }

    private static (string CertPem, string KeyPem) NewCertAndKeyPem()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=connapse-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ConnapseAwsCredentialsTests"`
Expected: FAIL — the Roles Anywhere branch does not exist yet, so the first test resolves nothing and returns ambient credentials (or throws), not `ASIA_RA`.

- [ ] **Step 3a: Add the Roles Anywhere branch to ConnapseAwsCredentials**

Add the using directives at the top of `ConnapseAwsCredentials.cs`:

```csharp
using System.Security.Cryptography.X509Certificates;
using Connapse.Storage.CloudScope.RolesAnywhere;
```

Add the constant beside `ProviderKey`:

```csharp
/// <summary>Name of the named HttpClient used for Roles Anywhere CreateSession calls.</summary>
public const string RolesAnywhereHttpClientName = "RolesAnywhere";
```

Add a private record near the bottom of the class:

```csharp
/// <summary>A resolved credential and, for Roles Anywhere, when it expires (null for a static key).</summary>
private sealed record ResolvedCredentials(ImmutableCredentials Credentials, DateTime? Expiration);
```

Replace `GenerateNewCredentials` so it honours a Roles Anywhere expiry:

```csharp
protected override CredentialsRefreshState GenerateNewCredentials()
{
    ResolvedCredentials? resolved = ResolveStored();

    if (resolved is not null)
    {
        DateTime expiry = DateTime.UtcNow.Add(RefreshWindow);
        // For Roles Anywhere, never hand back a credential past its own expiry; otherwise the
        // RefreshWindow governs, so a rotation still takes effect promptly.
        if (resolved.Expiration is DateTime exp && exp < expiry)
            expiry = exp;
        return new CredentialsRefreshState(resolved.Credentials, expiry);
    }

    var ambient = Amazon.Runtime.Credentials.DefaultAWSCredentialsIdentityResolver
        .GetCredentials(new Amazon.S3.AmazonS3Config());

    return new CredentialsRefreshState(
        ambient.GetCredentials(), DateTime.UtcNow.Add(RefreshWindow));
}
```

Change `ResolveStored`'s return type and the wrapped call to `ResolvedCredentials?` (only the type on the method signature and the `Task.Run` generic change; the catch blocks are unchanged):

```csharp
private ResolvedCredentials? ResolveStored()
{
    try
    {
        return Task.Run(ReadStoredAsync).GetAwaiter().GetResult();
    }
    // ... existing catch blocks unchanged ...
}
```

Replace `ReadStoredAsync` with the Roles-Anywhere-first version:

```csharp
private async Task<ResolvedCredentials?> ReadStoredAsync()
{
    using var scope = scopeFactory.CreateScope();
    var credentialStore = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();

    // A configured role outranks a static key, which outranks the ambient chain.
    RolesAnywhereConfig? roles = await credentialStore.GetRolesAnywhereAsync(ProviderKey);
    if (roles is not null)
    {
        string? privateKey = await credentialStore.GetRolesAnywherePrivateKeyAsync(ProviderKey);
        if (string.IsNullOrEmpty(privateKey)) return null;

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(roles.CertificatePem, privateKey);
        var client = new RolesAnywhereClient(httpClientFactory.CreateClient(RolesAnywhereHttpClientName));
        var parameters = new RolesAnywhereParameters(
            roles.TrustAnchorArn, roles.ProfileArn, roles.RoleArn, roles.Region);

        RolesAnywhereSession session = await client.CreateSessionAsync(
            certificate, parameters, DateTimeOffset.UtcNow);

        return new ResolvedCredentials(session.Credentials, session.Expiration.UtcDateTime);
    }

    var info = await credentialStore.GetAsync(ProviderKey);
    if (info is null) return null;

    string? secret = await credentialStore.GetSecretAsync(ProviderKey);
    if (string.IsNullOrEmpty(secret)) return null;

    return new ResolvedCredentials(new ImmutableCredentials(info.PublicId, secret, null), null);
}
```

- [ ] **Step 3b: Register the named HttpClient**

In `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`, inside `AddConnapseStorage` near the existing `services.AddHttpClient<OllamaEmbeddingProvider>();` (line ~112), add:

```csharp
// Roles Anywhere CreateSession calls; named so tests can substitute the transport.
services.AddHttpClient(CloudScope.ConnapseAwsCredentials.RolesAnywhereHttpClientName);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ConnapseAwsCredentialsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/ConnapseAwsCredentials.cs src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Storage.Tests/CloudScope/ConnapseAwsCredentialsTests.cs
git commit -m "feat: resolve AWS credentials via Roles Anywhere when configured"
```

---

## Task 3: Cross-check the signature against aws_signing_helper (offline)

**Files:**
- Test: `tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignHelperVectorTests.cs`

**What this validates and what it does not.** `aws_signing_helper sign-string` signs a string from stdin with a private key using the exact scheme AWS Roles Anywhere uses (RSA PKCS#1 v1.5 over SHA-256, deterministic). This test pins its output for a fixed key + fixed string as a golden vector and asserts our `RolesAnywhereSigner.SignBytes` produces the identical bytes — a cross-implementation check of the **signature primitive** against AWS's own tool. It does **not** validate full canonical-request construction end-to-end; that is the live-AWS smoke test deferred to PR 3.

**Execution note (this is where the golden constant comes from — not a placeholder to invent):** the `GOLDEN_SIGNATURE_HEX` constant below is produced by running AWS's tool once against the checked-in fixed key. Steps 1–3 describe obtaining it. Because RSA PKCS#1 v1.5 is deterministic, the captured value is stable across runs, so the test needs no binary at run time. If the binary cannot be obtained in this environment, STOP and report BLOCKED — do not invent or approximate the constant.

- [ ] **Step 1: Add a fixed RSA key + cert fixture and capture the golden signature**

Generate a fixed keypair once and write the PEMs into the test as constants (any RSA-2048 key works; it just has to be the SAME one the tool signs with). From a shell:

```bash
openssl req -x509 -newkey rsa:2048 -keyout ra-test-key.pem -out ra-test-cert.pem -days 3650 -nodes -subj "/CN=connapse-signhelper-test"
```

Obtain `aws_signing_helper` (official release binary from https://docs.aws.amazon.com/rolesanywhere/latest/userguide/credential-helper.html, or `go install github.com/aws/rolesanywhere-credential-helper/cmd/aws_signing_helper@latest`). Then capture the signature of a fixed string:

```bash
STS='AWS4-X509-RSA-SHA256
20260901T120000Z
20260901/us-east-1/rolesanywhere/aws4_request
abc123'
printf '%s' "$STS" | aws_signing_helper sign-string --private-key ra-test-key.pem --digest SHA256 --format bin | xxd -p -c 256
```

Record the lowercase-hex output as `GOLDEN_SIGNATURE_HEX`, and paste the two PEMs and the exact `STS` string into the test. (Confirm the tool's `--format` produces raw signature bytes; `bin` piped through `xxd -p` yields hex. If `--format` options differ in the installed version, capture whatever it emits and normalise to lowercase hex — the value pinned must be the tool's signature over that exact string with that key.)

- [ ] **Step 2: Write the test with the captured golden vector**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignHelperVectorTests.cs
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Connapse.Storage.CloudScope.RolesAnywhere;
using FluentAssertions;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope.RolesAnywhere;

[Trait("Category", "Unit")]
public class RolesAnywhereSignHelperVectorTests
{
    // Fixed test key + cert (throwaway, generated for this vector only). See the plan's Task 3 Step 1.
    private const string CertPem = "-----BEGIN CERTIFICATE-----\n<captured>\n-----END CERTIFICATE-----";
    private const string KeyPem = "-----BEGIN PRIVATE KEY-----\n<captured>\n-----END PRIVATE KEY-----";

    // Exactly the string signed by aws_signing_helper in Step 1 (note the real newlines).
    private const string StringToSign =
        "AWS4-X509-RSA-SHA256\n20260901T120000Z\n20260901/us-east-1/rolesanywhere/aws4_request\nabc123";

    // Lowercase hex of `aws_signing_helper sign-string` over StringToSign with KeyPem (captured, Step 1).
    private const string GoldenSignatureHex = "<captured>";

    [Fact]
    public void SignBytes_MatchesAwsSigningHelperForFixedKeyAndString()
    {
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(CertPem, KeyPem);

        byte[] signature = RolesAnywhereSigner.SignBytes(
            cert, RolesAnywhereSigner.RsaAlgorithm, Encoding.UTF8.GetBytes(StringToSign));

        Convert.ToHexStringLower(signature).Should().Be(GoldenSignatureHex);
    }
}
```

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~RolesAnywhereSignHelperVectorTests"`
Expected: PASS. A mismatch means our signing bytes differ from AWS's tool — a real defect, not a test to relax.

- [ ] **Step 4: Commit**

```bash
git add tests/Connapse.Storage.Tests/CloudScope/RolesAnywhere/RolesAnywhereSignHelperVectorTests.cs
git commit -m "test: cross-check signing primitive against aws_signing_helper vector"
```

---

## Self-Review

**Spec coverage (delivery step 2, storage + wiring half):**
- "reshape storage to Roles Anywhere fields (+ migration)" → Task 1 (additive form: new nullable columns + migration; the *reshape/removal* to a single shape is PR 3, per the deletion-timing decision).
- "wire the engine into `ConnapseAwsCredentials` behind the mode switch" → Task 2.
- Acceptance requirement "validate signing vs aws_signing_helper" → Task 3 (signature-primitive cross-check; full end-to-end live-AWS validation is PR 3, per the UI-timing decision).
- Deferred to PR 3 (explicitly out of scope here): delete `AwsIamUserSetup`, remove the access-key branch, drop the old columns, the adaptive Access-card UI, the BYO intermediate-chain header, the live-AWS smoke test. `AwsRolesAnywhereSetup` script generator + keypair generation are PR 2b.

**Placeholder scan:** the only deferred literals are the Task 3 golden constants, which are captured from AWS's tool during execution (documented as the reference oracle, with a BLOCKED path if the binary is unobtainable) — not invent-later placeholders.

**Type consistency:** `RolesAnywhereConfig`, the three store methods, `ResolvedCredentials`, and `RolesAnywhereHttpClientName` are referenced identically across tasks. Task 2 consumes exactly the Task 1 signatures and the PR 1 `RolesAnywhereClient`/`RolesAnywhereParameters`/`RolesAnywhereSession` types.

**Risks called out for execution:** Task 1's integration test needs Docker (Testcontainers). Task 3 needs the `aws_signing_helper` binary; if it cannot be obtained, it is BLOCKED and the validation falls to PR 3's live-AWS smoke test — surface that to the user rather than approximating.
