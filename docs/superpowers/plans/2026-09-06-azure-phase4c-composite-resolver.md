# Azure Phase 4c — flat resolver + composite + per-cloud enforcement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire Azure into search: an `AzureSearchScopeResolver` that turns a searcher into `azblob://` prefixes (flat accounts), a `CompositeSearchScopeResolver` that unions AWS + Azure per-cloud/per-scheme (fail-closed per cloud), and a generalized enforcement latch so Azure AD configuration also enables filtering — leaving the shared SQL filter and the AWS path behavior-identical.

**Architecture:** The existing SQL filter (`resource_uri IS NULL OR <OR of matches>`) is provider-agnostic and non-cloud docs have `resource_uri = NULL`, so per-cloud/per-scheme enforcement is expressed **by construction** in a composite `SearchScopes`: each cloud contributes its prefixes when enforcing-and-granted, a scheme wildcard (`s3://` / `azblob://`) when that cloud is not enforcing, and nothing when it denies/fails — the SQL layer is untouched. Enforcement stays a single global opt-in latch, broadened to latch on SAML **or** Azure AD; each cloud's resolver gates on its own provider's config via an additive `StateFor(bool)` overload so the AWS resolver, its `StateFor` call, and its tests are unchanged.

**Tech Stack:** .NET 10, `IMemoryCache`, `IOptionsMonitor<T>`, xUnit + FluentAssertions + NSubstitute, Testcontainers (integration).

**Spec:** `docs/superpowers/specs/2026-09-06-azure-phase4-permission-engine-design.md` (§C, §D).

## Global Constraints

