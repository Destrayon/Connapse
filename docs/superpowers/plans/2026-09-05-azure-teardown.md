# Azure Blob + Azure AD Teardown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the existing unverified Azure Blob Storage connector and Azure AD per-user identity/OAuth code so the Azure provider can be rebuilt cleanly, keeping a green build with AWS and Azure OpenAI untouched.

**Architecture:** Removal-only refactor. Delete Azure-Blob/Azure-AD-only files, prune the `AzureBlob`/`Azure` members from three shared enums and every consumer, drop three NuGet packages, and add one down-migration for the `user_cloud_identities` table. Tasks are ordered so the solution compiles and all tests pass at every task boundary — consumers of the shared enums are removed *before* the enum members themselves.

**Tech Stack:** .NET 10, EF Core (two DbContexts), xUnit + FluentAssertions + NSubstitute, Testcontainers, Blazor Server.

**Spec:** `docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md` (see "What we remove first (teardown)").

## Adaptation note (read before starting)

This is a **deletion** refactor, so the standard "write a failing test first" loop does not apply. Each task instead ends with a fixed verification gate:

1. **Build:** `dotnet build` → 0 errors.
2. **Grep-assert:** the task's removal grep returns **no hits in `src/`** (a residual hit means a reference was missed).
3. **Test:** the relevant `dotnet test` filter passes.
4. **Commit.**

Do not write new tests. When a test file *references* removed code, prune the Azure cases from it (Tasks 6–7) — that is part of the deletion, not new coverage.

## Global Constraints

- **Never touch Azure OpenAI / AI Foundry.** `AzureOpenAiLlmProvider`, `AzureOpenAiEmbeddingProvider`, `AzureOpenAiConnectionTester`, `AzureOpenAiLlmConnectionTester`, `AzureAIFoundryConnectionTester`, `AzureAIFoundryCrossEncoderProvider`, the `AzureEndpoint`/`AzureApiKey`/`AzureDeploymentName` settings, and the `"AzureOpenAI"`/`"AzureAIFoundry"` DI + `SettingsEndpoints` arms **stay**. They share the word "Azure" but use `Azure.AI.OpenAI` (API-key auth), not `Azure.Storage.Blobs`/`Azure.Identity`.
- **Never remove** the `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` packages — they back JWT + SAML (`JwtTokenService`, `SamlServiceProvider`), which stay.
- **Never touch** the AWS per-user identity path: `UserAwsIdentityLinkEntity`, `AwsIdentityLinkStore`, `AwsSearchScopeResolver`, `ConnapseAwsCredentials`, the SAML sign-in code, and the three `AddUserAwsIdentityLinks*` migrations.
- **Enum numeric values stay stable** for the members that remain — `ConnectorType`/`ConnectionProvider` values were chosen to match for cast-based backfill. Remove the `AzureBlob = 4` line entirely; do **not** renumber the others. Existing numeric gaps are the codebase's own style.
- Two EF migration histories: `KnowledgeDbContext` (Storage) and `ConnapseIdentityDbContext` (Identity). The `user_cloud_identities` table belongs to **Identity**.
- Build command: `dotnet build`. Test commands: `dotnet test --filter "Category=Unit"` (no Docker) and `dotnet test --filter "Category=Integration"` (Docker).

---

### Task 1: Remove the Azure AD per-user identity / OAuth surface

Removes the "connect my Azure identity" OAuth flow and the `azuread` admin settings. This unit is atomic because deleting `AzureAdSettings` breaks every consumer at once — they must all go together for the build to stay green.

