# Azure Phase 4a — Searcher identity resolution (Graph) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve a linked Entra searcher (oid) into an identity set **P** = {oid} ∪ {transitive security-group oids}, gated by a live deprovisioning check, in one Microsoft Graph `$batch`, cached ~5 min, failing closed.

**Architecture:** A Core contract (`IAzureDirectoryReader` → `AzureIdentitySet`) and a Storage implementation (`GraphDirectoryReader`) that authenticates to Graph with Connapse's own `TokenCredential` (`ConnapseAzureCredentials`), issues one `$batch` (deprovisioning gate + `getMemberGroups`), and caches confident answers. Library only — nothing wires it into search yet (that is 4c). Mirrors the AWS `IDirectoryUserLookup`/`IdentityStoreUserLookup` split and `AwsSearchScopeResolver`'s fail-closed caching discipline.

**Tech Stack:** .NET 10, `Azure.Core` (`TokenCredential`), `System.Net.Http.Json`, `System.Text.Json`, `IMemoryCache`, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-06-azure-phase4-permission-engine-design.md` (§A, and the governing sound/complete + fail-closed principle).

## Global Constraints

- **Fail closed.** Any missing/absent value, non-200, deprovisioned account, or exception → deny (`Enabled=false`, empty `PrincipalOids`), never a populated set. Only `Resolved` and `Deprovisioned` are cacheable; `Failed` is never cached.
- **Never trust the token `groups` claim** — groups come only from Graph `getMemberGroups`.
- **Live only, no new persistence** — the only state is the in-memory cache (freshness = TTL).
- **Read-only**, cert-based auth via `ConnapseAzureCredentials`; no client secrets, no writes.
- **.NET 10 conventions:** file-scoped namespaces, records for DTOs, primary constructors, async all the way, no `var` for primitive types, no `dynamic`.
- **Consumes** Phase 3's `AzureIdentityRef(string ObjectId, string TenantId)` (namespace `Connapse.Core`). Single-tenant: use `ObjectId`; `TenantId` is not needed for the Graph calls (the app queries its own tenant).

---

## File Structure

- Create `src/Connapse.Core/Models/AzureIdentitySet.cs` — `AzureIdentitySet` record + `AzureIdentityOutcome` enum (namespace `Connapse.Core`).
- Create `src/Connapse.Core/Interfaces/IAzureDirectoryReader.cs` — the contract (namespace `Connapse.Core.Interfaces`).
- Create `src/Connapse.Storage/CloudScope/GraphDirectoryReader.cs` — the Graph implementation (namespace `Connapse.Storage.CloudScope`).
- Modify `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register the `TokenCredential` mapping + the typed HttpClient.
- Create `tests/Connapse.Core.Tests/Models/AzureIdentitySetTests.cs`
- Create `tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs`
- Create `tests/Connapse.Integration.Tests/AzureDirectoryReaderDiIntegrationTests.cs`

---

## Task 1: Core contract — `AzureIdentitySet` + `IAzureDirectoryReader`

**Files:**
- Create: `src/Connapse.Core/Models/AzureIdentitySet.cs`
- Create: `src/Connapse.Core/Interfaces/IAzureDirectoryReader.cs`
- Test: `tests/Connapse.Core.Tests/Models/AzureIdentitySetTests.cs`