- **AWS behavior is unchanged.** `AwsSearchScopeResolver`, its `StateFor(SamlSignInSettings, bool)` call, and `SamlEnforcementStateTests` are not modified; the enforcement generalization is an *additive* `StateFor(bool, bool)` overload plus a broadened latch. The AWS resolver becomes one inner resolver of the composite with its logic intact.
- **The shared SQL filter is not touched.** `PgVectorStore`/`KeywordSearchService` stay as-is; correctness comes entirely from the `SearchScopes` the composite produces. Non-cloud docs (`resource_uri = NULL`) remain always-visible via the existing `resource_uri IS NULL` fallback.
- **Fail closed, per cloud.** Any error/deny/no-principal from one cloud contributes no matches for that cloud's scheme (its docs hidden), never a global failure that hides the other cloud or non-cloud docs. A cloud not enforcing contributes its scheme wildcard (its docs visible). Global `Unrestricted` only when **both** clouds are unrestricted.
- **No over-grant of Azure content.** A deployment in enforcing mode with Azure AD unconfigured denies `azblob://` docs (never shows them unfiltered).
- **Flat only.** This phase resolves flat (non-HNS) accounts via RBAC prefixes. Tag-conditioned RBAC residue and Gen2 ACLs are **not** admitted here (they need 4e's live verifier) — omitting them is a temporary under-grant on the epic branch that 4e closes before the epic reaches main.
- **.NET 10 conventions:** file-scoped namespaces, records, primary constructors, async all the way, no `var` for primitives, no `dynamic`.
- Azure resolver caches per-user (`azure-scopes:{userId}`, ~60 s, confident answers only), mirroring `AwsSearchScopeResolver`.

## File Structure

- Modify `src/Connapse.Core/Models/PermissionEnforcementSettings.cs` — add `StateFor(bool providerConfigured, bool determined = true)`; keep the `SamlSignInSettings` overload delegating to it.
- Create `src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs` — the flat Azure resolver.
- Create `src/Connapse.Storage/CloudScope/CompositeSearchScopeResolver.cs` — AWS ∪ Azure, per-cloud/per-scheme.
- Rename/broaden `src/Connapse.Web/Services/SamlEnforcementLatch.cs` → `CloudEnforcementLatch.cs` — latch on SAML **or** Azure AD.
- Modify `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register Aws + Azure resolvers as concrete types and the composite as `ISearchScopeResolver`.
- Modify the `CloudEnforcementLatch` registration in `src/Connapse.Web` (wherever `SamlEnforcementLatch` is registered as an `IHostedService`).
- Tests: `tests/Connapse.Core.Tests/Models/PermissionEnforcementStateTests.cs` (add bool-overload cases — new file or extend), `tests/Connapse.Storage.Tests/CloudScope/AzureSearchScopeResolverTests.cs`, `tests/Connapse.Storage.Tests/CloudScope/CompositeSearchScopeResolverTests.cs`, `tests/Connapse.Integration.Tests/AzureFlatEnforcementTests.cs`, and the DI resolution guard in `tests/Connapse.Integration.Tests/` (extend an existing Azure DI test or add one).

---

## Task 1: `StateFor(bool)` overload (additive; AWS untouched)

**Files:**
- Modify: `src/Connapse.Core/Models/PermissionEnforcementSettings.cs`
- Test: `tests/Connapse.Core.Tests/Models/PermissionEnforcementBoolStateTests.cs`

**Interfaces:**
- Consumes: `EnforcementState`, `IsEnforcing`.
- Produces: `EnforcementState StateFor(bool providerConfigured, bool determined = true)`. The existing `StateFor(SamlSignInSettings, bool)` now delegates: `StateFor(signIn.IsConfigured, determined)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Core.Tests/Models/PermissionEnforcementBoolStateTests.cs
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class PermissionEnforcementBoolStateTests
{
    [Fact]
    public void Undetermined_IsEnforcingButUnusable()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = true };
        s.StateFor(providerConfigured: true, determined: false).Should().Be(EnforcementState.EnforcingButUnusable);
    }

    [Fact]
    public void NotLatched_IsNotEnforcing()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = false };
        s.StateFor(providerConfigured: true).Should().Be(EnforcementState.NotEnforcing);
    }

    [Fact]
    public void Latched_ProviderConfigured_IsEnforcing()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = true };
        s.StateFor(providerConfigured: true).Should().Be(EnforcementState.Enforcing);
    }

    [Fact]
    public void Latched_ProviderNotConfigured_IsEnforcingButUnusable()
    {
        var s = new PermissionEnforcementSettings { IsEnforcing = true };
        s.StateFor(providerConfigured: false).Should().Be(EnforcementState.EnforcingButUnusable);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~PermissionEnforcementBoolStateTests"`
Expected: FAIL — the bool overload does not exist.

- [ ] **Step 3: Add the overload**

In `PermissionEnforcementSettings`, replace the existing `StateFor(SamlSignInSettings, bool)` body with a delegation and add the bool overload:

```csharp
    /// <summary>What a resolver should do, given whether its own sign-in provider is configured.</summary>
    /// <param name="providerConfigured">True when the resolver's identity provider (SAML, Azure AD)
    /// has a complete sign-in configuration.</param>
    /// <param name="determined">
    /// False when the startup migration could not establish whether this deployment was already
    /// enforcing. An undetermined deployment enforces: not knowing is not permission to open.
    /// </param>
    public EnforcementState StateFor(bool providerConfigured, bool determined = true)
    {
        if (!determined)
            return EnforcementState.EnforcingButUnusable;

        if (!IsEnforcing)
            return EnforcementState.NotEnforcing;

        return providerConfigured
            ? EnforcementState.Enforcing
            : EnforcementState.EnforcingButUnusable;
    }

    /// <summary>What a resolver should do, given the SAML sign-in settings it has. Delegates to the
    /// provider-agnostic overload — kept so the AWS resolver and its tests are unchanged.</summary>
    public EnforcementState StateFor(SamlSignInSettings signIn, bool determined = true)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        return StateFor(signIn.IsConfigured, determined);
    }
```

- [ ] **Step 4: Run both the new test and the existing AWS enforcement tests**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~PermissionEnforcementBoolStateTests|FullyQualifiedName~SamlEnforcementStateTests"`
Expected: PASS — the new bool cases and the unchanged `SamlEnforcementStateTests` (which exercise the delegating overload) both green.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/PermissionEnforcementSettings.cs tests/Connapse.Core.Tests/Models/PermissionEnforcementBoolStateTests.cs
git commit -m "feat(azure): add provider-agnostic PermissionEnforcementSettings.StateFor(bool) overload (#488)"
```

---

## Task 2: `AzureSearchScopeResolver` (flat)

**Files:**
- Create: `src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureSearchScopeResolverTests.cs`

**Interfaces:**
- Consumes: `ISearchScopeResolver`/`SearchScopes` (Core); `IAzureIdentityLinkReader`→`AzureIdentityRef` (Phase 3); `IAzureDirectoryReader`→`AzureIdentitySet` (4a); `IAzureRbacReader`→`AzureRbacScopes` (4b); `AzureAdSignInSettings` (Phase 3, has `IsConfigured`); `PermissionEnforcementSettings`; `EnforcementMigration`; `IMemoryCache`.
- Produces: `AzureSearchScopeResolver(...) : ISearchScopeResolver`; `public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60)`.

Mirrors `AwsSearchScopeResolver`: enforcement gate first, then link → deprovision gate → RBAC prefixes. **Only RBAC `ReadablePrefixes` are used** (tag residue and Gen2 are deferred to 4e). Fails closed on every uncertain path; caches only confident answers.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/AzureSearchScopeResolverTests.cs
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureSearchScopeResolverTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly AzureIdentityRef Link = new("oid-1", "tid-1");

    private static IOptionsMonitor<T> Opt<T>(T value) where T : class
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(value);
        return m;
    }

    private static AzureSearchScopeResolver Build(
        IAzureIdentityLinkReader? links = null,
        IAzureDirectoryReader? directory = null,
        IAzureRbacReader? rbac = null,
        bool azureAdConfigured = true,
        bool isEnforcing = true,
        bool determined = true)
    {
        var azureAd = Opt(new AzureAdSignInSettings
        {
            TenantId = azureAdConfigured ? "t" : null,
            ClientId = azureAdConfigured ? "c" : null,
            RedirectUri = azureAdConfigured ? "https://x/cb" : null,
            ClientCertificatePath = azureAdConfigured ? "cert.pem" : null,
        });
        var enforcement = Opt(new PermissionEnforcementSettings { IsEnforcing = isEnforcing });
        EnforcementMigration migration = determined ? EnforcementMigration.Completed() : new EnforcementMigration();

        return new AzureSearchScopeResolver(
            links ?? Substitute.For<IAzureIdentityLinkReader>(),
            directory ?? Substitute.For<IAzureDirectoryReader>(),
            rbac ?? Substitute.For<IAzureRbacReader>(),
            azureAd, enforcement, migration,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<AzureSearchScopeResolver>.Instance);
    }

    [Fact]
    public async Task NotEnforcing_IsUnrestricted()
    {
        var r = Build(isEnforcing: false);
        (await r.ResolveAsync(User)).IsUnrestricted.Should().BeTrue();
    }

    [Fact]
    public async Task Enforcing_ButAzureAdNotConfigured_Fails()
    {
        var r = Build(azureAdConfigured: false);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task Undetermined_Fails()
    {
        var r = Build(determined: false);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task NullUser_IsNoPrincipal()
    {
        var r = Build();
        (await r.ResolveAsync(null)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task NoLink_IsNoPrincipal()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns((AzureIdentityRef?)null);
        var r = Build(links: links);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task Deprovisioned_IsNoPrincipal()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Deprovisioned());
        var r = Build(links: links, directory: directory);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.NoPrincipal);
    }

    [Fact]
    public async Task IdentityResolutionFailed_Fails()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Failed());
        var r = Build(links: links, directory: directory);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task RbacFailed_Fails()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Resolved(["oid-1"]));
        var rbac = Substitute.For<IAzureRbacReader>();
        rbac.ResolveAsync("oid-1", Arg.Any<CancellationToken>()).Returns(AzureRbacScopes.Failed());
        var r = Build(links: links, directory: directory, rbac: rbac);
        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.ResolverFailed);
    }

    [Fact]
    public async Task Enabled_WithRbacPrefixes_ReturnsGrantedAzblobMatches()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Resolved(["oid-1"]));
        var rbac = Substitute.For<IAzureRbacReader>();
        rbac.ResolveAsync("oid-1", Arg.Any<CancellationToken>()).Returns(
            AzureRbacScopes.Resolved([new AzureScope("azblob://acct/docs/")], []));
        var r = Build(links: links, directory: directory, rbac: rbac);

        SearchScopes s = await r.ResolveAsync(User);
        s.Outcome.Should().Be(ScopeOutcome.Granted);
        s.Matches.Should().ContainSingle().Which.Value.Should().Be("azblob://acct/docs/");
        s.Matches[0].IsExact.Should().BeFalse();
    }

    [Fact]
    public async Task Enabled_NoRbacPrefixes_IsNoGrants()
    {
        var links = Substitute.For<IAzureIdentityLinkReader>();
        links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
        var directory = Substitute.For<IAzureDirectoryReader>();
        directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Resolved(["oid-1"]));
        var rbac = Substitute.For<IAzureRbacReader>();
        rbac.ResolveAsync("oid-1", Arg.Any<CancellationToken>()).Returns(AzureRbacScopes.Resolved([], []));
        var r = Build(links: links, directory: directory, rbac: rbac);

        (await r.ResolveAsync(User)).Outcome.Should().Be(ScopeOutcome.NoGrants);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureSearchScopeResolverTests"`