**Files:**
- Delete: `src/Connapse.Core/Models/AzureAdSettings.cs`
- Delete: `src/Connapse.Web/Components/Settings/AzureAdSettingsTab.razor`
- Delete: `src/Connapse.Storage/ConnectionTesters/AzureAdConnectionTester.cs`
- Modify: `src/Connapse.Identity/Services/CloudIdentityService.cs` — remove `GetAzureConnectUrl`, `HandleAzureCallbackAsync`, `IsAzureAdConfigured`, and the `IOptionsMonitor<AzureAdSettings>` constructor injection
- Modify: `src/Connapse.Identity/Services/ICloudIdentityService.cs` — remove the three Azure method signatures
- Modify: `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs` — remove `/azure/connect`, `/azure/callback`, the two Azure cookie consts, `azureAdConfigured` in the identities response, and the `CloudProvider.Azure`/`ConnectionProvider.AzureBlob` arms in the delete-scope route (keep all AWS/SAML routes; update the "Valid values: AWS, Azure" message to `AWS`)
- Modify: `src/Connapse.Web/Endpoints/SettingsEndpoints.cs` — remove the `"azuread"` GET/POST/test arms only (leave `"AzureOpenAI"`/`"AzureAIFoundry"`)
- Modify: `src/Connapse.Web/Services/ProviderSetupReader.cs` — remove the `"azure"` `ProviderSetup`, the `AzureAdSettings` injection, and the Azure `IsConfigured`/`SignIn`/`AzureAccess` helpers
- Modify: `src/Connapse.Web/Components/Pages/Providers.razor` — remove the `IOptionsMonitor<AzureAdSettings>` inject, the Azure AD tab render, the `azureAdSettings` field, and `SaveAzureAdSettings`
- Modify: `src/Connapse.Web/Components/Pages/ProfileIntegrations.razor` — remove the Azure card, its `azureAdConfigured`/`azureIdentity` state, and `ConnectAzureAsync` (keep the AWS card)
- Modify: `src/Connapse.Storage/Settings/DatabaseSettingsProvider.cs` — remove the `["azuread"] = "Identity:AzureAd"` `CategoryPrefixMap` entry
- Modify: `src/Connapse.Identity/IdentityServiceExtensions.cs` — remove `services.Configure<AzureAdSettings>(...)`
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — remove the `AzureAdConnectionTester` registration (keep AzureOpenAI/AI-Foundry testers)
- Modify: `src/Connapse.Web/Program.cs` — remove any `AzureAd` wiring surfaced by grep
- Modify: `src/Connapse.Web/appsettings.json` — remove the `Identity:AzureAd` block
- Modify: `.env.example` — remove the `Identity__AzureAd__*` block (keep `Knowledge__Embedding__Azure__*` / `Knowledge__Llm__AzureDeploymentName`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `AzureAdSettings` no longer exists; `ICloudIdentityService` no longer exposes Azure methods. `CloudIdentityService` still exists (generic parts remain; final fate decided in Task 3).

- [ ] **Step 1: Locate every reference**

Run: `rg -n "AzureAd|azuread" src/ --glob '!**/*AzureOpenAi*' --glob '!**/*AzureAIFoundry*'`
This is the authoritative worklist for this task. Every hit outside the AzureOpenAI/AIFoundry files must be removed or the file deleted.

- [ ] **Step 2: Delete the three Azure-AD-only files**

```bash
git rm src/Connapse.Core/Models/AzureAdSettings.cs \
       src/Connapse.Web/Components/Settings/AzureAdSettingsTab.razor \
       src/Connapse.Storage/ConnectionTesters/AzureAdConnectionTester.cs
```

- [ ] **Step 3: Prune every consumer listed under Files**

Edit each modified file to remove the Azure-AD members named above. Work top-down through the Step 1 grep output. Leave the `CloudProvider.Azure` enum value itself in place for now (Task 4) — but remove the *branches* that use it here.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: 0 errors. (A "type or namespace `AzureAdSettings` not found" error means a consumer was missed — return to Step 3.)

- [ ] **Step 5: Grep-assert**

Run: `rg -n "AzureAd|azuread" src/ --glob '!**/*AzureOpenAi*' --glob '!**/*AzureAIFoundry*'`
Expected: **no hits.** (Comment-only mentions count — remove them too.)

- [ ] **Step 6: Unit tests**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS. (Mixed test files that reference removed Azure-AD code are pruned in Task 7; if a Unit test fails to compile now because of that, note it and continue — Task 7 fixes it. If you prefer a green gate here, prune those specific Azure-AD test cases now.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: remove Azure AD per-user identity/OAuth surface (#476)"
```

---

### Task 2: Remove the Azure Blob data-plane connector

Removes the blob connector, its config, tester, the `AzureIdentityProvider` (which consumes `AzureBlobConnectorConfig`), and every UI/factory branch. Leaves the `ConnectorType.AzureBlob` / `ConnectionProvider.AzureBlob` enum *values* defined (Task 4 removes them) so this stays green.

**Files:**
- Delete: `src/Connapse.Storage/Connectors/AzureBlobConnector.cs`
- Delete: `src/Connapse.Storage/Connectors/AzureBlobConnectorConfig.cs`
- Delete: `src/Connapse.Storage/ConnectionTesters/AzureBlobConnectionTester.cs`
- Delete: `src/Connapse.Storage/CloudScope/AzureIdentityProvider.cs`
- Modify: `src/Connapse.Storage/Connectors/ConnectorFactory.cs` — remove the `ConnectionProvider.AzureBlob =>` build arm (keep S3/Filesystem/Sftp)
- Modify: `src/Connapse.Core/Utilities/ResourceUri.cs` — remove `ForAzureBlob`
- Modify: `src/Connapse.Web/Components/Settings/ConnectionForm.cs` — remove `StorageAccountName`/`ManagedIdentityClientId` fields, the `AzureBlob` `IsCloudProvider` OR-arm, the `AzureBlob` build case, and its validation
- Modify: `src/Connapse.Web/Components/Settings/SourceForm.cs` — remove the `AzureBlob` case + validation + comment
- Modify: `src/Connapse.Web/Components/Pages/Connections.razor` — remove the `@inject AzureBlobConnectionTester`, the provider `<option>`, the form branch, the test call, the summary arm, and Azure copy text
- Modify: `src/Connapse.Web/Components/Pages/Sources.razor` — remove the `ConnectionProvider.AzureBlob` OR-arm and comment
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — remove the `AzureBlobConnectionTester` and `ICloudIdentityProvider → AzureIdentityProvider` registrations

**Interfaces:**
- Consumes: enums still define `ConnectorType.AzureBlob`/`ConnectionProvider.AzureBlob` (removed in Task 4).
- Produces: no `IConnector`/`IConnectionTester`/`ICloudIdentityProvider` implementation for Azure remains; `ResourceUri.ForAzureBlob` gone.

- [ ] **Step 1: Locate references**

Run: `rg -n "AzureBlobConnector|AzureBlobConnectionTester|AzureIdentityProvider|ForAzureBlob|StorageAccountName|ManagedIdentityClientId|DefaultAzureCredential" src/`

- [ ] **Step 2: Delete the four data-plane files**

```bash
git rm src/Connapse.Storage/Connectors/AzureBlobConnector.cs \
       src/Connapse.Storage/Connectors/AzureBlobConnectorConfig.cs \
       src/Connapse.Storage/ConnectionTesters/AzureBlobConnectionTester.cs \
       src/Connapse.Storage/CloudScope/AzureIdentityProvider.cs
```

- [ ] **Step 3: Prune the factory, ResourceUri, forms, pages, and DI** per the Files list, using the Step 1 grep as the worklist. Keep the `AzureBlob` enum values defined for now.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 5: Grep-assert**

Run: `rg -n "AzureBlobConnector|AzureBlobConnectionTester|AzureIdentityProvider|ForAzureBlob|DefaultAzureCredential" src/`
Expected: **no hits.**

- [ ] **Step 6: Unit tests**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS (defer mixed-test-file failures to Task 7 as in Task 1, or prune the specific cases now).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: remove Azure Blob data-plane connector (#476)"
```

---

### Task 3: Remove the CloudIdentity / CloudScope scaffolding + migration

The generic-but-Azure-only scaffolding is now dead (Task 1 removed the Azure writer, Task 2 removed the only `ICloudIdentityProvider`). Remove the dead types and drop the `user_cloud_identities` table. **Verify-then-delete:** each type below is deleted only after grep confirms no non-test caller remains; anything still referenced by an AWS/generic path is kept and only its Azure members pruned.

**Files:**
- Modify: `src/Connapse.Core/Models/AuthModels.cs` — delete the `AzureConnectResult` record; remove the Azure fields (`ObjectId`, `TenantId`) from `CloudIdentityData`; if `CloudIdentityData`/`CloudIdentityDto` have no remaining non-test callers after Task 1–2, delete them
- Modify: `src/Connapse.Identity/Services/CloudIdentityService.cs` + `ICloudIdentityService.cs` — if only the generic `Get/List/Disconnect/StoreIdentity` members remain and nothing calls them, delete both files; otherwise leave the still-called members
- Delete (if uncalled): `src/Connapse.Identity/Data/Entities/UserCloudIdentityEntity.cs`, `src/Connapse.Identity/Stores/ICloudIdentityStore.cs`, `src/Connapse.Identity/Stores/PostgresCloudIdentityStore.cs`, `src/Connapse.Core/Interfaces/ICloudIdentityProvider.cs`, `src/Connapse.Web/Services/CloudScopeService.cs`, `src/Connapse.Core/Interfaces/ICloudScopeService.cs`, `src/Connapse.Core/Models/CloudScopeModels.cs`
- Modify: `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs` — remove the `UserCloudIdentityEntity` `DbSet` + mapping
- Modify: `src/Connapse.Identity/Data/Entities/ConnapseUser.cs` — remove the `UserCloudIdentityEntity` navigation collection
- Modify: `src/Connapse.Identity/IdentityServiceExtensions.cs` — remove the `ICloudIdentityStore`/`ICloudIdentityService` registrations if those types are deleted
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — remove any `ICloudScopeService`/`IConnectorScopeCache`/`ICloudIdentityProvider` registrations left dangling
- Create: a new Identity migration dropping `user_cloud_identities`

**Interfaces:**
- Consumes: the Azure OAuth methods and `AzureIdentityProvider` are already gone (Tasks 1–2).
- Produces: the `user_cloud_identities` table is dropped; no `CloudIdentity*`/`CloudScope*` Azure scaffolding remains.

- [ ] **Step 1: Confirm each type is dead**

For each candidate type, run e.g. `rg -n "CloudScopeService|ICloudScopeService|UserCloudIdentityEntity|ICloudIdentityProvider|ICloudIdentityStore" src/`. A type with **zero non-test, non-DI hits** is safe to delete. A type still referenced by an AWS/generic caller is kept — prune only its Azure members. Record which you delete vs keep.

- [ ] **Step 2: Delete the confirmed-dead types and prune `AuthModels`, the DbContext, and `ConnapseUser`** per the Files list and the Step 1 findings.

- [ ] **Step 3: Add the down-migration**

Run: `dotnet ef migrations add DropUserCloudIdentities --project src/Connapse.Identity`
Then open the generated migration and confirm `Up()` drops `user_cloud_identities` and the model snapshot no longer contains `UserCloudIdentityEntity`. (Migrations run automatically on startup.)

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: 0 errors.

- [ ] **Step 5: Grep-assert**

Run: `rg -n "UserCloudIdentityEntity|CloudScopeService|ICloudIdentityProvider" src/`
Expected: **no hits** (or only the intentionally-kept generic type you documented in Step 1).

- [ ] **Step 6: Migration + unit tests**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS. (Integration migration verification happens in Task 7.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: remove dead CloudIdentity/CloudScope scaffolding + drop user_cloud_identities (#476)"
```

---

### Task 4: Prune the shared enum members

Now that no consumer references them, remove the `AzureBlob`/`Azure` enum values. Do this in one task so the build proves all references are gone.

**Files:**
- Modify: `src/Connapse.Core/Models/StorageModels.cs` — remove `AzureBlob = 4` from `ConnectorType`
- Modify: `src/Connapse.Core/Models/ConnectionModels.cs` — remove `AzureBlob = 4` from `ConnectionProvider`
- Modify: `src/Connapse.Core/Models/CloudProvider.cs` — remove `Azure = 1` from `CloudProvider` (keep the enum and `AWS`)

**Interfaces:**
- Consumes: all consumers removed in Tasks 1–3.
- Produces: the three enums no longer define an Azure member.

- [ ] **Step 1: Remove the three enum lines** listed above. Do not renumber the surviving members.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: 0 errors. (Any `CS0117 'ConnectorType' does not contain a definition for 'AzureBlob'` points to a missed consumer — fix it in place.)

- [ ] **Step 3: Grep-assert**

Run: `rg -n "AzureBlob|CloudProvider\.Azure" src/`
Expected: **no hits.**

- [ ] **Step 4: Unit tests**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: remove AzureBlob/Azure members from shared enums (#476)"
```

---

### Task 5: Remove NuGet packages and Azurite test fixtures

**Files:**
- Modify: `src/Connapse.Storage/Connapse.Storage.csproj` — remove `<PackageReference Include="Azure.Storage.Blobs" .../>` and `<PackageReference Include="Azure.Identity" .../>` (keep `Azure.AI.OpenAI` and all `AWSSDK.*`)
- Modify: `tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj` — remove `<PackageReference Include="Testcontainers.Azurite" .../>`
- Delete: `tests/Connapse.Integration.Tests/AzuriteFixture.cs`
- Delete: `tests/Connapse.Integration.Tests/AzureBlobConnectorIntegrationTests.cs`
- Delete: `tests/Connapse.Integration.Tests/TestableAzureBlobConnector.cs`
- Modify: `tests/Connapse.Integration.Tests/CloudConnectorTestCollection.cs` — remove the `AzureBlobConnectorTestCollection` (keep `S3ConnectorTestCollection`)
- Modify: `tests/Connapse.Integration.Tests/AssemblyInfo.cs` — update the comment prose referencing `AzuriteFixture`

**Interfaces:**
- Consumes: no Azure blob code remains (Tasks 1–4).
- Produces: `Azure.Storage.Blobs`, `Azure.Identity`, `Testcontainers.Azurite` no longer referenced anywhere.

- [ ] **Step 1: Delete the Azure integration-test files**

```bash
git rm tests/Connapse.Integration.Tests/AzuriteFixture.cs \
       tests/Connapse.Integration.Tests/AzureBlobConnectorIntegrationTests.cs \
       tests/Connapse.Integration.Tests/TestableAzureBlobConnector.cs
```

- [ ] **Step 2: Remove the package references and the Azure test collection** per the Files list.

- [ ] **Step 3: Confirm the packages are unreferenced**

Run: `rg -n "Azure\.Storage\.Blobs|Azure\.Identity|Testcontainers\.Azurite|Azurite" src/ tests/`
Expected: **no hits.** (Confirm no other project transitively required them.)

- [ ] **Step 4: Restore + build**

Run: `dotnet build`
Expected: 0 errors, no `NU1101`/missing-package warnings for the removed packages.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: drop Azure.Storage.Blobs/Azure.Identity/Testcontainers.Azurite (#476)"
```

---

### Task 6: Prune the mixed test files

These files interleave Azure cases with S3/generic cases. Remove only the Azure arms; keep the rest.

**Files (prune Azure cases; delete whole file where it is Azure-only):**
- Delete: `tests/Connapse.Core.Tests/CloudScope/AzureIdentityProviderTests.cs`
- Modify: `tests/Connapse.Core.Tests/CloudScope/CloudScopeServiceTests.cs` — delete the file if `CloudScopeService` was removed in Task 3; otherwise prune Azure cases
- Modify: `tests/Connapse.Identity.Tests/CloudIdentityServiceTests.cs` — remove the Azure OAuth-flow tests (delete the file if the service was removed)
- Modify: `tests/Connapse.Web.Tests/Components/CloudIdentityClaimsTests.cs` — prune Azure cases (delete if Azure-only)
- Modify: `tests/Connapse.Integration.Tests/CloudIdentityEndpointTests.cs` — remove `AzureConnect_NotConfigured_Returns400` and the `AzureAdConfigured` DTO field assertions
- Modify: `tests/Connapse.Integration.Tests/NewSettingsCategoriesIntegrationTests.cs` — remove `azuread` category cases
- Modify: `tests/Connapse.Core.Tests/Utilities/ResourceUriTests.cs` — remove the `ForAzureBlob`/`azblob` tests
- Modify: `tests/Connapse.Web.Tests/Services/ProviderSetupReaderTests.cs` — remove the Azure `ProviderSetup` cases
- Modify: `tests/Connapse.Web.Tests/Components/ProvidersPageTests.cs` — remove Azure AD tab cases
- Modify: `tests/Connapse.Core.Tests/Settings/ConnectionFormTests.cs`, `tests/Connapse.Core.Tests/Sources/SourceFormTests.cs`, `tests/Connapse.Core.Tests/Sources/SourceModelTests.cs`, `tests/Connapse.Core.Tests/Sources/SourceScopePreflightTests.cs`, `tests/Connapse.Core.Tests/Connectors/SourceConnectorFactoryTests.cs`, `tests/Connapse.Core.Tests/Connectors/ConnectorConfigTests.cs`, `tests/Connapse.Core.Tests/Connectors/ConnectorCapabilityTests.cs`, `tests/Connapse.Core.Tests/ConnectionTesterTests.cs` — remove the `AzureBlob`/`azblob` cases
- Modify: `tests/Connapse.Integration.Tests/ContainerCreationTests.cs` — remove Azure cases if present

**Interfaces:**
- Consumes: all Azure production code removed (Tasks 1–5).
- Produces: no test references Azure Blob / Azure AD.

- [ ] **Step 1: Locate every test reference**

Run: `rg -n "AzureBlob|AzureAd|azuread|azblob|CloudProvider\.Azure|Azurite" tests/`
This is the worklist. Delete Azure-only files; prune Azure cases from mixed files.

- [ ] **Step 2: Prune / delete** per the Files list and Step 1 output.

- [ ] **Step 3: Grep-assert**

Run: `rg -n "AzureBlob|AzureAd|azuread|azblob|CloudProvider\.Azure|Azurite" tests/`
Expected: **no hits.**

- [ ] **Step 4: Unit tests**

Run: `dotnet test --filter "Category=Unit"`
Expected: PASS, 0 skipped-for-compile.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "test: remove Azure Blob/AD test cases (#476)"
```

---

### Task 7: Final sweep — docs, docker-compose, full verification

**Files:**
- Delete: `docs/azure-identity-setup.md`
- Modify (if present): `docker-compose.dev.yml` / `docker-compose.yml` — remove any Azurite service
- Modify (optional, comment-only): `src/Connapse.Core/Utilities/StorageLocationPolicy.cs`, `src/Connapse.Web/Mcp/McpTools.cs`, `src/Connapse.Storage/Containers/PostgresContainerStore.cs`, `src/Connapse.Storage/Documents/DocumentCoordinateReport.cs`, `src/Connapse.Core/Interfaces/ISyncCursorConnector.cs`, `src/Connapse.Core/Interfaces/IManagedStorageProvider.cs` — remove stray "AzureBlob" prose mentions

**Interfaces:**
- Consumes: everything above.
- Produces: a fully green build and test suite with no Azure Blob / Azure AD residue.

- [ ] **Step 1: Delete the setup doc and any Azurite compose service**

```bash
git rm docs/azure-identity-setup.md
rg -n "azurite" docker-compose*.yml
```
Remove any matched service block.

- [ ] **Step 2: Full residue sweep (whole repo, excluding Azure OpenAI)**

Run: `rg -n "AzureBlob|AzureAd|azuread|azblob|Azurite|ForAzureBlob|CloudProvider\.Azure|Azure\.Storage\.Blobs" . --glob '!**/*AzureOpenAi*' --glob '!**/*AzureAIFoundry*' --glob '!docs/superpowers/**'`
Expected: **no hits** (design-spec/plan mentions under `docs/superpowers/` are intentionally excluded). Remove any straggler comments.

- [ ] **Step 3: Confirm Azure OpenAI survived**

Run: `rg -ln "AzureOpenAi|AzureAIFoundry" src/`
Expected: the AI-provider files still present (sanity check that the teardown did not over-reach).

- [ ] **Step 4: Full build + unit tests**

Run: `dotnet build && dotnet test --filter "Category=Unit"`
Expected: 0 build errors, all unit tests PASS.

- [ ] **Step 5: Integration tests (Docker) — verifies the migration + a clean container start**

Run: `dotnet test --filter "Category=Integration"`
Expected: PASS. This exercises the `DropUserCloudIdentities` migration against a real PostgreSQL container. (A DI-registration regression only shows up here — see the project memory "DI changes need integration tests.")

- [ ] **Step 6: Commit + open PR**

```bash
git add -A
git commit -m "refactor: final Azure Blob/AD teardown sweep + docs (#476)"
git push -u origin refactor/476-azure-teardown
gh pr create --repo Destrayon/Connapse --base main \
  --title "refactor: tear down existing Azure Blob + Azure AD code (#476)" \
  --body "Closes #476. Part of epic #475. Removal-only teardown of the unverified Azure Blob connector and Azure AD per-user identity/OAuth code; AWS and Azure OpenAI untouched. See docs/superpowers/specs/2026-09-05-azure-blob-provider-design.md."
```

---

## Self-Review

**Spec coverage** — the spec's teardown manifest maps to tasks as: delete-list files → Tasks 1, 2, 5, 7; shared-enum prune → Task 4; CloudIdentity scaffolding + migration → Task 3; NuGet removal → Task 5; mixed test prune → Task 6; "don't touch Azure OpenAI / JWT" → Global Constraints + Task 7 Step 3. No teardown item is unassigned.

**Placeholder scan** — the "verify-then-delete" judgment in Task 3 is deliberate, not a placeholder: the spec flags the CloudIdentity scaffolding as generic-shaped-but-Azure-only, so the plan gives an explicit grep gate to decide delete-vs-keep per type rather than guessing a delete-list that could break an AWS caller.

**Type consistency** — enum member names (`ConnectorType.AzureBlob`, `ConnectionProvider.AzureBlob`, `CloudProvider.Azure`) and file paths are used identically across tasks and match the Grep-verified live tree. The migration name `DropUserCloudIdentities` is used consistently in Task 3.
