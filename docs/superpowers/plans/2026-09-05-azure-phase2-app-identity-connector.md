# Azure Phase 2 — ConnapseAzureCredentials + Blob Connector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver Azure Blob ingestion end-to-end — Connapse's own Azure identity (ambient managed identity, or a configured service-principal certificate) plus a rebuilt read-only Azure Blob connector, creatable through the Connections/Sources UI.

**Architecture:** Mirror the existing S3 path. A pure `AzureCredentialChainFactory` turns settings into a `TokenCredential` (unit-testable without Azure); `ConnapseAzureCredentials` wraps it with `IOptionsMonitor`. `AzureBlobConnector` implements `IConnector` and accepts an injectable `BlobServiceClient` so the Azurite integration test can use a shared-key client (Azurite cannot authenticate an AAD `TokenCredential`). `ConnectorFactory` recombines connection config + source scope, exactly as it does for S3.

**Tech Stack:** .NET 10, `Azure.Storage.Blobs`, `Azure.Identity` (`TokenCredential`, `ChainedTokenCredential`, `ClientCertificateCredential`, `ManagedIdentityCredential`), xUnit + FluentAssertions + NSubstitute, Testcontainers.Azurite.

**Spec:** `docs/superpowers/specs/2026-09-05-azure-phase2-app-identity-connector-design.md` (parent: `docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md`).

## Global Constraints

- **One credential story, two contexts** — configured **certificate** (off-Azure) → **ambient managed identity** (in-Azure) → **fail closed**. No client-secret, no workload-identity-federation, no credential-kind chooser.
- **Credential config from the settings hierarchy only** (`AzureProviderSettings`), NOT `ProviderCredentialEntity`. No DB-stored/encrypted credential in this phase.
- Use an explicit **`ChainedTokenCredential`**, never `DefaultAzureCredential`.
- **No per-user permission logic** (Phase 4). **No guided Providers setup page.** **No live-watch** (`SupportsLiveWatch = false`).
- Enum values: `ConnectorType.AzureBlob = 4`, `ConnectionProvider.AzureBlob = 4`, `CloudProvider.Azure = 1` — do not renumber existing members.
- Don't touch Azure OpenAI / AI Foundry, JWT/SAML, or the AWS path.
- .NET conventions (CLAUDE.md): file-scoped namespaces, records for DTOs/settings, primary constructors for DI, async all the way, no `var` for primitives, parameterized SQL only.
- Build: `dotnet build`. Tests: `dotnet test --filter "Category=Unit"` (no Docker), `dotnet test --filter "Category=Integration"` (Docker). Tag tests with `[Trait("Category","Unit")]` or `[Trait("Category","Integration")]`.

---

### Task 1: `AzureProviderSettings` + `AzureCredentialChainFactory`

The pure credential-selection logic — the novel core, unit-testable without Azure.

**Files:**
- Create: `src/Connapse.Core/Models/AzureProviderSettings.cs`
- Create: `src/Connapse.Storage/CloudScope/AzureCredentialChainFactory.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureCredentialChainFactoryTests.cs`
- Modify: `src/Connapse.Storage/Connapse.Storage.csproj` (add `Azure.Identity`)

**Interfaces:**
- Produces: `record AzureProviderSettings { string? TenantId; string? ClientId; string? ClientCertificatePath; string? ClientCertificatePassword; string? UserAssignedManagedIdentityClientId; const string SectionName = "Providers:Azure"; }`
- Produces: `static class AzureCredentialChainFactory { static TokenCredential Create(AzureProviderSettings settings, Func<AzureProviderSettings, X509Certificate2?> certLoader); }` — returns a `ChainedTokenCredential`; **throws `InvalidOperationException`** when `ClientId` is set but `certLoader` returns null (misconfiguration must not silently fall through to ambient).

- [ ] **Step 1: Add the `Azure.Identity` package**

Add to `src/Connapse.Storage/Connapse.Storage.csproj` (version pinned to the latest 1.x the restore resolves, ≥ 1.16.0 for the resilient MI retry mode):
```xml
<PackageReference Include="Azure.Identity" Version="1.16.0" />
```
Run: `dotnet restore` — expect success.

- [ ] **Step 2: Write `AzureProviderSettings`**

```csharp
namespace Connapse.Core;

/// <summary>
/// Connapse's own Azure app-credential configuration, bound from the settings hierarchy.
/// When ClientId + a certificate are present, Connapse authenticates as that service principal;
/// otherwise it uses the ambient managed identity. Never a client secret.
/// </summary>
public record AzureProviderSettings
{
    public const string SectionName = "Providers:Azure";

    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificatePassword { get; init; }
    public string? UserAssignedManagedIdentityClientId { get; init; }
}
```

- [ ] **Step 3: Write the failing tests**