Expected: FAIL — `AzureSearchScopeResolver` does not exist.

- [ ] **Step 3: Write the resolver**

```csharp
// src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Answers what a Connapse user may search in Azure Blob, from the Storage-Blob-Data role
/// assignments held against the Entra identity they linked. Flat accounts only: the RBAC prefix set
/// is exact and complete. Mirrors <see cref="AwsSearchScopeResolver"/> — enforcement gate first,
/// then link → deprovisioning gate → RBAC — and fails closed on every uncertain path.
/// </summary>
public sealed class AzureSearchScopeResolver(
    IAzureIdentityLinkReader links,
    IAzureDirectoryReader directory,
    IAzureRbacReader rbac,
    IOptionsMonitor<AzureAdSignInSettings> azureAd,
    IOptionsMonitor<PermissionEnforcementSettings> enforcement,
    EnforcementMigration migration,
    IMemoryCache cache,
    ILogger<AzureSearchScopeResolver> logger) : ISearchScopeResolver
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private const string KeyPrefix = "azure-scopes:";

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        switch (enforcement.CurrentValue.StateFor(azureAd.CurrentValue.IsConfigured, migration.Determined))
        {
            case EnforcementState.NotEnforcing:
                return SearchScopes.Unrestricted;
            case EnforcementState.EnforcingButUnusable:
                logger.LogError(
                    "Azure per-user permissions cannot be determined — Azure AD sign-in is incomplete or the startup migration did not complete; denying rather than widening");
                return SearchScopes.Failed;
        }

        if (userId is null)
            return SearchScopes.NoPrincipal;

        string key = KeyPrefix + userId.Value;
        if (cache.TryGetValue(key, out SearchScopes? cached) && cached is not null)
            return cached;

        SearchScopes resolved;
        try
        {
            resolved = await ResolveUncachedAsync(userId.Value, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve Azure search scopes; denying rather than widening");
            return SearchScopes.Failed;
        }

        if (resolved.Outcome is ScopeOutcome.Granted or ScopeOutcome.NoGrants)
            cache.Set(key, resolved, CacheLifetime);
        return resolved;
    }

    private async Task<SearchScopes> ResolveUncachedAsync(Guid userId, CancellationToken ct)
    {
        AzureIdentityRef? link = await links.GetLinkAsync(userId, ct);
        if (link is null)
            return SearchScopes.NoPrincipal;

        // Deprovisioning gate: a disabled/deleted Entra account is denied even if role assignments
        // still exist. A Failed identity lookup is an uncertain answer → deny.
        AzureIdentitySet identity = await directory.ResolveAsync(link, ct);
        if (identity.Outcome is AzureIdentityOutcome.Deprovisioned)
        {
            logger.LogInformation("A linked Entra identity is disabled or gone; denying");
            return SearchScopes.NoPrincipal;
        }
        if (identity.Outcome is AzureIdentityOutcome.Failed)
            return SearchScopes.Failed;

        AzureRbacScopes scopes = await rbac.ResolveAsync(link.ObjectId, ct);
        if (scopes.Outcome is RbacOutcome.Failed)
            return SearchScopes.Failed;

        // Flat accounts: RBAC readable prefixes are the answer. Tag-conditioned residue and Gen2
        // ACLs are NOT admitted here — they require Phase 4e's live per-hit verifier; omitting them
        // is a temporary under-grant on the epic branch that 4e closes before the epic reaches main.
        var matches = scopes.ReadablePrefixes
            .Select(s => new GrantMatch(s.Prefix, IsExact: false))
            .ToList();

        return SearchScopes.Of(matches);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureSearchScopeResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs tests/Connapse.Storage.Tests/CloudScope/AzureSearchScopeResolverTests.cs
git commit -m "feat(azure): AzureSearchScopeResolver (flat) resolves azblob scopes from RBAC (#488)"
```

