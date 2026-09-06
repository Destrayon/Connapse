# Azure Phase 2 — ConnapseAzureCredentials + Azure Blob connector

**Status:** Design — approved in brainstorming, pending spec review
**Date:** 2026-09-05
**Milestone:** v0.4.0 · Issue #477 · Epic #475
**Parent design:** `docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md` (§A credentials, §E data plane). This phase spec refines that section with the phase-specific decisions; where they differ, this document governs Phase 2.

## Goal

Deliver Azure Blob ingestion end-to-end: Connapse's own Azure identity plus a rebuilt read-only Azure Blob data-plane connector, creatable through the existing Connections/Sources UI. **No per-user permission filtering** (that is Phase 4). Independently shippable — a green increment that ingests Azure blobs.

## Scope decisions (from brainstorming)

1. **One credential story, two runtime contexts** — not a chooser among kinds. Ambient **managed identity** in Azure; a configured service-principal **certificate** off-Azure. No client-secret, no workload-identity-federation, no UI toggle among kinds.
2. **Credential config comes from the settings hierarchy**, not `ProviderCredentialEntity`. DataProtection-encrypted DB storage of the cert (written by a guided Providers page) is a **deferred** follow-up. Reading a cert by path/config keeps this phase decoupled from that UI.
3. **UI is connection/source forms only.** The guided Azure Providers setup page (credential status cards) is deferred.

## Components

### A. `ConnapseAzureCredentials` (Connapse.Storage/CloudScope)

Mirror of [`ConnapseAwsCredentials`](../../../src/Connapse.Storage/CloudScope/ConnapseAwsCredentials.cs); `ProviderKey = "azure"`. Exposes an `Azure.Core.TokenCredential` consumed by every Azure SDK client. Built as an explicit `ChainedTokenCredential` (never `DefaultAzureCredential` — deterministic, no silent fall-through):

1. **Configured certificate** — when `AzureProviderSettings` supplies `TenantId` + `ClientId` + a certificate: `new ClientCertificateCredential(tenantId, clientId, cert, new ClientCertificateCredentialOptions { SendCertificateChain = true })`.
2. **Ambient managed identity** — `new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(id))` when `UserAssignedManagedIdentityClientId` is set, else `new ManagedIdentityCredential()`.
3. **Fail closed** — if neither branch can produce a credential object, the chain has no usable entry; a `GetToken` failure propagates and callers deny. No developer-tool credentials in the chain.

Registered **singleton**. Reads `IOptionsMonitor<AzureProviderSettings>` at token time so config reload (settings table) takes effect without restart. The Azure SDK honors `AccessToken.RefreshOn` for SDK-client calls, so **no manual refresh timer** (unlike the AWS design). Uses `Task.Run` where a synchronous credential path could deadlock the Blazor sync context, matching the AWS credential's precaution.

**`AzureProviderSettings`** (`Connapse.Core/Models`), bound from configuration only:
- `TenantId : string?`
- `ClientId : string?`
- `ClientCertificatePath : string?` (PEM or PFX path; the private key stays on disk under file permissions — the `AZURE_CLIENT_CERTIFICATE_PATH` pattern)
- `ClientCertificatePassword : string?` (for PFX; optional)
- `UserAssignedManagedIdentityClientId : string?`
- `SectionName = "Providers:Azure"`; a `DatabaseSettingsProvider.CategoryPrefixMap` entry maps DB category `azure` → `Providers:Azure`.

Cert loading is a small helper: PEM (`X509Certificate2.CreateFromPemFile`) or PFX (`new X509Certificate2(path, password)`), selected by extension/content. A missing/invalid cert file when `ClientId` is set is a **configuration error surfaced at credential build**, not a silent fall-through to ambient (that would mask a misconfiguration).

### B. Data-plane connector (Connapse.Storage/Connectors)

- **`AzureBlobConnectorConfig`** record: `{ AccountName, ContainerName, Prefix?, BlobEndpoint? }`. `BlobEndpoint` overrides the default `https://{account}.blob.core.windows.net` (Azurite/local).
- **`AzureBlobConnector : IConnector, IDisposable`** — ctor `(AzureBlobConnectorConfig config, TokenCredential credential)`. Builds one `BlobServiceClient` (per instance) from `BlobEndpoint ?? https://{account}.blob.core.windows.net` + credential; `GetBlobContainerClient(ContainerName)`. `Type => ConnectorType.AzureBlob`; `SupportsLiveWatch => false`.
  - `ListFilesAsync(prefix)` → `container.GetBlobsAsync(prefix: Combine(Prefix, prefix))`, paged, mapping each to `ConnectorFile` with `ResourceUri.ForAzureBlob(AccountName, ContainerName, blobName)`.
  - `ReadFileAsync(path)` → `GetBlobClient(path).DownloadStreamingAsync()`.
  - `ExistsAsync(path)` → `GetBlobClient(path).ExistsAsync()`.
  - `ResolveJobPath(relative)` → prefix-joined blob key.
  - `WatchAsync` → not supported (mirrors S3).
