# Azure Phase 4b — RBAC scope resolver (ARM) + ABAC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve a searcher (oid) into the set of `azblob://` prefixes they may read via Azure RBAC — one ARM `roleAssignments` call (transitive over groups) minus a parallel `denyAssignments` call, matched against the three Storage-Blob-Data read roles, with ABAC conditions translated to prefixes or tag residue — cached ~5 min, failing closed.

**Architecture:** A Core contract (`IAzureRbacReader` → `AzureRbacScopes`) and a Storage implementation (`ArmRbacReader`) that authenticates to ARM with Connapse's own `TokenCredential`, queries at the configured subscription scope, and returns readable `azblob://` prefixes plus tag-conditioned residue (verified live in 4e). Two pure, exhaustively-tested translators do the bug-prone work: ARM-scope→`azblob://` prefix, and ABAC-condition→(path prefix | container | tag | drop). Library only — no search wiring (that is 4c).

**Tech Stack:** .NET 10, `Azure.Core` (`TokenCredential`), `System.Net.Http.Json`, `System.Text.Json`, `IMemoryCache`, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-06-azure-phase4-permission-engine-design.md` (§B).

## Global Constraints

- **Fail closed.** A missing `SubscriptionId`, a failed/timed-out `roleAssignments` OR `denyAssignments` call, or any exception → `AzureRbacScopes.Failed()` (empty, `Outcome=Failed`), never a populated set. Only `Resolved` is cacheable; `Failed` is never cached. (Follow 4a's `GraphDirectoryReader` caching + cancellation discipline exactly, including `catch (OperationCanceledException) when (ct.IsCancellationRequested)` rethrow vs. internal-timeout → Failed.)
- **Deny wins, and never fails open.** `effective = grants − denies`; a failed deny call → `Failed` (never "assume none"). A deny of uncertain applicability drops the overlapping grant (under-grant, never over-grant).
- **Only the three built-in blob-data-read roles count** (Reader `2a2b9908-6ea1-4ae2-8e65-a410df84e7d1`, Contributor `ba92f5b4-2d11-453d-a403-e96b0029c9fe`, Owner `b7e6dc6d-f1e8-4753-8033-0f276bb0955b`); every other `roleDefinitionId` is ignored.
- **Query subscription scope WITHOUT `atScope()`** (`$filter=assignedTo('{oid}')`, api-version `2022-04-01`), so account- and container-scoped (descendant) assignments are included; `assignedTo` is transitive over groups. Follow the `nextLink` paging chain to completion (dropping a page = under-grant).
- **Unparseable ABAC condition → drop that one assignment** (not the whole resolution).
- **.NET 10 conventions:** file-scoped namespaces, records, primary constructors, async all the way, no `var` for primitive types, no `dynamic`, parameterized only.
- Cert-based ARM auth via `ConnapseAzureCredentials` (scope `https://management.azure.com/.default`), read-only.

## File Structure

- Create `src/Connapse.Core/Models/AzureRbacScopes.cs` — `AzureRbacScopes`, `AzureScope`, `AzureTagCondition`, `RbacOutcome` (namespace `Connapse.Core`).
- Create `src/Connapse.Core/Interfaces/IAzureRbacReader.cs` — the contract (namespace `Connapse.Core.Interfaces`).
- Modify `src/Connapse.Core/Models/AzureProviderSettings.cs` — add `SubscriptionId`.
- Create `src/Connapse.Storage/CloudScope/AzureRbacScopeTranslator.cs` — pure ARM-scope→`azblob://` prefix.
- Create `src/Connapse.Storage/CloudScope/AzureAbacConditionParser.cs` — pure ABAC-condition classifier.
- Create `src/Connapse.Storage/CloudScope/ArmRbacReader.cs` — the ARM implementation.
- Modify `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register the typed HttpClient.
- Tests: `tests/Connapse.Storage.Tests/CloudScope/AzureRbacScopeTranslatorTests.cs`, `AzureAbacConditionParserTests.cs`, `ArmRbacReaderTests.cs`; `tests/Connapse.Integration.Tests/AzureRbacReaderDiIntegrationTests.cs`.

The internal translator/parser types live in `Connapse.Storage.CloudScope` and are exercised through the reader's public `ResolveAsync` where possible; the two pure helpers are `internal static` and unit-tested directly (they need no InternalsVisibleTo if the tests call them via the reader — but because their matrices are large, expose them `public static` on `internal`-free classes in `Connapse.Storage.CloudScope`, which `Connapse.Storage.Tests` already references).

---

## Task 1: Core contract + `SubscriptionId`

**Files:**
- Create: `src/Connapse.Core/Models/AzureRbacScopes.cs`
- Create: `src/Connapse.Core/Interfaces/IAzureRbacReader.cs`
- Modify: `src/Connapse.Core/Models/AzureProviderSettings.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureRbacScopeTranslatorTests.cs` (created here with a placeholder contract test; real cases land in Task 2)

**Interfaces:**
- Produces: `RbacOutcome { Resolved, Failed }`; `AzureScope(string Prefix)`; `AzureTagCondition(string Scope, string TagKey, string TagValue, bool KeyCaseSensitive)`; `AzureRbacScopes(IReadOnlyList<AzureScope> ReadablePrefixes, IReadOnlyList<AzureTagCondition> TagConditioned, RbacOutcome Outcome)` with `Resolved(prefixes, tags)` / `Failed()`; `IAzureRbacReader.ResolveAsync(string primaryOid, CancellationToken) → Task<AzureRbacScopes>`; `AzureProviderSettings.SubscriptionId`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/AzureRbacScopeTranslatorTests.cs
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureRbacScopeTranslatorTests
{
    [Fact]
    public void RbacScopes_Failed_IsEmptyAndFailed()
    {
        AzureRbacScopes f = AzureRbacScopes.Failed();
        f.Outcome.Should().Be(RbacOutcome.Failed);
        f.ReadablePrefixes.Should().BeEmpty();
        f.TagConditioned.Should().BeEmpty();
    }

    [Fact]
    public void RbacScopes_Resolved_CarriesPrefixesAndTags()
    {
        AzureRbacScopes r = AzureRbacScopes.Resolved(
            [new AzureScope("azblob://acct/c/")],
            [new AzureTagCondition("azblob://acct/c/", "Project", "Cascade", true)]);
        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().ContainSingle().Which.Prefix.Should().Be("azblob://acct/c/");
        r.TagConditioned.Should().ContainSingle();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureRbacScopeTranslatorTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write the types**

```csharp
// src/Connapse.Core/Models/AzureRbacScopes.cs
namespace Connapse.Core;