---

## Task 3: `CompositeSearchScopeResolver`

**Files:**
- Create: `src/Connapse.Storage/CloudScope/CompositeSearchScopeResolver.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/CompositeSearchScopeResolverTests.cs`

**Interfaces:**
- Consumes: `AwsSearchScopeResolver`, `AzureSearchScopeResolver` (concrete), `SearchScopes`, `GrantMatch`, `ScopeOutcome`.
- Produces: `CompositeSearchScopeResolver(AwsSearchScopeResolver aws, AzureSearchScopeResolver azure) : ISearchScopeResolver`.

Combine rule (per-cloud fail-closed, per-scheme): each cloud → contribution (Unrestricted → its scheme wildcard; Granted → its matches; None/NoPrincipal/Failed → nothing). Both Unrestricted → global `Unrestricted`. Else → `SearchScopes.Of(union)`. Each inner resolve is wrapped so one cloud throwing/denying never affects the other.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/CompositeSearchScopeResolverTests.cs
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class CompositeSearchScopeResolverTests
{
    // The composite composes two ISearchScopeResolver results; drive it through a tiny fake for each
    // cloud rather than the concrete resolvers (those have their own tests).
    private sealed class FakeResolver(SearchScopes result) : ISearchScopeResolver
    {
        public Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private static SearchScopes S3(params string[] prefixes) =>
        SearchScopes.OfPrefixes(prefixes.Select(p => p).ToList());

    private static async Task<SearchScopes> Combine(SearchScopes aws, SearchScopes azure)
    {
        var c = new CompositeSearchScopeResolver.Combiner();
        return await Task.FromResult(c.Combine(aws, azure));
    }

    [Fact]
    public async Task BothUnrestricted_IsUnrestricted() =>
        (await Combine(SearchScopes.Unrestricted, SearchScopes.Unrestricted)).IsUnrestricted.Should().BeTrue();

    [Fact]
    public async Task AwsGranted_AzureUnrestricted_UnionsPrefixesAndAzureWildcard()
    {
        SearchScopes r = await Combine(SearchScopes.OfPrefixes(["s3://bucket/team/"]), SearchScopes.Unrestricted);
        r.IsUnrestricted.Should().BeFalse();
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("s3://bucket/team/", "azblob://");
    }

    [Fact]
    public async Task AwsUnrestricted_AzureGranted_UnionsAwsWildcardAndAzurePrefixes()
    {
        SearchScopes r = await Combine(SearchScopes.Unrestricted, SearchScopes.OfPrefixes(["azblob://acct/docs/"]));
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("s3://", "azblob://acct/docs/");
    }

    [Fact]
    public async Task OneCloudFailed_DoesNotDenyTheOther()
    {
        SearchScopes r = await Combine(SearchScopes.Failed, SearchScopes.OfPrefixes(["azblob://acct/"]));
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("azblob://acct/"); // AWS failed → no s3 matches
    }

    [Fact]
    public async Task BothDeny_IsEmpty()
    {
        SearchScopes r = await Combine(SearchScopes.Failed, SearchScopes.None);
        r.IsEmpty.Should().BeTrue(); // only non-cloud (resource_uri IS NULL) will survive in SQL
    }

    [Fact]
    public async Task AwsGranted_AzureFailed_KeepsAwsHidesAzure()
    {
        SearchScopes r = await Combine(SearchScopes.OfPrefixes(["s3://b/"]), SearchScopes.Failed);
        r.Matches.Select(m => m.Value).Should().BeEquivalentTo("s3://b/");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~CompositeSearchScopeResolverTests"`
Expected: FAIL — `CompositeSearchScopeResolver` does not exist.

- [ ] **Step 3: Write the composite**

```csharp
// src/Connapse.Storage/CloudScope/CompositeSearchScopeResolver.cs
using Connapse.Core;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Unions the AWS and Azure scope resolvers into one, per cloud and per URI scheme. Each cloud
/// governs its own scheme (<c>s3://</c>, <c>azblob://</c>); non-cloud documents (resource_uri NULL)
/// are always admitted by the store's existing fallback. A cloud that is not enforcing contributes
/// its scheme wildcard (its docs visible); a cloud that denies or fails contributes nothing (its
/// docs hidden), and one cloud's failure never denies the other's or non-cloud docs. Only when both
/// clouds are unrestricted is the result globally unrestricted.
/// </summary>
public sealed class CompositeSearchScopeResolver(
    AwsSearchScopeResolver aws,
    AzureSearchScopeResolver azure) : ISearchScopeResolver
{
    private static readonly Combiner Combine = new();

    public async Task<SearchScopes> ResolveAsync(Guid? userId, CancellationToken ct = default)
    {
        SearchScopes awsScopes = await SafeResolveAsync(aws, userId, ct);
        SearchScopes azureScopes = await SafeResolveAsync(azure, userId, ct);
        return Combine.Combine(awsScopes, azureScopes);
    }

    // A throw from one cloud fails only that cloud closed; cancellation still propagates.
    private static async Task<SearchScopes> SafeResolveAsync(ISearchScopeResolver inner, Guid? userId, CancellationToken ct)
    {
        try
        {
            return await inner.ResolveAsync(userId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return SearchScopes.Failed;
        }
    }

    /// <summary>The pure combine rule, separated so it can be unit-tested without resolvers.</summary>
    public sealed class Combiner
    {
        private const string S3Scheme = "s3://";
        private const string AzureScheme = "azblob://";

        public SearchScopes Combine(SearchScopes awsScopes, SearchScopes azureScopes)
        {
            if (awsScopes.IsUnrestricted && azureScopes.IsUnrestricted)
                return SearchScopes.Unrestricted;

            var matches = new List<GrantMatch>();
            matches.AddRange(ContributionFor(awsScopes, S3Scheme));
            matches.AddRange(ContributionFor(azureScopes, AzureScheme));
            return SearchScopes.Of(matches);
        }

        // Unrestricted → the scheme wildcard (all of that cloud's docs). Granted → its own matches.
        // Any denial/failure/no-principal → nothing (that scheme's docs are hidden — fail closed).
        private static IReadOnlyList<GrantMatch> ContributionFor(SearchScopes scopes, string schemeWildcard)
        {
            if (scopes.IsUnrestricted)
                return [new GrantMatch(schemeWildcard, IsExact: false)];
            if (scopes.Outcome is ScopeOutcome.Granted)
                return scopes.Matches;
            return [];
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~CompositeSearchScopeResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/CompositeSearchScopeResolver.cs tests/Connapse.Storage.Tests/CloudScope/CompositeSearchScopeResolverTests.cs
git commit -m "feat(azure): CompositeSearchScopeResolver unions AWS + Azure per-scheme (#488)"
```

---

## Task 4: Broaden the enforcement latch to Azure AD

**Files:**
- Rename: `src/Connapse.Web/Services/SamlEnforcementLatch.cs` → `src/Connapse.Web/Services/CloudEnforcementLatch.cs` (class `SamlEnforcementLatch` → `CloudEnforcementLatch`)
- Modify: its `IHostedService` registration (grep `SamlEnforcementLatch` under `src/Connapse.Web`)
- Test: `tests/Connapse.Web.Tests/Services/CloudEnforcementLatchTests.cs` (rename/extend the existing `SamlEnforcementLatchTests`)

**Interfaces:**
- Consumes: `IOptionsMonitor<SamlSignInSettings>`, and **newly** `IOptionsMonitor<AzureAdSignInSettings>`; `PermissionEnforcementSettings`; `EnforcementMigration`.
- Produces: the deployment latches `IsEnforcing` when SAML **or** Azure AD is configured.

- [ ] **Step 1: Rename the class + file, inject Azure AD settings, broaden the "never configured" check**

Rename the file and type to `CloudEnforcementLatch`. Add `IOptionsMonitor<AzureAdSignInSettings> azureAd` to the constructor. Change the "never configured" short-circuit from:

```csharp
if (!signIn.CurrentValue.IsConfigured)
{
    migration.Complete();
    return;
}
```

to:

```csharp
// Enforcement is opt-in via ANY cloud identity provider. Once SAML OR Azure AD is configured,
// the deployment latches into enforcing mode; before that, searches are unfiltered.
if (!signIn.CurrentValue.IsConfigured && !azureAd.CurrentValue.IsConfigured)
{
    migration.Complete();
    return;
}
```

Leave every other line (reload-or-refuse, already-enforcing short-circuit, the save that sets `IsEnforcing = true`, `migration.Complete()` placement, and the catch that deliberately does not complete) unchanged. Update the class XML doc to say "SAML or Azure AD".

- [ ] **Step 2: Update the registration**

Grep: `grep -rn "SamlEnforcementLatch" src/Connapse.Web` — update the `AddHostedService<SamlEnforcementLatch>()` (or equivalent) to `AddHostedService<CloudEnforcementLatch>()`. Confirm `AzureAdSignInSettings` is `Configure`d in the app (Phase 3 registered `services.Configure<AzureAdSignInSettings>(...)` in Identity) so `IOptionsMonitor<AzureAdSignInSettings>` resolves.

- [ ] **Step 3: Rename/extend the latch tests**

Rename `tests/Connapse.Web.Tests/Services/SamlEnforcementLatchTests.cs` → `CloudEnforcementLatchTests.cs` and the class accordingly; update construction to pass an `IOptionsMonitor<AzureAdSignInSettings>` (default: not configured, so all existing SAML-only cases behave exactly as before). Add one case:

```csharp
[Fact]
public async Task Latches_When_OnlyAzureAd_Configured()
{
    // SAML not configured, Azure AD configured, not yet marked → the latch turns enforcement on.
    // (Mirror the existing "configured-but-unmarked → saves IsEnforcing = true" test, but with the
    // Azure AD settings configured and SAML blank.)
}
```

Fill the body by mirroring the existing configured-but-unmarked test (assert `ISettingsStore.SaveAsync` was called with `IsEnforcing = true` and `migration.Complete()` ran), swapping which provider is configured.

- [ ] **Step 4: Run the latch tests**

Run: `dotnet test tests/Connapse.Web.Tests/Connapse.Web.Tests.csproj --filter "FullyQualifiedName~CloudEnforcementLatchTests"`
Expected: PASS — all prior SAML cases plus the Azure-AD-only latch case.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Web/Services/CloudEnforcementLatch.cs tests/Connapse.Web.Tests/Services/CloudEnforcementLatchTests.cs
git rm src/Connapse.Web/Services/SamlEnforcementLatch.cs tests/Connapse.Web.Tests/Services/SamlEnforcementLatchTests.cs 2>/dev/null; true
git add -A src/Connapse.Web
git commit -m "feat(azure): latch enforcement on SAML or Azure AD (CloudEnforcementLatch) (#488)"
```

---

## Task 5: DI rewiring — register the composite

**Files:**
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Connapse.Integration.Tests/AzureCompositeResolverDiIntegrationTests.cs`

**Interfaces:**
- Consumes: `AwsSearchScopeResolver`, `AzureSearchScopeResolver`, `CompositeSearchScopeResolver`.

- [ ] **Step 1: Change the registration**

Replace the single line `services.AddScoped<ISearchScopeResolver, CloudScope.AwsSearchScopeResolver>();` with:

```csharp
        // The composite is THE resolver; it consumes the AWS and Azure resolvers as concrete types
        // and unions them per cloud/scheme. AWS keeps its exact behavior as one inner resolver.
        services.AddScoped<CloudScope.AwsSearchScopeResolver>();
        services.AddScoped<CloudScope.AzureSearchScopeResolver>();
        services.AddScoped<ISearchScopeResolver, CloudScope.CompositeSearchScopeResolver>();
```

Confirm the Azure resolver's dependencies resolve: `IAzureIdentityLinkReader` (Identity), `IAzureDirectoryReader` (4a, Storage), `IAzureRbacReader` (4b, Storage), `IOptionsMonitor<AzureAdSignInSettings>` (Configured in Identity), `IOptionsMonitor<PermissionEnforcementSettings>`, `EnforcementMigration`, `IMemoryCache` — all already registered.

- [ ] **Step 2: Write the DI resolution test**

```csharp
// tests/Connapse.Integration.Tests/AzureCompositeResolverDiIntegrationTests.cs
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureCompositeResolverDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_CompositeAsTheScopeResolver()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        ISearchScopeResolver resolver = scope.ServiceProvider.GetRequiredService<ISearchScopeResolver>();
        resolver.Should().BeOfType<CompositeSearchScopeResolver>();
        scope.ServiceProvider.GetRequiredService<AzureSearchScopeResolver>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<AwsSearchScopeResolver>().Should().NotBeNull();
    }
}
```

- [ ] **Step 3: Run it (fails before Step 1's rebuild, passes after)**

Run: `dotnet test tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj --filter "FullyQualifiedName~AzureCompositeResolverDiIntegrationTests"` (needs Docker)
Expected: PASS — the composite is resolved as `ISearchScopeResolver` and both inner resolvers resolve.

- [ ] **Step 4: Commit**

```bash
git add src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Integration.Tests/AzureCompositeResolverDiIntegrationTests.cs
git commit -m "feat(azure): register CompositeSearchScopeResolver as the scope resolver (#488)"
```

---

## Task 6: Flat end-to-end enforcement integration test

**Files:**
- Test: `tests/Connapse.Integration.Tests/AzureFlatEnforcementTests.cs`

**Interfaces:**
- Consumes: the full stack (composite + Azure resolver + the SQL filter). Follows `SearchScopeEnforcementTests` (AWS) as the template — seed documents with `resource_uri` values, resolve scopes, assert the SQL filter admits/excludes correctly.

- [ ] **Step 1: Read the AWS template**

Read `tests/Connapse.Integration.Tests/SearchScopeEnforcementTests.cs` to reuse its fixture pattern (how it seeds `DocumentEntity` rows with `resource_uri`, builds a `SearchScopes`, and asserts which rows a store search returns). Mirror it for Azure.

- [ ] **Step 2: Write the tests**

Seed four documents: `azblob://acct/docs/a` (Azure, in scope), `azblob://acct/secret/b` (Azure, out of scope), `s3://bucket/x` (AWS), and one with `resource_uri = NULL` (non-cloud). Then, using a `SearchScopes` equal to what the composite yields for an Azure-granted / AWS-unrestricted user (`azblob://acct/docs/` prefix + `s3://` wildcard), assert a store query returns: the in-scope Azure doc, the AWS doc, and the NULL doc — but **not** the out-of-scope Azure doc. Add a second case where Azure is `Failed` (no azblob matches): assert **no** Azure docs are returned but the NULL doc still is. Use the same store-level assertion mechanism `SearchScopeEnforcementTests` uses (do not re-implement the SQL).

```csharp
// tests/Connapse.Integration.Tests/AzureFlatEnforcementTests.cs — shape (fill bodies from the AWS template)
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureFlatEnforcementTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task AzurePrefixPlusAwsWildcard_AdmitsInScopeAzure_AwsAndNonCloud_ExcludesOutOfScopeAzure()
    {
        // scopes = Of([ GrantMatch("azblob://acct/docs/", false), GrantMatch("s3://", false) ])
        // Seed the four docs above; run the store search with these scopes; assert the returned
        // resource_uris are exactly { azblob://acct/docs/a, s3://bucket/x, <null doc> }.
    }

    [Fact]
    public async Task AzureFailed_HidesAllAzure_ButKeepsNonCloud()
    {
        // scopes = Of([ GrantMatch("s3://", false) ])  // Azure contributed nothing
        // Assert no azblob:// doc is returned; the null-resource_uri doc still is.
    }
}
```

- [ ] **Step 3: Run + full build + suite, commit**

Run: `dotnet build`, then `dotnet test --filter "Category=Unit"` (all green incl. new resolver/composite/enforcement tests), then the two Azure integration tests (Docker). Expected: green (23 pre-existing Ollama integration failures remain, unrelated).

```bash
git add tests/Connapse.Integration.Tests/AzureFlatEnforcementTests.cs
git commit -m "test(azure): flat end-to-end enforcement — Azure prefixes filter, non-cloud survives (#488)"
```

---

## Self-Review notes

- **Spec §C/§D coverage:** flat resolver from 4a identity + 4b RBAC (Task 2, tag/Gen2 deferred to 4e); composite per-cloud/per-scheme with fail-closed isolation + scheme wildcards + both-unrestricted→Unrestricted (Task 3); enforcement generalized to SAML-or-Azure-AD via additive `StateFor(bool)` + broadened latch (Tasks 1, 4); DI drop-in with AWS untouched (Task 5); flat end-to-end proof + non-cloud always-visible (Task 6). The shared SQL and AWS resolver are unmodified. ✅
- **AWS unchanged:** `StateFor(bool)` is additive (the `SamlSignInSettings` overload delegates); `AwsSearchScopeResolver` and `SamlEnforcementStateTests` are not edited; `PgVectorStore`/`KeywordSearchService` are not edited; the latch broadening only adds Azure AD as an alternative trigger.
- **Deferred to 4e:** tag-conditioned RBAC residue, Gen2 Phase-A/B, the post-retrieval verifier and over-fetch. 4c ships flat correctness; Gen2/tag completeness lands in 4d/4e before the epic reaches main.
- **Type consistency:** `StateFor(bool,bool)`, `AzureSearchScopeResolver`, `CompositeSearchScopeResolver`(+`Combiner`), `CloudEnforcementLatch` referenced consistently; the composite consumes the two concrete resolvers; `AzureRbacScopes.ReadablePrefixes`/`AzureScope.Prefix` and `AzureIdentitySet.Outcome` used as defined in 4a/4b.