**Interfaces:**
- Consumes: `AzureIdentityRef(string ObjectId, string TenantId)` (namespace `Connapse.Core`).
- Produces: `AzureIdentityOutcome { Resolved, Deprovisioned, Failed }`; `AzureIdentitySet(bool Enabled, IReadOnlyList<string> PrincipalOids, AzureIdentityOutcome Outcome)` with static factories `Resolved(IReadOnlyList<string>)`, `Deprovisioned()`, `Failed()`; `IAzureDirectoryReader.ResolveAsync(AzureIdentityRef link, CancellationToken ct = default) → Task<AzureIdentitySet>`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Core.Tests/Models/AzureIdentitySetTests.cs
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class AzureIdentitySetTests
{
    [Fact]
    public void Resolved_IsEnabled_AndCarriesPrincipals()
    {
        AzureIdentitySet set = AzureIdentitySet.Resolved(["oid-1", "group-a"]);

        set.Enabled.Should().BeTrue();
        set.Outcome.Should().Be(AzureIdentityOutcome.Resolved);
        set.PrincipalOids.Should().Equal("oid-1", "group-a");
    }

    [Fact]
    public void Deprovisioned_And_Failed_DenyWithNoPrincipals()
    {
        AzureIdentitySet.Deprovisioned().Enabled.Should().BeFalse();
        AzureIdentitySet.Deprovisioned().Outcome.Should().Be(AzureIdentityOutcome.Deprovisioned);
        AzureIdentitySet.Deprovisioned().PrincipalOids.Should().BeEmpty();

        AzureIdentitySet.Failed().Enabled.Should().BeFalse();
        AzureIdentitySet.Failed().Outcome.Should().Be(AzureIdentityOutcome.Failed);
        AzureIdentitySet.Failed().PrincipalOids.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AzureIdentitySetTests"`
Expected: FAIL — `AzureIdentitySet` / `AzureIdentityOutcome` do not exist (compile error).

- [ ] **Step 3: Write the types**

```csharp
// src/Connapse.Core/Models/AzureIdentitySet.cs
namespace Connapse.Core;

/// <summary>What the directory said about a searcher when their identity set was resolved.</summary>
public enum AzureIdentityOutcome
{
    /// <summary>A confident answer: the account is enabled and its group set is known.</summary>
    Resolved,

    /// <summary>The directory says the account is gone (404) or disabled — deny, and cacheable.</summary>
    Deprovisioned,

    /// <summary>The directory could not be asked (error/timeout/partial). Deny, and never cached.</summary>
    Failed,
}

/// <summary>
/// A searcher's Entra identity set: the object id plus the transitive security-group object ids
/// permissions may be held against. Fails closed — unless <see cref="Enabled"/> is true,
/// <see cref="PrincipalOids"/> is empty and nothing may be authorized from it.
/// </summary>
public record AzureIdentitySet(bool Enabled, IReadOnlyList<string> PrincipalOids, AzureIdentityOutcome Outcome)
{
    /// <summary>P = {oid} ∪ {group oids}, from a confident directory answer.</summary>
    public static AzureIdentitySet Resolved(IReadOnlyList<string> principalOids) =>
        new(true, principalOids, AzureIdentityOutcome.Resolved);

    /// <summary>The account is gone or disabled. Deny; cacheable.</summary>
    public static AzureIdentitySet Deprovisioned() => new(false, [], AzureIdentityOutcome.Deprovisioned);

    /// <summary>The directory could not be asked. Deny; never cache.</summary>
    public static AzureIdentitySet Failed() => new(false, [], AzureIdentityOutcome.Failed);
}
```

```csharp
// src/Connapse.Core/Interfaces/IAzureDirectoryReader.cs
namespace Connapse.Core.Interfaces;

/// <summary>
/// Resolves a linked Entra searcher into their identity set, reading the directory with Connapse's
/// own Azure identity. Mirrors <c>IDirectoryUserLookup</c> for AWS: rather than acting as the user,
/// Connapse asks the directory about them.
/// </summary>
public interface IAzureDirectoryReader
{
    /// <summary>
    /// The searcher's identity set (object id ∪ transitive security-group object ids), or a
    /// fail-closed denial. Applies the deprovisioning gate first — a gone/disabled account resolves
    /// to <see cref="AzureIdentityOutcome.Deprovisioned"/>.
    /// </summary>
    Task<AzureIdentitySet> ResolveAsync(AzureIdentityRef link, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AzureIdentitySetTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/AzureIdentitySet.cs src/Connapse.Core/Interfaces/IAzureDirectoryReader.cs tests/Connapse.Core.Tests/Models/AzureIdentitySetTests.cs
git commit -m "feat(azure): AzureIdentitySet + IAzureDirectoryReader contract (#486)"
```

---

## Task 2: `GraphDirectoryReader` — `$batch` request + happy-path resolve

**Files:**
- Create: `src/Connapse.Storage/CloudScope/GraphDirectoryReader.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs`

**Interfaces:**
- Consumes: `IAzureDirectoryReader`, `AzureIdentitySet`, `AzureIdentityRef` (Task 1); `Azure.Core.TokenCredential`; `IMemoryCache`.
- Produces: `GraphDirectoryReader(HttpClient httpClient, TokenCredential azureCredential, IMemoryCache cache) : IAzureDirectoryReader`; `public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5)`.

Ctor takes `TokenCredential` (the base type `ConnapseAzureCredentials` extends) so a unit test can inject a stub without real Azure; DI maps it to `ConnapseAzureCredentials` in Task 5.

- [ ] **Step 1: Write the failing test** (happy path + shared test helpers)

```csharp
// tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs
using System.Net;
using System.Text;
using Azure.Core;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class GraphDirectoryReaderTests
{
    private static readonly AzureIdentityRef Link = new("11111111-1111-1111-1111-111111111111", "tenant-1");

    // A minimal stub that returns a queued response and counts how many sends it saw, so caching
    // tests can assert the network was hit exactly once.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sends++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            new("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            new(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // A well-formed $batch response: user enabled, two group oids.
    private static HttpResponseMessage BatchOk(bool accountEnabled, params string[] groups)
    {
        string groupJson = string.Join(",", groups.Select(g => $"\"{g}\""));
        return Json(HttpStatusCode.OK, $$"""
        {"responses":[
          {"id":"user","status":200,"body":{"id":"oid","accountEnabled":{{(accountEnabled ? "true" : "false")}}}},
          {"id":"groups","status":200,"body":{"value":[{{groupJson}}]}}
        ]}
        """);
    }

    private static GraphDirectoryReader NewReader(StubHandler handler, IMemoryCache? cache = null) =>
        new(new HttpClient(handler), new StubTokenCredential(),
            cache ?? new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task Resolve_EnabledUser_ReturnsPrincipalSet_OidPlusGroups()
    {
        var handler = new StubHandler(_ => BatchOk(true, "group-a", "group-b"));
        GraphDirectoryReader reader = NewReader(handler);

        AzureIdentitySet set = await reader.ResolveAsync(Link, CancellationToken.None);

        set.Outcome.Should().Be(AzureIdentityOutcome.Resolved);
        set.Enabled.Should().BeTrue();
        set.PrincipalOids.Should().BeEquivalentTo(Link.ObjectId, "group-a", "group-b");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~GraphDirectoryReaderTests"`
Expected: FAIL — `GraphDirectoryReader` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

```csharp
// src/Connapse.Storage/CloudScope/GraphDirectoryReader.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Resolves a searcher's Entra identity set in one Microsoft Graph <c>$batch</c>: the deprovisioning
/// gate (<c>GET /users/{oid}?$select=id,accountEnabled</c>) and transitive security groups
/// (<c>POST /users/{oid}/getMemberGroups {securityEnabledOnly:true}</c>). Authenticates with
/// Connapse's own <see cref="TokenCredential"/>. Fails closed; caches only confident answers.
/// </summary>
public sealed class GraphDirectoryReader(
    HttpClient httpClient,
    TokenCredential azureCredential,
    IMemoryCache cache) : IAzureDirectoryReader
{
    private const string GraphBatchUrl = "https://graph.microsoft.com/v1.0/$batch";
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    /// <summary>Cache window for a confident answer; also the revocation-propagation delay.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public async Task<AzureIdentitySet> ResolveAsync(AzureIdentityRef link, CancellationToken ct = default)
    {
        string oid = link.ObjectId;
        string cacheKey = "azure-identity:" + oid;
        if (cache.TryGetValue(cacheKey, out AzureIdentitySet? cached) && cached is not null)
            return cached;

        AzureIdentitySet result;
        try
        {
            result = await ResolveUncachedAsync(oid, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any transport/parse failure fails closed and is never cached.
            return AzureIdentitySet.Failed();
        }

        // Only confident answers are cached; a failure must be retried on the next search.
        if (result.Outcome is not AzureIdentityOutcome.Failed)
            cache.Set(cacheKey, result, CacheLifetime);
        return result;
    }

    private async Task<AzureIdentitySet> ResolveUncachedAsync(string oid, CancellationToken ct)
    {
        AccessToken token = await azureCredential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphBatchUrl);
        request.Headers.Authorization = new("Bearer", token.Token);
        request.Content = JsonContent.Create(new BatchRequest(
        [
            new("user", "GET", $"/users/{oid}?$select=id,accountEnabled", null, null),
            new("groups", "POST", $"/users/{oid}/getMemberGroups",
                new GetMemberGroupsBody(true),
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }),
        ]));

        using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return AzureIdentitySet.Failed();   // whole-batch failure (e.g. 401 bad token)

        BatchResponse? batch = await response.Content.ReadFromJsonAsync<BatchResponse>(ct);
        return Interpret(oid, batch);
    }

    /// <summary>
    /// The fail-closed decision, isolated from transport so it is exhaustively unit-testable:
    /// deprovisioning gate first, then the group set. Missing/partial responses fail closed.
    /// </summary>
    internal static AzureIdentitySet Interpret(string oid, BatchResponse? batch)
    {
        SubResponse? user = batch?.Responses?.FirstOrDefault(r => r.Id == "user");
        SubResponse? groups = batch?.Responses?.FirstOrDefault(r => r.Id == "groups");
        if (user is null || groups is null)
            return AzureIdentitySet.Failed();

        // Deprovisioning gate.
        if (user.Status == 404)
            return AzureIdentitySet.Deprovisioned();
        if (user.Status != 200)
            return AzureIdentitySet.Failed();
        if (user.Body?.AccountEnabled != true)
            return AzureIdentitySet.Deprovisioned();

        // Groups must resolve, or the identity set is unknown — fail closed.
        if (groups.Status != 200 || groups.Body?.Value is null)
            return AzureIdentitySet.Failed();

        var principals = new List<string>(1 + groups.Body.Value.Count) { oid };
        principals.AddRange(groups.Body.Value);
        return AzureIdentitySet.Resolved(principals);
    }

    // ---- Graph $batch DTOs ----
    private sealed record BatchRequest([property: JsonPropertyName("requests")] IReadOnlyList<SubRequest> Requests);
    private sealed record SubRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("body")] object? Body,
        [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers);
    private sealed record GetMemberGroupsBody([property: JsonPropertyName("securityEnabledOnly")] bool SecurityEnabledOnly);

    internal sealed record BatchResponse([property: JsonPropertyName("responses")] IReadOnlyList<SubResponse>? Responses);
    internal sealed record SubResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("body")] SubBody? Body);
    internal sealed record SubBody(
        [property: JsonPropertyName("accountEnabled")] bool? AccountEnabled,
        [property: JsonPropertyName("value")] IReadOnlyList<string>? Value);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~GraphDirectoryReaderTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/GraphDirectoryReader.cs tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs
git commit -m "feat(azure): GraphDirectoryReader resolves identity set via Graph \$batch (#486)"
```

---

## Task 3: Fail-closed matrix

**Files:**
- Modify: `tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs` (add cases)

**Interfaces:**
- Consumes: `GraphDirectoryReader`, `AzureIdentitySet` (Task 2). No production change expected — `Interpret` and `ResolveAsync` from Task 2 already encode these paths; this task proves them and adds any missing guard.

- [ ] **Step 1: Write the failing tests**

```csharp
// append to GraphDirectoryReaderTests
[Fact]
public async Task Resolve_UserNotFound_IsDeprovisioned()
{
    var handler = new StubHandler(_ => Json(System.Net.HttpStatusCode.OK, """
    {"responses":[
      {"id":"user","status":404,"body":null},
      {"id":"groups","status":200,"body":{"value":[]}}
    ]}
    """));
    (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
        .Outcome.Should().Be(AzureIdentityOutcome.Deprovisioned);
}

[Fact]
public async Task Resolve_AccountDisabled_IsDeprovisioned()
{
    var handler = new StubHandler(_ => BatchOk(accountEnabled: false, "group-a"));
    (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
        .Outcome.Should().Be(AzureIdentityOutcome.Deprovisioned);
}

[Fact]
public async Task Resolve_GroupsCallFailed_FailsClosed()
{
    var handler = new StubHandler(_ => Json(System.Net.HttpStatusCode.OK, """
    {"responses":[
      {"id":"user","status":200,"body":{"id":"oid","accountEnabled":true}},
      {"id":"groups","status":403,"body":null}
    ]}
    """));
    AzureIdentitySet set = await NewReader(handler).ResolveAsync(Link, CancellationToken.None);
    set.Outcome.Should().Be(AzureIdentityOutcome.Failed);
    set.PrincipalOids.Should().BeEmpty();
}

[Fact]
public async Task Resolve_WholeBatchUnauthorized_FailsClosed()
{
    var handler = new StubHandler(_ => Json(System.Net.HttpStatusCode.Unauthorized, "{}"));
    (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
        .Outcome.Should().Be(AzureIdentityOutcome.Failed);
}

[Fact]
public async Task Resolve_TransportThrows_FailsClosed()
{
    var handler = new StubHandler(_ => throw new HttpRequestException("network down"));
    (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
        .Outcome.Should().Be(AzureIdentityOutcome.Failed);
}

[Fact]
public async Task Resolve_MissingSubResponse_FailsClosed()
{
    var handler = new StubHandler(_ => Json(System.Net.HttpStatusCode.OK,
        """{"responses":[{"id":"user","status":200,"body":{"accountEnabled":true}}]}"""));
    (await NewReader(handler).ResolveAsync(Link, CancellationToken.None))
        .Outcome.Should().Be(AzureIdentityOutcome.Failed);
}
```

- [ ] **Step 2: Run tests to verify they pass (Task 2 logic already covers them)**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~GraphDirectoryReaderTests"`
Expected: PASS. If any case fails, fix the guard in `GraphDirectoryReader.Interpret`/`ResolveUncachedAsync` (do not weaken any deny path) and re-run.

- [ ] **Step 3: Commit**

```bash
git add tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs
git commit -m "test(azure): GraphDirectoryReader fail-closed matrix (#486)"
```

---

## Task 4: Caching — 5 minutes, confident answers only

**Files:**
- Modify: `tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs` (add cases)

**Interfaces:**
- Consumes: `GraphDirectoryReader.CacheLifetime`, the `StubHandler.Sends` counter (Task 2). Caching logic is already in Task 2's `ResolveAsync`; this task proves it.

- [ ] **Step 1: Write the failing tests**

```csharp
// append to GraphDirectoryReaderTests
[Fact]
public async Task Resolve_ConfidentAnswer_IsCached_SecondCallDoesNotHitNetwork()
{
    var handler = new StubHandler(_ => BatchOk(true, "group-a"));
    var cache = new MemoryCache(new MemoryCacheOptions());
    GraphDirectoryReader reader = NewReader(handler, cache);

    await reader.ResolveAsync(Link, CancellationToken.None);
    await reader.ResolveAsync(Link, CancellationToken.None);

    handler.Sends.Should().Be(1); // second answer served from cache
}

[Fact]
public async Task Resolve_Failure_IsNotCached_NextCallRetries()
{
    bool first = true;
    var handler = new StubHandler(_ =>
    {
        if (first) { first = false; return Json(System.Net.HttpStatusCode.Unauthorized, "{}"); }
        return BatchOk(true, "group-a");
    });
    GraphDirectoryReader reader = NewReader(handler);

    (await reader.ResolveAsync(Link, CancellationToken.None)).Outcome.Should().Be(AzureIdentityOutcome.Failed);
    AzureIdentitySet second = await reader.ResolveAsync(Link, CancellationToken.None);

    second.Outcome.Should().Be(AzureIdentityOutcome.Resolved); // failure was retried, not cached
    handler.Sends.Should().Be(2);
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~GraphDirectoryReaderTests"`
Expected: PASS. If the failure case is cached, fix `ResolveAsync` so only non-`Failed` outcomes call `cache.Set`.

- [ ] **Step 3: Commit**

```bash
git add tests/Connapse.Storage.Tests/CloudScope/GraphDirectoryReaderTests.cs
git commit -m "test(azure): GraphDirectoryReader caches confident answers only (#486)"
```

---

## Task 5: DI wiring + resolution test

**Files:**
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Create: `tests/Connapse.Integration.Tests/AzureDirectoryReaderDiIntegrationTests.cs`

**Interfaces:**
- Consumes: `IAzureDirectoryReader`/`GraphDirectoryReader` (Tasks 1-2), `ConnapseAzureCredentials` (already a singleton). Registers a bare `TokenCredential` → `ConnapseAzureCredentials` mapping and the typed HttpClient.

- [ ] **Step 1: Add the DI registration**

In `AddConnapseStorage`, immediately after the existing `services.AddSingleton<CloudScope.ConnapseAzureCredentials>();` block (around line 225), add:

```csharp
        // Expose Connapse's Azure identity as the ambient TokenCredential for Azure control-plane
        // readers (Graph, and ARM in 4b). Nothing else resolves a bare TokenCredential today —
        // connectors take ConnapseAzureCredentials directly — so this mapping is unambiguous.
        services.TryAddSingleton<Azure.Core.TokenCredential>(
            sp => sp.GetRequiredService<CloudScope.ConnapseAzureCredentials>());

        // Reads the Entra directory (deprovisioning gate + transitive groups) over Graph $batch.
        // Typed HttpClient; the 5-minute decision cache is the shared IMemoryCache singleton, so a
        // transient reader instance per resolve still shares one cache across the process.
        services.AddHttpClient<Connapse.Core.Interfaces.IAzureDirectoryReader, CloudScope.GraphDirectoryReader>();
```

Confirm `using Microsoft.Extensions.DependencyInjection.Extensions;` is present (it is — `TryAddSingleton` is already used for `EnforcementMigration`). `AddMemoryCache()` is already called in this method.

- [ ] **Step 2: Write the failing DI resolution test**

```csharp
// tests/Connapse.Integration.Tests/AzureDirectoryReaderDiIntegrationTests.cs
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Proves the Phase 4a Graph identity-reader DI graph resolves from a real host — a clean container
/// start does not catch a missing registration, only constructing the service does. Guards the
/// TokenCredential mapping + typed HttpClient landing together.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureDirectoryReaderDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_AzureDirectoryReader()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAzureDirectoryReader>().Should().NotBeNull();
    }
}
```

- [ ] **Step 3: Run the DI test to verify it fails, then passes**

Run: `dotnet test tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj --filter "FullyQualifiedName~AzureDirectoryReaderDiIntegrationTests"`
Expected before Step 1: FAIL (cannot resolve `IAzureDirectoryReader`). After Step 1: PASS.
(Requires Docker for the shared fixture; the 23 pre-existing Ollama-dependent failures elsewhere are unrelated.)

- [ ] **Step 4: Build the solution and run the full unit suite**

Run: `dotnet build` then `dotnet test --filter "Category=Unit"`
Expected: build clean; unit tests green (new `AzureIdentitySetTests` + `GraphDirectoryReaderTests` included).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Integration.Tests/AzureDirectoryReaderDiIntegrationTests.cs
git commit -m "feat(azure): register GraphDirectoryReader + TokenCredential mapping (#486)"
```

---

## Self-Review notes

- **Spec §A coverage:** deprovisioning gate (Task 2/3), transitive `getMemberGroups` (Task 2), identity set P = {oid} ∪ groups (Task 2), one `$batch` (Task 2), ~5-min cache confident-only (Task 4), never trust token `groups` claim (groups come only from Graph — no token parsing anywhere here), fail-closed matrix (Task 3). ✅
- **Not in 4a (deferred):** RBAC/ARM (4b), any search wiring or resolver (4c), Gen2 (4d/4e). This task ships a library + DI only.
- **Type consistency:** `AzureIdentitySet`/`AzureIdentityOutcome`/`IAzureDirectoryReader.ResolveAsync(AzureIdentityRef, ct)` used identically across tasks; `CacheLifetime` and `StubHandler.Sends` referenced in Task 4 are defined in Task 2.
- **Namespaces:** `Connapse.Core` (AzureIdentitySet), `Connapse.Core.Interfaces` (IAzureDirectoryReader), `Connapse.Storage.CloudScope` (GraphDirectoryReader) — matching existing files in each location.