```csharp
using Azure.Identity;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureCredentialChainFactoryTests
{
    private static X509Certificate2 SelfSigned() =>
        new CertificateRequest("CN=connapse-test",
            System.Security.Cryptography.ECDsa.Create(),
            HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

    [Fact]
    public void Create_WithClientIdAndCert_UsesCertificateCredential()
    {
        var settings = new AzureProviderSettings { TenantId = "t", ClientId = "c" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => SelfSigned());
        cred.Should().BeOfType<ChainedTokenCredential>();
        // First source is the cert credential when configured.
        FirstSource(cred).Should().BeOfType<ClientCertificateCredential>();
    }

    [Fact]
    public void Create_NoCert_SystemAssignedManagedIdentity()
    {
        var cred = AzureCredentialChainFactory.Create(new AzureProviderSettings(), _ => null);
        FirstSource(cred).Should().BeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void Create_NoCert_UserAssignedManagedIdentity()
    {
        var settings = new AzureProviderSettings { UserAssignedManagedIdentityClientId = "mi-client" };
        var cred = AzureCredentialChainFactory.Create(settings, _ => null);
        FirstSource(cred).Should().BeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void Create_ClientIdSetButCertMissing_Throws()
    {
        var settings = new AzureProviderSettings { TenantId = "t", ClientId = "c" };
        var act = () => AzureCredentialChainFactory.Create(settings, _ => null);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*certificate*");
    }

    // Reads the private _sources array ChainedTokenCredential stores, to assert ordering.
    private static TokenCredential FirstSource(TokenCredential chain)
    {
        var field = typeof(ChainedTokenCredential)
            .GetField("_sources", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var sources = (TokenCredential[])field.GetValue(chain)!;
        return sources[0];
    }
}
```
> Note: if a future `Azure.Identity` renames the private `_sources` field, switch `FirstSource` to assert behavior via a stubbed `certLoader`/env instead. The public contract under test is "cert first when configured, else MI, else throw."

- [ ] **Step 4: Run tests, verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AzureCredentialChainFactoryTests"`
Expected: FAIL — `AzureCredentialChainFactory` does not exist.

- [ ] **Step 5: Implement `AzureCredentialChainFactory`**

```csharp
using Azure.Core;
using Azure.Identity;
using Connapse.Core;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Pure selection of Connapse's Azure credential from settings: a configured
/// service-principal certificate first, else the ambient managed identity, else fail closed.
/// Deterministic (an explicit ChainedTokenCredential) — never DefaultAzureCredential.
/// </summary>
public static class AzureCredentialChainFactory
{
    public static TokenCredential Create(
        AzureProviderSettings settings,
        Func<AzureProviderSettings, X509Certificate2?> certLoader)
    {
        var sources = new List<TokenCredential>();

        if (!string.IsNullOrWhiteSpace(settings.ClientId))
        {
            X509Certificate2 cert = certLoader(settings)
                ?? throw new InvalidOperationException(
                    "Azure ClientId is configured but no usable certificate was loaded "
                    + $"(ClientCertificatePath='{settings.ClientCertificatePath}'). "
                    + "Fix the certificate configuration; Connapse will not silently fall back to managed identity.");

            sources.Add(new ClientCertificateCredential(
                settings.TenantId, settings.ClientId, cert,
                new ClientCertificateCredentialOptions { SendCertificateChain = true }));
        }

        sources.Add(string.IsNullOrWhiteSpace(settings.UserAssignedManagedIdentityClientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(
                ManagedIdentityId.FromUserAssignedClientId(settings.UserAssignedManagedIdentityClientId)));

        return new ChainedTokenCredential(sources.ToArray());
    }
}
```