- **`ResourceUri.ForAzureBlob(account, container, path)`** → `azblob://{account}/{container}/{path}` (re-added; mirror `ForS3`).
- **`AzureBlobConnectionTester : IConnectionTester`** — `TestConnectionAsync` lists up to ~5 blobs via the credential; rich per-status error messages (auth failure, container missing, account not found), mirroring `S3ConnectionTester`.

### C. Enum re-add + factory recombination

- `ConnectorType.AzureBlob = 4`, `ConnectionProvider.AzureBlob = 4`, `CloudProvider.Azure = 1` (values match, per the enum comment's cast-backfill convention).
- `ConnectorFactory` gains the `ConnectionProvider.AzureBlob` arm: read `accountName`/`blobEndpoint` from the **connection** config, `containerName`/`prefix` from the **source** scope, run `RequirePermittedLocation` (same as S3), pass the singleton `ConnapseAzureCredentials`' `TokenCredential`. Throws if `source.ConnectionId != connection.Id` (existing invariant).

**JSON shapes:**
- Connection config: `{ "accountName": "...", "blobEndpoint"?: "..." }`
- Source scope: `{ "containerName": "...", "prefix"?: "..." }`

### D. UI (forms only)

- `ConnectionForm` — add `StorageAccountName` (and optional `BlobEndpoint`) fields; add `AzureBlob` to the `IsCloudProvider` arm, the build case (`ToConfigJson` → `{accountName, blobEndpoint?}`), and validation (account name required, DNS-name shape).
- `SourceForm` — add the `AzureBlob` case to `ToScopeJson` (`{containerName, prefix?}`) and validation (container required).
- `Connections.razor` — provider `<option>`, the Azure form branch, the `AzureBlobConnectionTester` test call, and the summary/`DescribeScope` arm.
- `Sources.razor` — the `ConnectionProvider.AzureBlob` branch for the container/prefix fields.

### E. DI, packages, settings

- Re-add NuGet to `Connapse.Storage.csproj`: `Azure.Storage.Blobs`, `Azure.Identity`.
- `Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`: register `ConnapseAzureCredentials` (singleton), `AzureBlobConnectionTester` (scoped), and `Configure<AzureProviderSettings>` from the `"Providers:Azure"` section; `ConnectorFactory` picks up the new arm.
- `DatabaseSettingsProvider.CategoryPrefixMap`: add `["azure"] = "Providers:Azure"`.

## Testing

- **Unit — `ConnapseAzureCredentialsTests`** (no Docker): cert-configured → a `ClientCertificateCredential` is composed; no cert but MI configured → `ManagedIdentityCredential` (system vs user-assigned by settings); neither → fail-closed (a token request throws / yields no token); an invalid cert path with `ClientId` set → surfaces a configuration error rather than silently using ambient. Assert on the composed chain / a seam that exposes which branch was chosen (extract a small pure `AzureCredentialChainFactory` so the composition is unit-testable without real Azure).
- **Unit** — `ResourceUri.ForAzureBlob` round-trips (clone the S3 cases); `ConnectorFactory` builds the Azure arm from split config/scope; `ConnectionForm`/`SourceForm` Azure build+validation cases.
- **Integration — `AzureBlobConnectorIntegrationTests`** (Testcontainers **Azurite**): re-add `Testcontainers.Azurite` + an `AzuriteFixture`. **Azurite cannot authenticate an AAD `TokenCredential`**, so the test drives the connector's list/read/`ResourceUri` logic through a blob client built with Azurite's **shared-key/connection-string** (a test-only construction path — the `TestableAzureBlobConnector` pattern that Phase 1 deleted). This proves blob enumeration, prefix scoping, streaming reads, and URI minting; the `TokenCredential` wiring is covered by the unit tests above, not against Azurite.
- Add the `AzureBlobConnectorTestCollection` back to `CloudConnectorTestCollection.cs`.

## Testable seams (design-for-isolation)

- `AzureCredentialChainFactory` (pure): `(AzureProviderSettings, certLoader) → TokenCredential` — the branch logic, unit-testable without Azure. `ConnapseAzureCredentials` wraps it + `IOptionsMonitor`.
- `AzureBlobConnector` takes an already-built `BlobServiceClient` via an internal ctor (or a client-factory delegate) so the integration test can inject the Azurite shared-key client while production uses the account+credential path.

## Non-goals (deferred)

- Guided Azure Providers setup page + DataProtection-encrypted `ProviderCredentialEntity` storage of the cert.
- Workload-identity-federation and client-secret credential kinds.
- Per-user permission resolution / search filtering (Phase 4, #479).
- Live-watch for Azure Blob.