/// <summary>Whether the RBAC scope set was resolved confidently or must fail closed.</summary>
public enum RbacOutcome { Resolved, Failed }

/// <summary>An <c>azblob://</c> prefix the searcher may read via an RBAC grant. A whole-account
/// grant is <c>azblob://{account}/</c>; a grant broader than an account (RG/subscription/management
/// group) is <c>azblob://</c> (matches every account).</summary>
public record AzureScope(string Prefix);

/// <summary>An RBAC grant gated on a blob index-tag condition that cannot reduce to a prefix. The
/// <see cref="Scope"/> is the broad candidate prefix; the tag predicate is verified live per hit
/// (Phase 4e). Never excluded here.</summary>
public record AzureTagCondition(string Scope, string TagKey, string TagValue, bool KeyCaseSensitive);

/// <summary>The searcher's effective RBAC-readable scope set. Fails closed: unless
/// <see cref="Outcome"/> is <see cref="RbacOutcome.Resolved"/>, both lists are empty.</summary>
public record AzureRbacScopes(
    IReadOnlyList<AzureScope> ReadablePrefixes,
    IReadOnlyList<AzureTagCondition> TagConditioned,
    RbacOutcome Outcome)
{
    public static AzureRbacScopes Resolved(
        IReadOnlyList<AzureScope> readablePrefixes, IReadOnlyList<AzureTagCondition> tagConditioned) =>
        new(readablePrefixes, tagConditioned, RbacOutcome.Resolved);

    public static AzureRbacScopes Failed() => new([], [], RbacOutcome.Failed);
}
```

```csharp
// src/Connapse.Core/Interfaces/IAzureRbacReader.cs
namespace Connapse.Core.Interfaces;