- [ ] **Step 6: Run tests, verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AzureCredentialChainFactoryTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Core/Models/AzureProviderSettings.cs src/Connapse.Storage/CloudScope/AzureCredentialChainFactory.cs tests/Connapse.Storage.Tests/CloudScope/AzureCredentialChainFactoryTests.cs src/Connapse.Storage/Connapse.Storage.csproj
git commit -m "feat(azure): AzureProviderSettings + credential chain factory (#477)"
```

---

### Task 2: `ConnapseAzureCredentials`

The `TokenCredential` singleton every Azure SDK client consumes — wraps the factory with `IOptionsMonitor`, caching the chain and rebuilding on settings reload.

**Files:**
- Create: `src/Connapse.Storage/CloudScope/ConnapseAzureCredentials.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/ConnapseAzureCredentialsTests.cs`

**Interfaces:**
- Consumes: `AzureCredentialChainFactory.Create`, `AzureProviderSettings`.
- Produces: `class ConnapseAzureCredentials : TokenCredential { const string ProviderKey = "azure"; }` — overrides `GetToken`/`GetTokenAsync` to delegate to the current chain.

- [ ] **Step 1: Write the failing test**

```csharp
using Azure.Core;
using Azure.Identity;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ConnapseAzureCredentialsTests
{
    [Fact]
    public void ProviderKey_IsAzure() => ConnapseAzureCredentials.ProviderKey.Should().Be("azure");

    [Fact]
    public void GetToken_WithNoAzureEnvironment_FailsClosed()
    {
        // No cert, no managed-identity endpoint reachable in a unit-test host → the chain
        // cannot produce a token and throws, rather than returning a bogus token.
        var monitor = new TestOptionsMonitor<AzureProviderSettings>(new AzureProviderSettings());
        var creds = new ConnapseAzureCredentials(monitor);
        var act = () => creds.GetToken(
            new TokenRequestContext(new[] { "https://storage.azure.com/.default" }), default);
        act.Should().Throw<CredentialUnavailableException>();
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
```
> The fail-closed assertion runs in a host with no managed-identity endpoint; `ManagedIdentityCredential` throws `CredentialUnavailableException`, which the chain surfaces. If a CI runner ever exposes an MI endpoint, gate this test with the `AZURE_*`-absent precondition.

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConnapseAzureCredentialsTests"`
Expected: FAIL — `ConnapseAzureCredentials` does not exist.

- [ ] **Step 3: Implement `ConnapseAzureCredentials`**

```csharp
using Azure.Core;
using Connapse.Core;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Connapse's own Azure identity: an explicit certificate-or-ambient-managed-identity chain,
/// rebuilt when settings reload. The single TokenCredential every Azure SDK client uses.
/// </summary>
public sealed class ConnapseAzureCredentials : TokenCredential, IDisposable
{
    public const string ProviderKey = "azure";

    private readonly IOptionsMonitor<AzureProviderSettings> _options;
    private readonly IDisposable? _reload;
    private readonly object _gate = new();
    private TokenCredential _current;

    public ConnapseAzureCredentials(IOptionsMonitor<AzureProviderSettings> options)
    {
        _options = options;
        _current = Build(options.CurrentValue);
        _reload = options.OnChange(settings =>
        {
            lock (_gate) { _current = Build(settings); }
        });
    }

    private TokenCredential Current { get { lock (_gate) { return _current; } } }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) =>
        Current.GetToken(requestContext, ct);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken ct) =>
        Current.GetTokenAsync(requestContext, ct);

    private static TokenCredential Build(AzureProviderSettings settings) =>
        AzureCredentialChainFactory.Create(settings, LoadCertificate);

    private static X509Certificate2? LoadCertificate(AzureProviderSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ClientCertificatePath)) return null;
        if (!File.Exists(s.ClientCertificatePath)) return null;

        string ext = Path.GetExtension(s.ClientCertificatePath).ToLowerInvariant();
        return ext is ".pem" or ".crt"
            ? X509Certificate2.CreateFromPemFile(s.ClientCertificatePath)
            : X509CertificateLoader.LoadPkcs12FromFile(s.ClientCertificatePath, s.ClientCertificatePassword);
    }

    public void Dispose() => _reload?.Dispose();
}
```
> `X509Certificate2.CreateFromPemFile` reads a PEM cert (with its key from the same file or a paired `.key`); `X509CertificateLoader.LoadPkcs12FromFile` is the .NET 9+ replacement for the obsolete `new X509Certificate2(path, password)` PFX ctor. If the target framework lacks `X509CertificateLoader`, use `new X509Certificate2(path, password)` and suppress the obsoletion locally.

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ConnapseAzureCredentialsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/ConnapseAzureCredentials.cs tests/Connapse.Storage.Tests/CloudScope/ConnapseAzureCredentialsTests.cs
git commit -m "feat(azure): ConnapseAzureCredentials TokenCredential singleton (#477)"
```

---

### Task 3: Enum values + `ResourceUri.ForAzureBlob`

Foundational identifiers the connector and factory consume.

**Files:**
- Modify: `src/Connapse.Core/Models/StorageModels.cs` (`ConnectorType`)
- Modify: `src/Connapse.Core/Models/ConnectionModels.cs` (`ConnectionProvider`)
- Modify: `src/Connapse.Core/Models/CloudProvider.cs`
- Modify: `src/Connapse.Core/Utilities/ResourceUri.cs`
- Test: `tests/Connapse.Core.Tests/Utilities/ResourceUriTests.cs`

**Interfaces:**
- Produces: `ConnectorType.AzureBlob = 4`, `ConnectionProvider.AzureBlob = 4`, `CloudProvider.Azure = 1`, `ResourceUri.ForAzureBlob(string account, string container, string path) → "azblob://{account}/{container}/{path}"`.

- [ ] **Step 1: Write the failing test** (append to `ResourceUriTests.cs`)

```csharp
[Fact]
[Trait("Category", "Unit")]
public void ForAzureBlob_BuildsAzblobUri()
{
    ResourceUri.ForAzureBlob("acct", "docs", "reports/q1.pdf")
        .Should().Be("azblob://acct/docs/reports/q1.pdf");
}

[Fact]
[Trait("Category", "Unit")]
public void ForAzureBlob_BlankAccount_Throws()
{
    var act = () => ResourceUri.ForAzureBlob("", "docs", "k");
    act.Should().Throw<ArgumentException>();
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ResourceUriTests.ForAzureBlob"`
Expected: FAIL — `ForAzureBlob` not defined.

- [ ] **Step 3: Implement**

In `ResourceUri.cs`, add beside `ForS3`:
```csharp
public static string ForAzureBlob(string account, string container, string path)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(account);
    ArgumentException.ThrowIfNullOrWhiteSpace(container);
    return $"azblob://{account}/{container}/{path}";
}
```
In the three enums, add the Azure member (do not renumber others):
- `ConnectorType`: `... S3 = 3, AzureBlob = 4, Sftp = 5 }`
- `ConnectionProvider`: `... S3 = 3, AzureBlob = 4, Sftp = 5 }`
- `CloudProvider`: `{ AWS = 0, Azure = 1 }`

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ResourceUriTests.ForAzureBlob"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/StorageModels.cs src/Connapse.Core/Models/ConnectionModels.cs src/Connapse.Core/Models/CloudProvider.cs src/Connapse.Core/Utilities/ResourceUri.cs tests/Connapse.Core.Tests/Utilities/ResourceUriTests.cs
git commit -m "feat(azure): re-add AzureBlob/Azure enum values + ResourceUri.ForAzureBlob (#477)"
```

---

### Task 4: `AzureBlobConnectorConfig` + `AzureBlobConnector`

**Files:**
- Create: `src/Connapse.Storage/Connectors/AzureBlobConnectorConfig.cs`
- Create: `src/Connapse.Storage/Connectors/AzureBlobConnector.cs`
- Modify: `src/Connapse.Storage/Connapse.Storage.csproj` (add `Azure.Storage.Blobs`)
- Test: `tests/Connapse.Core.Tests/Connectors/AzureBlobConnectorUnitTests.cs`

**Interfaces:**
- Consumes: `ResourceUri.ForAzureBlob`, `ConnectorType.AzureBlob`, `TokenCredential`.
- Produces: `record AzureBlobConnectorConfig { string AccountName; string ContainerName; string? Prefix; string? BlobEndpoint; }`; `class AzureBlobConnector : IConnector, IDisposable` with a public ctor `(AzureBlobConnectorConfig config, TokenCredential credential)` and an `internal` ctor `(AzureBlobConnectorConfig config, BlobServiceClient client)` used by tests.

- [ ] **Step 1: Add the `Azure.Storage.Blobs` package**

Add to `Connapse.Storage.csproj`:
```xml
<PackageReference Include="Azure.Storage.Blobs" Version="12.24.0" />
```
Run: `dotnet restore` — expect success.

- [ ] **Step 2: Write the failing unit test** (network-free surface only: `Type`, `SupportsLiveWatch`, `ResolveJobPath`, `WatchAsync` throws)

```csharp
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Connectors;

[Trait("Category", "Unit")]
public class AzureBlobConnectorUnitTests
{
    private static AzureBlobConnector Make() =>
        new(new AzureBlobConnectorConfig { AccountName = "acct", ContainerName = "docs", Prefix = "reports/" },
            new BlobServiceClient(new Uri("http://127.0.0.1:10000/devstoreaccount1")));

    [Fact] public void Type_IsAzureBlob() => Make().Type.Should().Be(ConnectorType.AzureBlob);
    [Fact] public void SupportsLiveWatch_False() => Make().SupportsLiveWatch.Should().BeFalse();

    [Fact]
    public void ResolveJobPath_JoinsUnderPrefix()
        => Make().ResolveJobPath("q1.pdf").Should().Be("reports/q1.pdf");

    [Fact]
    public async Task WatchAsync_Throws()
    {
        var act = async () => { await foreach (var _ in Make().WatchAsync()) { } };
        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
```

- [ ] **Step 3: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~AzureBlobConnectorUnitTests"`
Expected: FAIL — types not defined.

- [ ] **Step 4: Implement config + connector**

`AzureBlobConnectorConfig.cs`:
```csharp
namespace Connapse.Storage.Connectors;

/// <summary>Recombined connection (account/endpoint) + source (container/prefix) config for the Azure Blob connector.</summary>
public record AzureBlobConnectorConfig
{
    public string AccountName { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public string? Prefix { get; init; }
    /// <summary>Overrides https://{account}.blob.core.windows.net (Azurite/local).</summary>
    public string? BlobEndpoint { get; init; }
}
```

`AzureBlobConnector.cs`:
```csharp
using Azure.Core;
using Azure.Storage.Blobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using System.Runtime.CompilerServices;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Read-only Azure Blob Storage connector. Builds a BlobServiceClient from the account +
/// Connapse's TokenCredential; the internal ctor accepts a prebuilt client for tests
/// (Azurite cannot authenticate an AAD TokenCredential). SupportsLiveWatch = false.
/// </summary>
public sealed class AzureBlobConnector : IConnector, IDisposable
{
    private readonly AzureBlobConnectorConfig _config;
    private readonly BlobContainerClient _container;

    public AzureBlobConnector(AzureBlobConnectorConfig config, TokenCredential credential)
        : this(config, new BlobServiceClient(
            new Uri(config.BlobEndpoint ?? $"https://{config.AccountName}.blob.core.windows.net"),
            credential))
    { }

    internal AzureBlobConnector(AzureBlobConnectorConfig config, BlobServiceClient client)
    {
        _config = config;
        _container = client.GetBlobContainerClient(config.ContainerName);
    }

    public ConnectorType Type => ConnectorType.AzureBlob;
    public bool SupportsLiveWatch => false;

    public string ResolveJobPath(string relativePath) => CombinePrefix(relativePath);

    public async Task<IReadOnlyList<ConnectorFile>> ListFilesAsync(string? prefix = null, CancellationToken ct = default)
    {
        string effective = CombinePrefix(prefix ?? "");
        var files = new List<ConnectorFile>();
        await foreach (var item in _container.GetBlobsAsync(prefix: effective, cancellationToken: ct))
        {
            files.Add(new ConnectorFile(
                item.Name,
                item.Properties.ContentLength ?? 0,
                (item.Properties.LastModified ?? DateTimeOffset.MinValue).UtcDateTime,
                item.Properties.ContentType,
                ResourceUri.ForAzureBlob(_config.AccountName, _config.ContainerName, item.Name)));
        }
        return files;
    }

    public async Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        var download = await _container.GetBlobClient(path).DownloadStreamingAsync(cancellationToken: ct);
        return download.Value.Content;
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
        => await _container.GetBlobClient(path).ExistsAsync(ct);

    public async IAsyncEnumerable<ConnectorFileEvent> WatchAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        throw new NotSupportedException("Azure Blob connector does not support live watch.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private string CombinePrefix(string tail)
    {
        string p = _config.Prefix?.Trim('/') ?? "";
        string t = tail.TrimStart('/');
        return string.IsNullOrEmpty(p) ? t : string.IsNullOrEmpty(t) ? p + "/" : $"{p}/{t}";
    }

    public void Dispose() { }
}
```
> `ListFilesAsync` correctness (real enumeration, prefix scoping, streaming reads) is covered by the Azurite integration test in Task 8; this unit test covers only the network-free surface.

- [ ] **Step 5: Run test, verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~AzureBlobConnectorUnitTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Storage/Connectors/AzureBlobConnectorConfig.cs src/Connapse.Storage/Connectors/AzureBlobConnector.cs src/Connapse.Storage/Connapse.Storage.csproj tests/Connapse.Core.Tests/Connectors/AzureBlobConnectorUnitTests.cs
git commit -m "feat(azure): AzureBlobConnector + config (#477)"
```

---

### Task 5: `ConnectorFactory` Azure arm

**Files:**
- Modify: `src/Connapse.Storage/Connectors/ConnectorFactory.cs` (add `ConnapseAzureCredentials` to the primary ctor; add the `ConnectionProvider.AzureBlob` switch arm)
- Test: `tests/Connapse.Core.Tests/Connectors/SourceConnectorFactoryTests.cs` (add the Azure case)

**Interfaces:**
- Consumes: `ConnapseAzureCredentials` (as `TokenCredential`), `AzureBlobConnector`, `AzureBlobConnectorConfig`, `ConnectionProvider.AzureBlob`.
- Produces: `IConnectorFactory.Create(source, connection, secret)` returns an `AzureBlobConnector` for `ConnectionProvider.AzureBlob`, reading `accountName`/`blobEndpoint` from the connection config and `containerName`/`prefix` from the source scope.

- [ ] **Step 1: Write the failing test** (mirror the existing S3 factory test in the same file)

```csharp
[Fact]
[Trait("Category", "Unit")]
public void Create_AzureBlob_BuildsConnectorFromSplitConfig()
{
    var connection = TestConnection(ConnectionProvider.AzureBlob,
        configJson: """{"accountName":"acct"}""");
    var source = TestSource(connection.Id,
        scopeJson: """{"containerName":"docs","prefix":"reports/"}""");

    var connector = Factory().Create(source, connection);

    connector.Should().BeOfType<AzureBlobConnector>();
    connector.Type.Should().Be(ConnectorType.AzureBlob);
}
```
> Reuse the file's existing `TestConnection`/`TestSource`/`Factory` helpers (as the S3 case does). If `Factory()` needs the new ctor arg, pass a `ConnapseAzureCredentials` built with a `TestOptionsMonitor<AzureProviderSettings>(new())` — no network is touched because `Create` does not call the connector.

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SourceConnectorFactoryTests.Create_AzureBlob"`
Expected: FAIL — no AzureBlob arm / ctor arg missing.

- [ ] **Step 3: Implement the arm**

Add `ConnapseAzureCredentials azureCredentials` to `ConnectorFactory`'s constructor (beside the existing `awsCredentials`), store it, and add the switch arm after the S3 arm:
```csharp
ConnectionProvider.AzureBlob => new AzureBlobConnector(new AzureBlobConnectorConfig
{
    AccountName = Str(credential, "accountName")
        ?? throw new InvalidOperationException(
            $"Connection '{connection.Name}' has no accountName."),
    BlobEndpoint = Str(credential, "blobEndpoint"),
    ContainerName = RequirePermittedLocation(
        StorageLocationPolicy.ReadAllowedLocations(credential.RootElement),
        Str(scope, "containerName")
            ?? throw new InvalidOperationException(
                $"Source '{source.Name}' has no containerName in its scope."),
        Str(scope, "prefix"),
        connection.Name, source.Name),
    Prefix = Str(scope, "prefix"),
}, azureCredentials),
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~SourceConnectorFactoryTests"`
Expected: PASS (the new case + existing S3/Filesystem/Sftp cases).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Connectors/ConnectorFactory.cs tests/Connapse.Core.Tests/Connectors/SourceConnectorFactoryTests.cs
git commit -m "feat(azure): ConnectorFactory AzureBlob arm (#477)"
```

---

### Task 6: `AzureBlobConnectionTester`

**Files:**
- Create: `src/Connapse.Storage/ConnectionTesters/AzureBlobConnectionTester.cs`
- Test: `tests/Connapse.Core.Tests/ConnectionTesterTests.cs` (add Azure settings-shaping cases)

**Interfaces:**
- Consumes: `ConnapseAzureCredentials`, `AzureBlobConnectorConfig`, `ConnectionTestResult`.
- Produces: `class AzureBlobConnectionTester : IConnectionTester` — `TestConnectionAsync(object settings, …)` where `settings` is an `AzureBlobConnectorConfig`; lists ≤5 blobs; returns `ConnectionTestResult.CreateSuccess`/`CreateFailure`.

- [ ] **Step 1: Write the failing test** (settings-shaping + failure mapping, no network)

```csharp
[Fact]
[Trait("Category", "Unit")]
public async Task AzureBlobTester_WrongSettingsType_Fails()
{
    var tester = new AzureBlobConnectionTester(
        new ConnapseAzureCredentials(new TestOptionsMonitor<AzureProviderSettings>(new())));
    var result = await tester.TestConnectionAsync("not-a-config", TimeSpan.FromSeconds(1));
    result.Success.Should().BeFalse();
    result.Message.Should().Contain("AzureBlobConnectorConfig");
}
```
> `TestOptionsMonitor<T>` is the small stub from Task 2's test; extract it to a shared test helper (`tests/Connapse.Core.Tests/TestOptionsMonitor.cs`) if both projects need it, or duplicate the 5-line stub — do not add a production seam for it.

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ConnectionTesterTests.AzureBlobTester"`
Expected: FAIL — `AzureBlobConnectionTester` not defined.

- [ ] **Step 3: Implement** (mirror `S3ConnectionTester` structure)

```csharp
using Azure;
using Azure.Storage.Blobs;
using Connapse.Core.Interfaces;
using Connapse.Core.Models;
using Connapse.Storage.CloudScope;
using Connapse.Storage.Connectors;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>Validates an Azure Blob connection by listing a few blobs with Connapse's identity.</summary>
public sealed class AzureBlobConnectionTester(ConnapseAzureCredentials credentials) : IConnectionTester
{
    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not AzureBlobConnectorConfig cfg)
            return ConnectionTestResult.CreateFailure(
                "Invalid settings: expected AzureBlobConnectorConfig.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

        try
        {
            var service = new BlobServiceClient(
                new Uri(cfg.BlobEndpoint ?? $"https://{cfg.AccountName}.blob.core.windows.net"),
                credentials);
            var container = service.GetBlobContainerClient(cfg.ContainerName);

            int seen = 0;
            await foreach (var _ in container.GetBlobsAsync(prefix: cfg.Prefix, cancellationToken: cts.Token))
                if (++seen >= 5) break;

            return ConnectionTestResult.CreateSuccess(
                $"Connected to container '{cfg.ContainerName}' on account '{cfg.AccountName}'.");
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            return ConnectionTestResult.CreateFailure(
                "Authorization failed — Connapse's identity lacks read access to this container "
                + "(needs Storage Blob Data Reader).");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ConnectionTestResult.CreateFailure(
                $"Container '{cfg.ContainerName}' or account '{cfg.AccountName}' not found.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ConnectionTestResult.CreateFailure("Connection test timed out.");
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.CreateFailure($"Connection test failed: {ex.Message}");
        }
    }
}
```

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ConnectionTesterTests.AzureBlobTester"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/ConnectionTesters/AzureBlobConnectionTester.cs tests/Connapse.Core.Tests/ConnectionTesterTests.cs
git commit -m "feat(azure): AzureBlobConnectionTester (#477)"
```

---

### Task 7: DI + settings wiring

**Files:**
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/Connapse.Storage/Settings/DatabaseSettingsProvider.cs` (`CategoryPrefixMap`)
- Modify: `src/Connapse.Web/appsettings.json` (empty `Providers:Azure` section as documentation)
- Test: `tests/Connapse.Integration.Tests/` — add a DI-resolution assertion to an existing startup/integration fixture test (a clean container start proves the DI graph)

**Interfaces:**
- Consumes: everything above.
- Produces: `ConnapseAzureCredentials` (singleton), `AzureBlobConnectionTester` (scoped), `Configure<AzureProviderSettings>` bound to `"Providers:Azure"`, and the `ConnectorFactory` ctor's new arg satisfied.

- [ ] **Step 1: Write the failing test** — add to the existing DI/host integration test (find the one that resolves `IConnectorFactory`/testers; e.g. in `Connapse.Integration.Tests`):

```csharp
[Fact]
[Trait("Category", "Integration")]
public void Di_Resolves_AzureCredentialsAndTester()
{
    using var scope = _fixture.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<ConnapseAzureCredentials>().Should().NotBeNull();
    scope.ServiceProvider.GetServices<IConnectionTester>()
        .Should().Contain(t => t is AzureBlobConnectionTester);
    scope.ServiceProvider.GetRequiredService<IConnectorFactory>().Should().NotBeNull();
}
```
> Use the collection's shared `SharedWebAppFixture` (per CLAUDE.md) rather than a new host. If no such DI-resolution test exists yet, add this one to the existing integration collection.

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Di_Resolves_AzureCredentials"`
Expected: FAIL — `ConnapseAzureCredentials`/tester not registered (or `ConnectorFactory` ctor unsatisfied).

- [ ] **Step 3: Implement the registrations**

In `AddConnapseStorage` (`ServiceCollectionExtensions.cs`), beside the AWS credential + S3 tester registrations:
```csharp
services.Configure<AzureProviderSettings>(configuration.GetSection(AzureProviderSettings.SectionName));
services.AddSingleton<ConnapseAzureCredentials>();
services.AddScoped<IConnectionTester, AzureBlobConnectionTester>();
```
In `DatabaseSettingsProvider.CategoryPrefixMap`, add:
```csharp
["azure"] = "Providers:Azure",
```
In `appsettings.json`, add a documented empty section:
```json
"Providers": { "Azure": { "TenantId": "", "ClientId": "", "ClientCertificatePath": "", "UserAssignedManagedIdentityClientId": "" } }
```
(If `ConnectorFactory` is a singleton consuming `ConnapseAzureCredentials`, the new ctor arg is satisfied automatically once the singleton is registered.)

- [ ] **Step 4: Run test, verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~Di_Resolves_AzureCredentials"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs src/Connapse.Storage/Settings/DatabaseSettingsProvider.cs src/Connapse.Web/appsettings.json tests/Connapse.Integration.Tests/
git commit -m "feat(azure): DI + settings wiring for Azure provider (#477)"
```

---

### Task 8: Azurite integration tests

**Files:**
- Modify: `tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj` (add `Testcontainers.Azurite`)
- Create: `tests/Connapse.Integration.Tests/AzuriteFixture.cs`
- Create: `tests/Connapse.Integration.Tests/AzureBlobConnectorIntegrationTests.cs`
- Modify: `tests/Connapse.Integration.Tests/CloudConnectorTestCollection.cs` (add `AzureBlobConnectorTestCollection`)

**Interfaces:**
- Consumes: `AzureBlobConnector` (internal `BlobServiceClient` ctor), `AzureBlobConnectorConfig`.
- Produces: end-to-end coverage of list/read/exists/prefix/ResourceUri against Azurite.

- [ ] **Step 1: Add the package**

```xml
<PackageReference Include="Testcontainers.Azurite" Version="4.3.0" />
```
Run: `dotnet restore` — expect success.

- [ ] **Step 2: Write `AzuriteFixture`**

```csharp
using Testcontainers.Azurite;
using Xunit;

namespace Connapse.Integration.Tests;

public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container = new AzuriteBuilder().Build();
    public string ConnectionString => _container.GetConnectionString();
    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
```

- [ ] **Step 3: Write the failing integration test** (drives the connector via the shared-key client — Azurite can't auth AAD)

```csharp
using Azure.Storage.Blobs;
using Connapse.Storage.Connectors;
using FluentAssertions;
using System.Text;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("AzureBlobConnector")]
public class AzureBlobConnectorIntegrationTests(AzuriteFixture fixture)
{
    [Fact]
    public async Task ListAndRead_ScopedToPrefix()
    {
        var service = new BlobServiceClient(fixture.ConnectionString); // shared-key, Azurite
        var container = service.GetBlobContainerClient("docs");
        await container.CreateIfNotExistsAsync();
        await container.UploadBlobAsync("reports/q1.pdf", new BinaryData("hello"));
        await container.UploadBlobAsync("other/skip.txt", new BinaryData("nope"));

        var connector = new AzureBlobConnector(
            new AzureBlobConnectorConfig { AccountName = "devstoreaccount1", ContainerName = "docs", Prefix = "reports/" },
            service); // internal test ctor

        var files = await connector.ListFilesAsync();
        files.Should().ContainSingle();
        files[0].Path.Should().Be("reports/q1.pdf");
        files[0].ResourceUri.Should().Be("azblob://devstoreaccount1/docs/reports/q1.pdf");

        (await connector.ExistsAsync("reports/q1.pdf")).Should().BeTrue();
        using var stream = await connector.ReadFileAsync("reports/q1.pdf");
        (await new StreamReader(stream).ReadToEndAsync()).Should().Be("hello");
    }
}
```
> The internal ctor requires `[assembly: InternalsVisibleTo("Connapse.Integration.Tests")]` on `Connapse.Storage` — add it to that project's `AssemblyInfo`/csproj if not already present (the S3 tests' access pattern shows whether it is).

- [ ] **Step 4: Add the test collection** in `CloudConnectorTestCollection.cs`:
```csharp
[CollectionDefinition("AzureBlobConnector")]
public class AzureBlobConnectorTestCollection : ICollectionFixture<AzuriteFixture> { }
```

- [ ] **Step 5: Run test, verify it fails then passes**

Run (fails first if run before Task 4 wiring, else drives real behavior): `dotnet test --filter "FullyQualifiedName~AzureBlobConnectorIntegrationTests"`
Expected: PASS (needs Docker).

- [ ] **Step 6: Commit**

```bash
git add tests/Connapse.Integration.Tests/
git commit -m "test(azure): Azurite integration tests for AzureBlobConnector (#477)"
```

---

### Task 9: Connection/Source form UI

**Files:**
- Modify: `src/Connapse.Web/Components/Settings/ConnectionForm.cs`
- Modify: `src/Connapse.Web/Components/Settings/SourceForm.cs`
- Modify: `src/Connapse.Web/Components/Pages/Connections.razor`
- Modify: `src/Connapse.Web/Components/Pages/Sources.razor`
- Test: `tests/Connapse.Core.Tests/Settings/ConnectionFormTests.cs`, `tests/Connapse.Core.Tests/Sources/SourceFormTests.cs`

**Interfaces:**
- Consumes: `ConnectionProvider.AzureBlob`, `AzureBlobConnectionTester`.
- Produces: `ConnectionForm` emits `{"accountName":...,"blobEndpoint"?:...}` for AzureBlob; `SourceForm` emits `{"containerName":...,"prefix"?:...}`.

- [ ] **Step 1: Write the failing tests**

`ConnectionFormTests.cs`:
```csharp
[Fact]
[Trait("Category", "Unit")]
public void ConnectionForm_AzureBlob_BuildsConfigJson()
{
    var form = new ConnectionForm { Provider = ConnectionProvider.AzureBlob, StorageAccountName = "acct" };
    form.Validate().Should().BeNull();               // valid
    form.ToConfigJson().Should().Contain("\"accountName\":\"acct\"");
}

[Fact]
[Trait("Category", "Unit")]
public void ConnectionForm_AzureBlob_MissingAccount_FailsValidation()
{
    var form = new ConnectionForm { Provider = ConnectionProvider.AzureBlob };
    form.Validate().Should().NotBeNull();
}
```
`SourceFormTests.cs`:
```csharp
[Fact]
[Trait("Category", "Unit")]
public void SourceForm_AzureBlob_BuildsScopeJson()
{
    var form = new SourceForm { Container = "docs", Prefix = "reports/" };
    form.ToScopeJson(ConnectionProvider.AzureBlob)
        .Should().Contain("\"containerName\":\"docs\"").And.Contain("\"prefix\":\"reports/\"");
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ConnectionForm_AzureBlob|FullyQualifiedName~SourceForm_AzureBlob"`
Expected: FAIL.

- [ ] **Step 3: Implement the form logic**

`ConnectionForm.cs`:
- Add `public string? StorageAccountName { get; set; }` and `public string? BlobEndpoint { get; set; }`.
- In `IsCloudProvider` (or the cloud-branching property), include `ConnectionProvider.AzureBlob`.
- In `ToConfigJson()`, add the case: for `AzureBlob`, serialize `{ accountName = StorageAccountName, blobEndpoint = string.IsNullOrWhiteSpace(BlobEndpoint) ? null : BlobEndpoint }` (omit null endpoint).
- In `Validate()`, add: `AzureBlob` requires non-blank `StorageAccountName` (else return a message).

`SourceForm.cs`:
- In `ToScopeJson(provider)`, add the `ConnectionProvider.AzureBlob` case building `{ containerName = Container, prefix = Prefix (if non-blank) }` (mirror the S3 case that uses `Container` as bucketName).
- In `Validate()`, require `Container` for `AzureBlob`.

`Connections.razor`:
- Add `@inject AzureBlobConnectionTester AzureBlobTester`.
- Add `<option value="AzureBlob">Azure Blob Storage</option>` to the provider select.
- Add the Azure form branch (Storage Account Name input, optional Blob Endpoint).
- Wire the test button for AzureBlob to call `AzureBlobTester.TestConnectionAsync(new AzureBlobConnectorConfig { AccountName = form.StorageAccountName, BlobEndpoint = form.BlobEndpoint, ContainerName = "$root" })` — test at account level; use a probe container name only if the form has one, else test service-level reachability.
- Add the summary/`DescribeScope` arm for AzureBlob.

`Sources.razor`:
- Add the `ConnectionProvider.AzureBlob` branch rendering the container + prefix fields (mirror S3's bucket + prefix).

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConnectionForm_AzureBlob|FullyQualifiedName~SourceForm_AzureBlob"`
Expected: PASS.

- [ ] **Step 5: Build (Blazor components compile)**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Web/Components/Settings/ConnectionForm.cs src/Connapse.Web/Components/Settings/SourceForm.cs src/Connapse.Web/Components/Pages/Connections.razor src/Connapse.Web/Components/Pages/Sources.razor tests/Connapse.Core.Tests/Settings/ConnectionFormTests.cs tests/Connapse.Core.Tests/Sources/SourceFormTests.cs
git commit -m "feat(azure): connection/source form UI for Azure Blob (#477)"
```

---

### Task 10: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Unit tests**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS (all, including the new Azure unit tests).

- [ ] **Step 3: Integration tests (Docker)**

Run: `dotnet test --filter "Category=Integration"`
Expected: the Azure integration + DI-resolution tests PASS. (Pre-existing Ollama-dependent ingestion tests may fail if no local Ollama — that is the known environmental gap from Phase 1, not introduced here; confirm any failures are only those.)

- [ ] **Step 4: Confirm Azure OpenAI untouched**

Run: `rg -ln "AzureOpenAi|AzureAIFoundry" src/`
Expected: the AI-provider files still present.

- [ ] **Step 5: Push + open PR**

```bash
git push -u origin feature/477-azure-app-identity-connector
gh pr create --repo Destrayon/Connapse --base main \
  --title "feat: Azure Blob connector + ConnapseAzureCredentials (#477)" \
  --body "Closes #477. Part of epic #475. Adds Connapse's Azure identity (ambient managed identity, or a configured service-principal certificate) and a read-only Azure Blob connector, creatable via the Connections/Sources UI. No per-user permission filtering (Phase 4); no guided Providers page or DB-stored credentials (deferred). See docs/superpowers/specs/2026-09-05-azure-phase2-app-identity-connector-design.md."
```
(Push/PR is an outward action — perform in the finishing step after the whole-branch review, with the user's go-ahead.)

---

## Self-Review

**Spec coverage:** §A credential (Tasks 1–2, chain factory + wrapper, cert-or-MI-or-fail-closed, `AzureProviderSettings` from config), §B connector (Tasks 3–4, `ResourceUri.ForAzureBlob`, config, connector with injectable client), §C enums + factory (Tasks 3, 5), §D UI (Task 9, forms only), §E DI/packages/settings (Tasks 1/4/7/8 add the three packages; Task 7 DI + category map), testing (Tasks 1–2 unit chain, 8 Azurite seam, 7 DI-resolution). Non-goals (Providers page, DB creds, federation/secret, per-user perms, live-watch) are respected — no task implements them.

**Placeholder scan:** UI Task 9's razor steps describe exact fields/JSON/validation rather than pasting full markup — that is the one place the plan gives precise instructions over verbatim code, because Blazor markup mirrors the existing S3 branch in the same files; every data shape (`accountName`/`blobEndpoint`/`containerName`/`prefix`) and validation rule is stated explicitly. No "TBD"/"handle edge cases"/"similar to Task N" remain.

**Type consistency:** `AzureProviderSettings` fields, `AzureBlobConnectorConfig { AccountName, ContainerName, Prefix, BlobEndpoint }`, `AzureCredentialChainFactory.Create(settings, certLoader)`, `ConnapseAzureCredentials.ProviderKey = "azure"`, and `ResourceUri.ForAzureBlob(account, container, path)` are used identically across tasks. The connection-config keys (`accountName`/`blobEndpoint`) and source-scope keys (`containerName`/`prefix`) match between the factory (Task 5), the forms (Task 9), and the integration test (Task 8).