/// <summary>
/// Resolves a searcher's effective RBAC-readable <c>azblob://</c> scope set from Azure Resource
/// Manager, reading with Connapse's own Azure identity. Fails closed.
/// </summary>
public interface IAzureRbacReader
{
    /// <summary>
    /// The <c>azblob://</c> prefixes <paramref name="primaryOid"/> may read via Storage Blob Data
    /// roles (transitive over groups, minus deny assignments), plus any tag-conditioned residue.
    /// </summary>
    Task<AzureRbacScopes> ResolveAsync(string primaryOid, CancellationToken ct = default);
}
```

Add to `src/Connapse.Core/Models/AzureProviderSettings.cs` (after `UserAssignedManagedIdentityClientId`):

```csharp
    /// <summary>The subscription whose role/deny assignments the RBAC resolver queries. Required
    /// for per-user Azure permission filtering; absent → the resolver fails closed.</summary>
    public string? SubscriptionId { get; init; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureRbacScopeTranslatorTests"`
Expected: PASS (2).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/AzureRbacScopes.cs src/Connapse.Core/Interfaces/IAzureRbacReader.cs src/Connapse.Core/Models/AzureProviderSettings.cs tests/Connapse.Storage.Tests/CloudScope/AzureRbacScopeTranslatorTests.cs
git commit -m "feat(azure): AzureRbacScopes + IAzureRbacReader contract + SubscriptionId (#487)"
```

---

## Task 2: ARM scope → `azblob://` prefix translator (pure)

**Files:**
- Create: `src/Connapse.Storage/CloudScope/AzureRbacScopeTranslator.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureRbacScopeTranslatorTests.cs` (extend)

**Interfaces:**
- Consumes: nothing (pure).
- Produces: `AzureRbacScopeTranslator.ToAzblobPrefix(string armScope) → string` — the `azblob://` prefix an ARM assignment scope governs.

- [ ] **Step 1: Write the failing tests**

```csharp
// append to AzureRbacScopeTranslatorTests
using Connapse.Storage.CloudScope;

[Theory]
[InlineData(
    "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs",
    "azblob://acct/docs/")]
[InlineData(
    "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct",
    "azblob://acct/")]
[InlineData(
    "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default",
    "azblob://acct/")]
[InlineData("/subscriptions/s/resourceGroups/rg", "azblob://")]
[InlineData("/subscriptions/s", "azblob://")]
[InlineData("/providers/Microsoft.Management/managementGroups/mg", "azblob://")]
public void ToAzblobPrefix_MapsScopeToPrefix(string armScope, string expected) =>
    AzureRbacScopeTranslator.ToAzblobPrefix(armScope).Should().Be(expected);

[Fact]
public void ToAzblobPrefix_IsCaseInsensitiveOnResourceProviderSegments() =>
    AzureRbacScopeTranslator.ToAzblobPrefix(
        "/subscriptions/s/resourceGroups/rg/providers/microsoft.storage/STORAGEACCOUNTS/Acct/blobservices/default/CONTAINERS/Docs")
        .Should().Be("azblob://Acct/Docs/");
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureRbacScopeTranslatorTests"`
Expected: FAIL — `AzureRbacScopeTranslator` does not exist.

- [ ] **Step 3: Write the translator**

```csharp
// src/Connapse.Storage/CloudScope/AzureRbacScopeTranslator.cs
namespace Connapse.Storage.CloudScope;

/// <summary>
/// Translates an ARM role-assignment scope into the <c>azblob://</c> prefix it governs. Resource
/// provider and type segments are matched case-insensitively (ARM is case-insensitive on them);
/// the account and container names keep their original case (they are the resource_uri content).
/// </summary>
public static class AzureRbacScopeTranslator
{
    public static string ToAzblobPrefix(string armScope)
    {
        string[] parts = armScope.Split('/', StringSplitOptions.RemoveEmptyEntries);

        int acctIdx = Array.FindIndex(parts,
            p => string.Equals(p, "storageAccounts", StringComparison.OrdinalIgnoreCase));
        if (acctIdx < 0 || acctIdx + 1 >= parts.Length)
            return "azblob://"; // broader than an account (RG / subscription / management group)

        string account = parts[acctIdx + 1];

        int containersIdx = Array.FindIndex(parts, acctIdx + 1,
            p => string.Equals(p, "containers", StringComparison.OrdinalIgnoreCase));
        if (containersIdx >= 0 && containersIdx + 1 < parts.Length)
            return $"azblob://{account}/{parts[containersIdx + 1]}/";

        return $"azblob://{account}/";
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureRbacScopeTranslatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/AzureRbacScopeTranslator.cs tests/Connapse.Storage.Tests/CloudScope/AzureRbacScopeTranslatorTests.cs
git commit -m "feat(azure): ARM scope to azblob prefix translator (#487)"
```

---

## Task 3: ABAC condition parser (pure)

**Files:**
- Create: `src/Connapse.Storage/CloudScope/AzureAbacConditionParser.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureAbacConditionParserTests.cs`

**Interfaces:**
- Produces: `AbacResult` record `{ AbacKind Kind, string? PathPrefix, string? ContainerName, string? TagKey, string? TagValue, bool TagKeyCaseSensitive }`; `AbacKind { None, PathPrefix, ContainerName, Tag, Unparseable }`; `AzureAbacConditionParser.Parse(string? condition) → AbacResult` (null/blank condition → `None`).

**Interpretation:** A null/empty condition is an unconditional grant (`None`). Otherwise classify by the single resource attribute the condition references. This parser only understands the canonical read templates (per the spec's non-goals: anything else → `Unparseable` → the caller drops that grant). Recognized attribute tokens (case-insensitive on the attribute path, value in single quotes):
- `blobs:path]` with `StringStartsWith`/`StringLike` `'<p>'` → `PathPrefix` = `<p>` with a single trailing `*` stripped.
- `containers:name]` with `StringEquals` `'<c>'` → `ContainerName` = `<c>`.
- `blobs/tags:<Key><$key_case_sensitive$>]` or `blobs/tags:<Key><$key_case_insensitive$>]` with `StringEquals` `'<v>'` → `Tag`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Connapse.Storage.Tests/CloudScope/AzureAbacConditionParserTests.cs
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureAbacConditionParserTests
{
    private const string PathCond =
        "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'} AND NOT SubOperationMatches{'Blob.List'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*'))";
    private const string NameCond =
        "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers:name] StringEquals 'reports'))";
    private const string TagCond =
        "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Project<$key_case_sensitive$>] StringEquals 'Cascade'))";

    [Fact]
    public void Parse_Null_IsNone() =>
        AzureAbacConditionParser.Parse(null).Kind.Should().Be(AbacKind.None);

    [Fact]
    public void Parse_Path_ReturnsPrefix_TrailingStarStripped()
    {
        AbacResult r = AzureAbacConditionParser.Parse(PathCond);
        r.Kind.Should().Be(AbacKind.PathPrefix);
        r.PathPrefix.Should().Be("readonly/");
    }

    [Fact]
    public void Parse_ContainerName_ReturnsName()
    {
        AbacResult r = AzureAbacConditionParser.Parse(NameCond);
        r.Kind.Should().Be(AbacKind.ContainerName);
        r.ContainerName.Should().Be("reports");
    }

    [Fact]
    public void Parse_Tag_ReturnsKeyValue()
    {
        AbacResult r = AzureAbacConditionParser.Parse(TagCond);
        r.Kind.Should().Be(AbacKind.Tag);
        r.TagKey.Should().Be("Project");
        r.TagValue.Should().Be("Cascade");
        r.TagKeyCaseSensitive.Should().BeTrue();
    }

    [Fact]
    public void Parse_UnknownExpression_IsUnparseable() =>
        AzureAbacConditionParser.Parse("@Request[...] DateTimeGreaterThan '2024-01-01'")
            .Kind.Should().Be(AbacKind.Unparseable);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureAbacConditionParserTests"`
Expected: FAIL — parser does not exist.

- [ ] **Step 3: Write the parser**

```csharp
// src/Connapse.Storage/CloudScope/AzureAbacConditionParser.cs
using System.Text.RegularExpressions;

namespace Connapse.Storage.CloudScope;

public enum AbacKind { None, PathPrefix, ContainerName, Tag, Unparseable }

public record AbacResult(
    AbacKind Kind,
    string? PathPrefix = null,
    string? ContainerName = null,
    string? TagKey = null,
    string? TagValue = null,
    bool TagKeyCaseSensitive = false);

/// <summary>
/// Classifies the canonical Azure Blob ABAC read conditions into a prefix, a container name, a tag
/// predicate, or Unparseable. Only the documented read templates are understood; anything else is
/// Unparseable and the caller drops that grant (fail closed). A null/blank condition is None (an
/// unconditional grant).
/// </summary>
public static partial class AzureAbacConditionParser
{
    [GeneratedRegex(@"blobServices/containers/blobs:path\]\s+String(?:Like|StartsWith)\s+'([^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"blobServices/containers:name\]\s+StringEquals\s+'([^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"blobServices/containers/blobs/tags:([^<\]]+)<\$key_(case_sensitive|case_insensitive)\$>\]\s+StringEquals\s+'([^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    public static AbacResult Parse(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return new AbacResult(AbacKind.None);

        Match tag = TagRegex().Match(condition);
        if (tag.Success)
            return new AbacResult(AbacKind.Tag,
                TagKey: tag.Groups[1].Value,
                TagValue: tag.Groups[3].Value,
                TagKeyCaseSensitive: string.Equals(tag.Groups[2].Value, "case_sensitive", StringComparison.OrdinalIgnoreCase));

        Match path = PathRegex().Match(condition);
        if (path.Success)
        {
            string p = path.Groups[1].Value;
            if (p.EndsWith('*')) p = p[..^1];
            return new AbacResult(AbacKind.PathPrefix, PathPrefix: p);
        }

        Match name = NameRegex().Match(condition);
        if (name.Success)
            return new AbacResult(AbacKind.ContainerName, ContainerName: name.Groups[1].Value);

        return new AbacResult(AbacKind.Unparseable);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureAbacConditionParserTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/AzureAbacConditionParser.cs tests/Connapse.Storage.Tests/CloudScope/AzureAbacConditionParserTests.cs
git commit -m "feat(azure): ABAC blob condition parser (path/name/tag) (#487)"
```

---

## Task 4: `ArmRbacReader` — role assignments → readable scopes (grants only)

**Files:**
- Create: `src/Connapse.Storage/CloudScope/ArmRbacReader.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs`

**Interfaces:**
- Consumes: `IAzureRbacReader`, `AzureRbacScopes`, `AzureScope`, `AzureTagCondition` (Task 1); `AzureRbacScopeTranslator` (Task 2); `AzureAbacConditionParser`/`AbacKind`/`AbacResult` (Task 3); `TokenCredential`; `IMemoryCache`; `IOptionsMonitor<AzureProviderSettings>`.
- Produces: `ArmRbacReader(HttpClient httpClient, TokenCredential azureCredential, IMemoryCache cache, IOptionsMonitor<AzureProviderSettings> options) : IAzureRbacReader`; `public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5)`.

This task builds the grants path (roleAssignments only); Task 5 adds deny subtraction; Task 6 adds caching + DI. The internal apply-condition logic maps each grant `(azblobPrefix, condition)` → prefix / tag-residue / drop.

- [ ] **Step 1: Write the failing tests** (happy path + role filtering + ABAC application + subscription-missing)

```csharp
// tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs
using System.Net;
using System.Text;
using Azure.Core;
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class ArmRbacReaderTests
{
    private const string Oid = "11111111-1111-1111-1111-111111111111";
    private const string Sub = "22222222-2222-2222-2222-222222222222";
    private const string ReaderRole = "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1";

    // Records every request URL so tests can assert which ARM endpoints were called.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) =>
            new("stub", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
            new(GetToken(c, ct));
    }

    private static HttpResponseMessage Json(HttpStatusCode s, string body) =>
        new(s) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string RoleAssignmentsBody(params (string roleGuid, string scope, string? condition)[] rows)
    {
        string items = string.Join(",", rows.Select(r =>
        {
            string cond = r.condition is null ? "null" : $"\"{r.condition.Replace("\"", "\\\"")}\"";
            return $$"""
            {"properties":{"roleDefinitionId":"/subscriptions/{{Sub}}/providers/Microsoft.Authorization/roleDefinitions/{{r.roleGuid}}","principalId":"{{Oid}}","scope":"{{r.scope}}","condition":{{cond}},"conditionVersion":null}}
            """;
        }));
        return $$"""{"value":[{{items}}]}""";
    }

    private static readonly string EmptyDeny = """{"value":[]}""";

    // A reader whose roleAssignments call returns `roles` and whose denyAssignments call returns empty.
    private static ArmRbacReader NewReader(string rolesBody, string? subId = Sub, IMemoryCache? cache = null)
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
                ? Json(HttpStatusCode.OK, EmptyDeny)
                : Json(HttpStatusCode.OK, rolesBody));
        var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
        opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = subId });
        return new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
            cache ?? new MemoryCache(new MemoryCacheOptions()), opts);
    }

    [Fact]
    public async Task Resolve_AccountScopedReaderRole_NoCondition_YieldsAccountPrefix()
    {
        string body = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Select(p => p.Prefix).Should().Contain("azblob://acct/");
    }

    [Fact]
    public async Task Resolve_IgnoresNonBlobDataRoles()
    {
        // Owner of the *control plane* role "Contributor" for ARM is a different GUID; use a random one.
        string body = RoleAssignmentsBody(("00000000-0000-0000-0000-000000000000",
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.Outcome.Should().Be(RbacOutcome.Resolved);
        r.ReadablePrefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task Resolve_PathCondition_NarrowsPrefix()
    {
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'} AND NOT SubOperationMatches{'Blob.List'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs:path] StringLike 'readonly/*'))";
        string body = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", cond));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().Contain("azblob://acct/docs/readonly/");
    }

    [Fact]
    public async Task Resolve_TagCondition_GoesToTagResidue_NotPrefix()
    {
        string cond = "((!(ActionMatches{'Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read'})) OR (@Resource[Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags:Project<$key_case_sensitive$>] StringEquals 'Cascade'))";
        string body = RoleAssignmentsBody((ReaderRole,
            "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", cond));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Should().BeEmpty();
        r.TagConditioned.Should().ContainSingle().Which.TagValue.Should().Be("Cascade");
    }

    [Fact]
    public async Task Resolve_UnparseableCondition_DropsThatGrantOnly()
    {
        string bad = "((!(ActionMatches{'x'})) OR (@Request[foo] DateTimeGreaterThan '2024-01-01'))";
        string body = RoleAssignmentsBody(
            (ReaderRole, "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/bad", bad),
            (ReaderRole, "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/good", null));
        AzureRbacScopes r = await NewReader(body).ResolveAsync(Oid, CancellationToken.None);

        r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/good/");
    }

    [Fact]
    public async Task Resolve_NoSubscriptionConfigured_FailsClosed()
    {
        AzureRbacScopes r = await NewReader(RoleAssignmentsBody(), subId: null).ResolveAsync(Oid, CancellationToken.None);
        r.Outcome.Should().Be(RbacOutcome.Failed);
    }

    [Fact]
    public async Task Resolve_QueriesSubscriptionScope_WithoutAtScope()
    {
        var reader = NewReader(RoleAssignmentsBody());
        await reader.ResolveAsync(Oid, CancellationToken.None);
        // (assert via a handler that captured URLs — see StubHandler.Urls; validated in Task 6's DI test
        //  and here by constructing the reader with a capturing handler if needed)
    }
}
```

Note: add `using NSubstitute;` — the test project already references NSubstitute (see CLAUDE.md). The final URL-assertion test is fleshed out in Task 6 where the capturing handler is wired; keep the placeholder body compiling (it simply resolves).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ArmRbacReaderTests"`
Expected: FAIL — `ArmRbacReader` does not exist.

- [ ] **Step 3: Write the reader (grants path)**

```csharp
// src/Connapse.Storage/CloudScope/ArmRbacReader.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Resolves a searcher's effective RBAC-readable azblob scopes from ARM: one roleAssignments call
/// at the subscription scope (transitive over groups, WITHOUT atScope so account/container
/// assignments are included) minus a parallel denyAssignments call. Fails closed; caches only
/// confident answers. Mirrors <see cref="GraphDirectoryReader"/>'s transport/caching discipline.
/// </summary>
public sealed class ArmRbacReader(
    HttpClient httpClient,
    TokenCredential azureCredential,
    IMemoryCache cache,
    IOptionsMonitor<AzureProviderSettings> options) : IAzureRbacReader
{
    private const string ArmBase = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";
    private static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    private static readonly HashSet<string> BlobDataReadRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1", // Storage Blob Data Reader
        "ba92f5b4-2d11-453d-a403-e96b0029c9fe", // Storage Blob Data Contributor
        "b7e6dc6d-f1e8-4753-8033-0f276bb0955b", // Storage Blob Data Owner
    };

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public async Task<AzureRbacScopes> ResolveAsync(string primaryOid, CancellationToken ct = default)
    {
        string? sub = options.CurrentValue.SubscriptionId;
        if (string.IsNullOrWhiteSpace(sub))
            return AzureRbacScopes.Failed(); // cannot query without a subscription — fail closed

        try
        {
            return await ResolveUncachedAsync(sub, primaryOid, ct); // Task 5/6 add deny + cache
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AzureRbacScopes.Failed();
        }
    }

    private async Task<AzureRbacScopes> ResolveUncachedAsync(string sub, string oid, CancellationToken ct)
    {
        AccessToken token = await azureCredential.GetTokenAsync(new TokenRequestContext(ArmScopes), ct);

        IReadOnlyList<Assignment> grants = await ListAllAsync(
            $"{ArmBase}/subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments" +
            $"?api-version={ApiVersion}&$filter=assignedTo('{oid}')", token, ct);

        var prefixes = new List<AzureScope>();
        var tags = new List<AzureTagCondition>();
        foreach (Assignment a in grants)
        {
            string? roleGuid = LastSegment(a.Properties?.RoleDefinitionId);
            if (roleGuid is null || !BlobDataReadRoles.Contains(roleGuid) || a.Properties?.Scope is null)
                continue;

            ApplyGrant(a.Properties.Scope, a.Properties.Condition, prefixes, tags);
        }

        return AzureRbacScopes.Resolved(prefixes, tags);
    }

    /// <summary>Maps one matched grant (scope + ABAC condition) into a prefix, a tag residue, or a drop.</summary>
    internal static void ApplyGrant(string armScope, string? condition, List<AzureScope> prefixes, List<AzureTagCondition> tags)
    {
        string basePrefix = AzureRbacScopeTranslator.ToAzblobPrefix(armScope);
        AbacResult abac = AzureAbacConditionParser.Parse(condition);
        switch (abac.Kind)
        {
            case AbacKind.None:
                prefixes.Add(new AzureScope(basePrefix));
                break;
            case AbacKind.PathPrefix:
                prefixes.Add(new AzureScope(basePrefix + abac.PathPrefix));
                break;
            case AbacKind.ContainerName:
                // Narrow an account/broader scope to the named container.
                prefixes.Add(new AzureScope(NarrowToContainer(basePrefix, abac.ContainerName!)));
                break;
            case AbacKind.Tag:
                tags.Add(new AzureTagCondition(basePrefix, abac.TagKey!, abac.TagValue!, abac.TagKeyCaseSensitive));
                break;
            case AbacKind.Unparseable:
            default:
                break; // drop this grant only (fail closed)
        }
    }

    private static string NarrowToContainer(string basePrefix, string container)
    {
        // basePrefix is "azblob://", "azblob://{acct}/", or "azblob://{acct}/{c}/". A container-name
        // condition names the container within the account; only meaningful when the account is known.
        if (basePrefix == "azblob://")
            return basePrefix; // account unknown — leave broad; the condition can't be tightened here
        // basePrefix ends with "/"; strip any existing container and append the named one.
        string acct = basePrefix["azblob://".Length..].TrimEnd('/').Split('/')[0];
        return $"azblob://{acct}/{container}/";
    }

    private static string? LastSegment(string? id) =>
        string.IsNullOrEmpty(id) ? null : id.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private async Task<IReadOnlyList<Assignment>> ListAllAsync(string url, AccessToken token, CancellationToken ct)
    {
        var all = new List<Assignment>();
        string? next = url;
        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new("Bearer", token.Token);
            using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode(); // non-2xx → throw → caller fails closed
            ArmList? page = await response.Content.ReadFromJsonAsync<ArmList>(ct);
            if (page?.Value is { } v) all.AddRange(v);
            next = page?.NextLink;
        }
        return all;
    }

    // ---- ARM DTOs ----
    internal sealed record ArmList(
        [property: JsonPropertyName("value")] IReadOnlyList<Assignment>? Value,
        [property: JsonPropertyName("nextLink")] string? NextLink);
    internal sealed record Assignment([property: JsonPropertyName("properties")] AssignmentProps? Properties);
    internal sealed record AssignmentProps(
        [property: JsonPropertyName("roleDefinitionId")] string? RoleDefinitionId,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("condition")] string? Condition);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ArmRbacReaderTests"`
Expected: PASS (the URL-assertion placeholder test resolves; deny/cache tests come in 5/6).

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/ArmRbacReader.cs tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs
git commit -m "feat(azure): ArmRbacReader resolves readable scopes from role assignments (#487)"
```

---

## Task 5: Deny assignments — effective = grants − denies

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/ArmRbacReader.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs` (extend)

**Interfaces:**
- Consumes: the grants path (Task 4). Adds a parallel deny fetch and a conservative subtraction.

**Deny model (conservative, sound):** fetch `denyAssignments` at the same subscription scope with `assignedTo('{oid}')`. A deny assignment carries `properties.scope` and `properties.permissions[].dataActions`/`notDataActions`. Treat a deny as applicable to blob read when any `dataActions` entry is the blob read action or a wildcard covering it (`Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read`, or `.../blobs/*`, or `*`). For each applicable deny, remove any grant prefix that the deny's scope **covers** — i.e. the deny's azblob prefix (via `AzureRbacScopeTranslator`) is a prefix of, or equal to, the grant prefix. (Deny scope broader than grant ⇒ grant removed; this is deny-wins. Tag-conditioned residue whose scope is covered is likewise dropped.) A failed deny call throws → the outer catch returns `Failed` (never assume none).

- [ ] **Step 1: Write the failing tests**

```csharp
// append to ArmRbacReaderTests

// Build a reader whose deny call returns `denyBody`.
private static ArmRbacReader NewReaderWithDeny(string rolesBody, string denyBody, string? subId = Sub)
{
    var handler = new StubHandler(req =>
        req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
            ? Json(HttpStatusCode.OK, denyBody)
            : Json(HttpStatusCode.OK, rolesBody));
    var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
    opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = subId });
    return new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
        new MemoryCache(new MemoryCacheOptions()), opts);
}

private static string DenyBody(string scope) => $$"""
{"value":[{"properties":{"scope":"{{scope}}","permissions":[{"dataActions":["Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read"],"notDataActions":[]}]}}]}
""";

[Fact]
public async Task Resolve_DenyCoveringGrant_RemovesIt()
{
    string acctScope = "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct";
    string roles = RoleAssignmentsBody((ReaderRole, acctScope + "/blobServices/default/containers/docs", null));
    // Deny at the whole account covers the container grant.
    AzureRbacScopes r = await NewReaderWithDeny(roles, DenyBody(acctScope)).ResolveAsync(Oid, CancellationToken.None);

    r.Outcome.Should().Be(RbacOutcome.Resolved);
    r.ReadablePrefixes.Should().BeEmpty(); // deny wins
}

[Fact]
public async Task Resolve_DenyElsewhere_DoesNotRemoveUnrelatedGrant()
{
    string roles = RoleAssignmentsBody((ReaderRole,
        "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct/blobServices/default/containers/docs", null));
    string denyOther = DenyBody("/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/other");
    AzureRbacScopes r = await NewReaderWithDeny(roles, denyOther).ResolveAsync(Oid, CancellationToken.None);

    r.ReadablePrefixes.Select(p => p.Prefix).Should().ContainSingle().Which.Should().Be("azblob://acct/docs/");
}

[Fact]
public async Task Resolve_DenyCallFails_FailsClosed()
{
    var handler = new StubHandler(req =>
        req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
            ? Json(HttpStatusCode.InternalServerError, "{}")
            : Json(HttpStatusCode.OK, RoleAssignmentsBody((ReaderRole,
                "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null))));
    var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
    opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
    var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
        new MemoryCache(new MemoryCacheOptions()), opts);

    (await reader.ResolveAsync(Oid, CancellationToken.None)).Outcome.Should().Be(RbacOutcome.Failed);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ArmRbacReaderTests"`
Expected: FAIL (deny not yet fetched/subtracted).

- [ ] **Step 3: Add deny fetch + subtraction**

Replace `ResolveUncachedAsync` so it fetches deny in parallel and subtracts, and add the helpers:

```csharp
    private async Task<AzureRbacScopes> ResolveUncachedAsync(string sub, string oid, CancellationToken ct)
    {
        AccessToken token = await azureCredential.GetTokenAsync(new TokenRequestContext(ArmScopes), ct);

        Task<IReadOnlyList<Assignment>> grantsTask = ListAllAsync(
            $"{ArmBase}/subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments" +
            $"?api-version={ApiVersion}&$filter=assignedTo('{oid}')", token, ct);
        Task<IReadOnlyList<Assignment>> denyTask = ListAllAsync(
            $"{ArmBase}/subscriptions/{sub}/providers/Microsoft.Authorization/denyAssignments" +
            $"?api-version={ApiVersion}&$filter=assignedTo('{oid}')", token, ct);
        await Task.WhenAll(grantsTask, denyTask); // a failure in either throws → caller fails closed

        var prefixes = new List<AzureScope>();
        var tags = new List<AzureTagCondition>();
        foreach (Assignment a in grantsTask.Result)
        {
            string? roleGuid = LastSegment(a.Properties?.RoleDefinitionId);
            if (roleGuid is null || !BlobDataReadRoles.Contains(roleGuid) || a.Properties?.Scope is null)
                continue;
            ApplyGrant(a.Properties.Scope, a.Properties.Condition, prefixes, tags);
        }

        IReadOnlyList<string> denyPrefixes = DenyPrefixes(denyTask.Result);
        if (denyPrefixes.Count > 0)
        {
            prefixes = prefixes.Where(p => !CoveredByAnyDeny(p.Prefix, denyPrefixes)).ToList();
            tags = tags.Where(t => !CoveredByAnyDeny(t.Scope, denyPrefixes)).ToList();
        }

        return AzureRbacScopes.Resolved(prefixes, tags);
    }

    /// <summary>Deny scopes (as azblob prefixes) that apply to blob read for this searcher.</summary>
    private static IReadOnlyList<string> DenyPrefixes(IReadOnlyList<Assignment> denies)
    {
        var result = new List<string>();
        foreach (Assignment d in denies)
        {
            if (d.Properties?.Scope is null) continue;
            if (AppliesToBlobRead(d.Properties.Permissions))
                result.Add(AzureRbacScopeTranslator.ToAzblobPrefix(d.Properties.Scope));
        }
        return result;
    }

    private static bool AppliesToBlobRead(IReadOnlyList<Permission>? permissions)
    {
        if (permissions is null) return false;
        foreach (Permission p in permissions)
        {
            foreach (string a in p.DataActions ?? [])
            {
                if (a == "*"
                    || a.Equals("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", StringComparison.OrdinalIgnoreCase)
                    || (a.EndsWith('*') && "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read"
                            .StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    // A grant is denied when a deny prefix is equal to, or an ancestor of, the grant prefix
    // (deny-wins over a broader-or-equal scope). "azblob://" as a deny prefix covers everything.
    private static bool CoveredByAnyDeny(string grantPrefix, IReadOnlyList<string> denyPrefixes) =>
        denyPrefixes.Any(d => grantPrefix.StartsWith(d, StringComparison.Ordinal));
```

Add the deny DTO fields to `AssignmentProps` and a `Permission` record:

```csharp
    internal sealed record AssignmentProps(
        [property: JsonPropertyName("roleDefinitionId")] string? RoleDefinitionId,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("condition")] string? Condition,
        [property: JsonPropertyName("permissions")] IReadOnlyList<Permission>? Permissions);
    internal sealed record Permission(
        [property: JsonPropertyName("dataActions")] IReadOnlyList<string>? DataActions,
        [property: JsonPropertyName("notDataActions")] IReadOnlyList<string>? NotDataActions);
```

(`roleAssignments` responses simply have no `permissions` field → null → ignored. `denyAssignments` responses have no `roleDefinitionId` → not matched as a grant.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ArmRbacReaderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/ArmRbacReader.cs tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs
git commit -m "feat(azure): subtract deny assignments from RBAC grants (deny wins) (#487)"
```

---

## Task 6: Caching + DI wiring + resolution test

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/ArmRbacReader.cs` (add caching)
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs` (caching + URL assertion), `tests/Connapse.Integration.Tests/AzureRbacReaderDiIntegrationTests.cs`

**Interfaces:**
- Consumes: everything above; `ConnapseAzureCredentials` (already a singleton, mapped to `TokenCredential` in 4a).

- [ ] **Step 1: Add caching to `ResolveAsync`**

Wrap the resolve in a per-oid cache (confident answers only), mirroring `GraphDirectoryReader`:

```csharp
    public async Task<AzureRbacScopes> ResolveAsync(string primaryOid, CancellationToken ct = default)
    {
        string? sub = options.CurrentValue.SubscriptionId;
        if (string.IsNullOrWhiteSpace(sub))
            return AzureRbacScopes.Failed();

        string cacheKey = "azure-rbac:" + primaryOid;
        if (cache.TryGetValue(cacheKey, out AzureRbacScopes? cached) && cached is not null)
            return cached;

        AzureRbacScopes result;
        try
        {
            result = await ResolveUncachedAsync(sub, primaryOid, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AzureRbacScopes.Failed();
        }

        if (result.Outcome is RbacOutcome.Resolved)
            cache.Set(cacheKey, result, CacheLifetime);
        return result;
    }
```

- [ ] **Step 2: Write caching + URL-assertion tests**

```csharp
// append to ArmRbacReaderTests
[Fact]
public async Task Resolve_Resolved_IsCached_SecondCallDoesNotHitArm()
{
    string roles = RoleAssignmentsBody((ReaderRole,
        "/subscriptions/" + Sub + "/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/acct", null));
    var handler = new StubHandler(req =>
        req.RequestUri!.AbsolutePath.Contains("/denyAssignments", StringComparison.OrdinalIgnoreCase)
            ? Json(HttpStatusCode.OK, EmptyDeny) : Json(HttpStatusCode.OK, roles));
    var opts = Substitute.For<IOptionsMonitor<AzureProviderSettings>>();
    opts.CurrentValue.Returns(new AzureProviderSettings { SubscriptionId = Sub });
    var reader = new ArmRbacReader(new HttpClient(handler), new StubTokenCredential(),
        new MemoryCache(new MemoryCacheOptions()), opts);

    await reader.ResolveAsync(Oid, CancellationToken.None);
    int after = handler.Urls.Count;
    await reader.ResolveAsync(Oid, CancellationToken.None);

    handler.Urls.Count.Should().Be(after); // served from cache
    handler.Urls.Should().Contain(u => u.Contains("/roleAssignments") && u.Contains("assignedTo") && !u.Contains("atScope"));
    handler.Urls.Should().Contain(u => u.Contains("/subscriptions/" + Sub + "/"));
}
```

- [ ] **Step 3: DI registration** — after the 4a `IAzureDirectoryReader` registration in `AddConnapseStorage`, add:

```csharp
        // Reads the searcher's effective RBAC-readable azblob scopes from ARM (role assignments
        // minus deny assignments). Typed HttpClient; the 5-minute decision cache is the shared
        // IMemoryCache singleton. TokenCredential is already mapped to ConnapseAzureCredentials (4a).
        services.AddHttpClient<Connapse.Core.Interfaces.IAzureRbacReader, CloudScope.ArmRbacReader>();
```

- [ ] **Step 4: Write the DI resolution test**

```csharp
// tests/Connapse.Integration.Tests/AzureRbacReaderDiIntegrationTests.cs
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AzureRbacReaderDiIntegrationTests(SharedWebAppFixture fixture)
{
    [Fact]
    public void Di_Resolves_AzureRbacReader()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAzureRbacReader>().Should().NotBeNull();
    }
}
```

- [ ] **Step 5: Build, run tests, commit**

Run: `dotnet build` then `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~ArmRbacReaderTests|FullyQualifiedName~AzureRbacScopeTranslatorTests|FullyQualifiedName~AzureAbacConditionParserTests"` and the DI test `dotnet test tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj --filter "FullyQualifiedName~AzureRbacReaderDiIntegrationTests"` (needs Docker).
Expected: all green.

```bash
git add src/Connapse.Storage/CloudScope/ArmRbacReader.cs src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs tests/Connapse.Storage.Tests/CloudScope/ArmRbacReaderTests.cs tests/Connapse.Integration.Tests/AzureRbacReaderDiIntegrationTests.cs
git commit -m "feat(azure): cache RBAC scopes + register ArmRbacReader (#487)"
```

---

## Self-Review notes

- **Spec §B coverage:** subscription-scope query without `atScope()` + transitive `assignedTo` (Task 4, corrects the parent design); nextLink paging (Task 4); 3 role GUIDs (Task 4); scope→azblob translation incl. broader→`azblob://` (Task 2); ABAC path/name/tag/unparseable (Task 3, applied Task 4); deny in parallel, effective = grants − denies, deny-call-fail → Failed (Task 5); cache ~5 min confident-only + `azure-rbac:{oid}` (Task 6); fail-closed on no-subscription/HTTP-fail/timeout (Tasks 4, 6). ✅
- **Not in 4b (deferred):** search wiring / composite / enforcement (4c); Gen2 (4d/4e); the settings-based tenant-match guard (4a follow-up).
- **Type consistency:** `AzureRbacScopes`/`AzureScope`/`AzureTagCondition`/`RbacOutcome`/`IAzureRbacReader.ResolveAsync(string, ct)` identical across tasks; `AzureRbacScopeTranslator.ToAzblobPrefix`, `AzureAbacConditionParser.Parse`, `AbacResult`/`AbacKind` referenced consistently; `ArmRbacReader` ctor and `CacheLifetime` stable.
- **Fail-closed:** every non-Resolved path returns `Failed()` (empty); deny-call failure fails closed; unparseable condition drops one grant, not the resolution.
