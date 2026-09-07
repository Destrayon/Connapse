# Azure Phase 4e — Gen2 live per-hit verify seam + tag verify Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the Azure permission engine into live search: a post-retrieval `ISearchResultVerifier` that admits every readable Azure hit and drops every unreadable one, completing Phase 4 (on merge, the epic → `main`).

**Architecture:** `AzureSearchScopeResolver` broadens Azure retrieval to the scheme wildcard `azblob://` (retrieve every candidate by relevance) for a valid enforcing identity. After retrieval + rerank, `HybridSearchService` calls `ISearchResultVerifier`, which routes each `azblob://` hit: covered by an RBAC prefix → pass (in-memory, RBAC supersedes ACLs); under a tag condition → live blob-tag verify; otherwise → live Gen2 file-ACL (Read) + 4d ancestor-traverse (Execute). Fail closed; `s3://` and non-cloud hits pass untouched. Over-fetch + backfill keeps the page full; bounded parallelism bounds cost.

**Tech Stack:** .NET 10, C#; `Azure.Storage.Blobs` (tags), `Azure.Storage.Files.DataLake` (file ACL); `Microsoft.Extensions.Caching.Memory`; xUnit + FluentAssertions + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-09-06-azure-phase4-permission-engine-design.md` (§E and its 2026-09-07 amendment)

## Global Constraints

- **Broad retrieve-then-verify (§E amendment).** For a valid enforcing Azure identity, `AzureSearchScopeResolver` emits the single broad match `azblob://`; all Azure tightening is the verifier's job. No HNS detection, **no new Azure permission** (app stays `Storage Blob Data Reader` + the existing RBAC-read).
- **The verifier acts ONLY on `azblob://` hits.** `s3://` and non-cloud (`resource_uri` NULL) hits pass through untouched — the AWS path is unchanged.
- **Fail closed everywhere.** Any unreadable directory/file, tag-read failure, SDK error, missing link, deprovisioned/uncertain identity → drop that hit (never admit). A flat-account Gen2 ACL read fails → drop.
- **Live only.** No permission persistence. RBAC/identity are resolved live and cached (~5 min, existing readers); per-hit ACL/tag reads cached for the query and ~30–120 s. Freshness = cache TTL.
- **Routing precedence per `azblob://` hit:** (1) covered by any RBAC `ReadablePrefix` (exact or prefix) → **pass**, no live call; (2) else under a `TagConditioned` scope → **tag verify**; (3) else → **Gen2 verify** file ACL Read + ancestor-traverse Execute. Reading a Gen2 file requires **both** the file's own Read AND traverse-X on every ancestor (4d) — folder access is never trusted.
- **Connapse reads only** — never writes a grant/role/ACL/tag.
- **AWS unchanged:** no file under `AwsSearchScopeResolver`, `S3*`, `RolesAnywhere/`, or the SAML/JWT path is modified. `PgVectorStore`/`KeywordSearchService` SQL filters are not modified (retrieval breadth changes only via the resolver's `SearchScopes`).

## File Structure

**Create (Core):**
- `Models/AzblobUri.cs` — parse `azblob://account/container/path` → components / `Gen2Path`.
- `CloudScope/AzureTagConditionEvaluator.cs` — pure blob-tag ↔ `AzureTagCondition` match.
- `Interfaces/IGen2FileAclReader.cs`, `Interfaces/IBlobTagReader.cs`, `Interfaces/ISearchResultVerifier.cs`.

**Create (Storage):**
- `CloudScope/BlobTagReader.cs` — `IBlobTagReader` over `Azure.Storage.Blobs`.
- `CloudScope/AzureSearchResultVerifier.cs` — the routing verifier.
- `CloudScope/NoOpSearchResultVerifier.cs` — pass-through default.

**Modify:**
- `src/Connapse.Storage/CloudScope/DataLakeGen2DirectoryReader.cs` — also implement `IGen2FileAclReader` (file-ACL read).
- `src/Connapse.Core/Interfaces/IDocumentStore.cs` + its implementation — add `GetResourceUrisAsync`.
- `src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs` — emit broad `azblob://` for a valid enforcing identity.
- `src/Connapse.Search/Hybrid/HybridSearchService.cs` — over-fetch, call the verifier, backfill.
- `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs` — register the verifier + new seams.

**Create (tests):** one test file per new type under the mirroring `tests/*` path, plus an integration test `tests/Connapse.Integration.Tests/AzureVerifyEnforcementTests.cs`.

---

### Task 1: Azblob URI parsing (Core)

**Files:**
- Create: `src/Connapse.Core/Models/AzblobUri.cs`
- Test: `tests/Connapse.Core.Tests/Models/AzblobUriTests.cs`

**Interfaces:**
- Produces: `static bool AzblobUri.TryParse(string? resourceUri, out Gen2Path path)` — true only for a well-formed `azblob://account/container/blobpath` (account + container + non-empty path); `path` = `Gen2Path(account, container, blobpath)`. `static bool AzblobUri.IsAzblob(string? resourceUri)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Models;

[Trait("Category", "Unit")]
public class AzblobUriTests
{
    [Theory]
    [InlineData("azblob://acct/docs/a/b/file.txt", "acct", "docs", "a/b/file.txt")]
    [InlineData("azblob://acct/docs/file.txt", "acct", "docs", "file.txt")]
    public void TryParse_WellFormed_ReturnsComponents(string uri, string acct, string fs, string path)
    {
        AzblobUri.TryParse(uri, out Gen2Path p).Should().BeTrue();
        p.Account.Should().Be(acct);
        p.FileSystem.Should().Be(fs);
        p.Path.Should().Be(path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3://bucket/key")]
    [InlineData("azblob://acct")]          // no container or path
    [InlineData("azblob://acct/docs")]     // no blob path
    [InlineData("azblob://acct/docs/")]    // empty blob path
    public void TryParse_MalformedOrNonAzblob_ReturnsFalse(string? uri) =>
        AzblobUri.TryParse(uri, out _).Should().BeFalse();

    [Fact]
    public void IsAzblob_MatchesSchemeOnly()
    {
        AzblobUri.IsAzblob("azblob://a/c/x").Should().BeTrue();
        AzblobUri.IsAzblob("s3://b/k").Should().BeFalse();
        AzblobUri.IsAzblob(null).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AzblobUriTests"`
Expected: FAIL — `AzblobUri` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Connapse.Core;

/// <summary>Parses <c>azblob://account/container/blobpath</c> resource URIs into a
/// <see cref="Gen2Path"/> (container = filesystem). The blob path is required and kept verbatim.</summary>
public static class AzblobUri
{
    private const string Scheme = "azblob://";

    public static bool IsAzblob(string? resourceUri) =>
        resourceUri is not null && resourceUri.StartsWith(Scheme, StringComparison.Ordinal);

    public static bool TryParse(string? resourceUri, out Gen2Path path)
    {
        path = new Gen2Path("", "", "");
        if (!IsAzblob(resourceUri))
            return false;

        string rest = resourceUri![Scheme.Length..];
        int firstSlash = rest.IndexOf('/');
        if (firstSlash <= 0)
            return false; // no container
        int secondSlash = rest.IndexOf('/', firstSlash + 1);
        if (secondSlash < 0 || secondSlash == rest.Length - 1)
            return false; // no container/path boundary, or empty blob path

        string account = rest[..firstSlash];
        string container = rest[(firstSlash + 1)..secondSlash];
        string blobPath = rest[(secondSlash + 1)..];
        if (account.Length == 0 || container.Length == 0 || blobPath.Length == 0)
            return false;

        path = new Gen2Path(account, container, blobPath);
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AzblobUriTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/AzblobUri.cs tests/Connapse.Core.Tests/Models/AzblobUriTests.cs
git commit -m "feat(azure): azblob:// URI parser (#490)"
```

---

### Task 2: Tag-condition evaluator (Core)

**Files:**
- Create: `src/Connapse.Core/CloudScope/AzureTagConditionEvaluator.cs`
- Test: `tests/Connapse.Core.Tests/CloudScope/AzureTagConditionEvaluatorTests.cs`

**Interfaces:**
- Consumes: `AzureTagCondition(string Scope, string TagKey, string TagValue, bool KeyCaseSensitive, bool ValueCaseSensitive)` (Connapse.Core, from 4b).
- Produces: `static bool AzureTagConditionEvaluator.Matches(AzureTagCondition condition, IReadOnlyDictionary<string,string> blobTags)` — the blob's index tags satisfy the condition. Key match honors `KeyCaseSensitive`; value match honors `ValueCaseSensitive`. A missing key → false (fail closed).

- [ ] **Step 1: Write the failing test**

```csharp
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.CloudScope;

[Trait("Category", "Unit")]
public class AzureTagConditionEvaluatorTests
{
    private static AzureTagCondition Cond(bool keyCase = true, bool valueCase = false) =>
        new("azblob://acct/docs/", "Project", "Cascade", keyCase, valueCase);

    [Fact]
    public void Matches_KeyAndValuePresent_True() =>
        AzureTagConditionEvaluator.Matches(Cond(), new Dictionary<string, string> { ["Project"] = "Cascade" })
            .Should().BeTrue();

    [Fact]
    public void Matches_MissingKey_False() =>
        AzureTagConditionEvaluator.Matches(Cond(), new Dictionary<string, string> { ["Other"] = "Cascade" })
            .Should().BeFalse();

    [Fact]
    public void Matches_KeyCaseSensitive_DoesNotMatchDifferentCaseKey() =>
        AzureTagConditionEvaluator.Matches(Cond(keyCase: true), new Dictionary<string, string> { ["project"] = "Cascade" })
            .Should().BeFalse();

    [Fact]
    public void Matches_ValueCaseInsensitive_MatchesDifferentCaseValue() =>
        AzureTagConditionEvaluator.Matches(Cond(valueCase: false), new Dictionary<string, string> { ["Project"] = "cascade" })
            .Should().BeTrue();

    [Fact]
    public void Matches_ValueCaseSensitive_DoesNotMatchDifferentCaseValue() =>
        AzureTagConditionEvaluator.Matches(Cond(valueCase: true), new Dictionary<string, string> { ["Project"] = "cascade" })
            .Should().BeFalse();

    [Fact]
    public void Matches_WrongValue_False() =>
        AzureTagConditionEvaluator.Matches(Cond(), new Dictionary<string, string> { ["Project"] = "Other" })
            .Should().BeFalse();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AzureTagConditionEvaluatorTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Connapse.Core;

/// <summary>
/// Evaluates a blob's index tags against an <see cref="AzureTagCondition"/> (an RBAC ABAC grant that
/// could not reduce to a prefix, carried as residue by 4b). Pure; used by the Phase 4e verifier per
/// hit. A missing key fails closed.
/// </summary>
public static class AzureTagConditionEvaluator
{
    public static bool Matches(AzureTagCondition condition, IReadOnlyDictionary<string, string> blobTags)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(blobTags);

        string? value = FindValue(condition.TagKey, condition.KeyCaseSensitive, blobTags);
        if (value is null)
            return false;

        StringComparison valueCmp = condition.ValueCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(value, condition.TagValue, valueCmp);
    }

    private static string? FindValue(string key, bool keyCaseSensitive, IReadOnlyDictionary<string, string> tags)
    {
        if (keyCaseSensitive)
            return tags.TryGetValue(key, out string? v) ? v : null;

        foreach (KeyValuePair<string, string> t in tags)
            if (string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
                return t.Value;
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --filter "FullyQualifiedName~AzureTagConditionEvaluatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/CloudScope/AzureTagConditionEvaluator.cs tests/Connapse.Core.Tests/CloudScope/AzureTagConditionEvaluatorTests.cs
git commit -m "feat(azure): blob-tag condition evaluator (#490)"
```

---

### Task 3: Gen2 file-ACL read seam (Core interface + Storage)

**Files:**
- Create: `src/Connapse.Core/Interfaces/IGen2FileAclReader.cs`
- Modify: `src/Connapse.Storage/CloudScope/DataLakeGen2DirectoryReader.cs` (also implement the new interface)
- Test: `tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs` (the mapper is already covered; no new mapper logic — the file path reuses `MapAccessControl` + `IsStructurallyComplete`).

**Interfaces:**
- Consumes: `Gen2Acl`, `Gen2Path` (Core); the existing `MapAccessControl`/`IsStructurallyComplete` (4d).
- Produces: `interface IGen2FileAclReader { Task<Gen2Acl?> ReadFileAclAsync(Gen2Path file, CancellationToken ct = default); }`; `DataLakeGen2DirectoryReader` implements it too.

- [ ] **Step 1: Add the interface**

```csharp
// src/Connapse.Core/Interfaces/IGen2FileAclReader.cs
using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Reads a single ADLS Gen2 <b>file's own</b> access ACL, with Connapse's Data-Reader identity, to
/// evaluate a searcher's Read on the leaf (the ancestor-traverse resolver checks only directories).
/// Returns <c>null</c> when the file cannot be read or the ACL is structurally incomplete — an
/// uncertain answer the caller treats as fail-closed. On a non-HNS (flat) account the underlying
/// call fails and this returns <c>null</c>, which is the correct drop for a flat blob reached only
/// by the broad retrieval over-approximation.
/// </summary>
public interface IGen2FileAclReader
{
    Task<Gen2Acl?> ReadFileAclAsync(Gen2Path file, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing test (interface implemented by the reader)**

Add to `tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs`:

```csharp
[Fact]
public void Reader_Implements_FileAclReader() =>
    typeof(Connapse.Core.Interfaces.IGen2FileAclReader)
        .IsAssignableFrom(typeof(DataLakeGen2DirectoryReader)).Should().BeTrue();
```

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~DataLakeGen2DirectoryReaderMappingTests"`
Expected: FAIL — the reader does not implement `IGen2FileAclReader` yet (compile error).

- [ ] **Step 3: Implement `ReadFileAclAsync` on `DataLakeGen2DirectoryReader`**

Change the class declaration to also implement the interface, and add the method (mirrors `ReadAccessAclAsync` but uses a `DataLakeFileClient`):

```csharp
public sealed class DataLakeGen2DirectoryReader(TokenCredential credential)
    : IGen2DirectoryReader, IGen2FileAclReader
{
    // ... existing members ...

    public async Task<Gen2Acl?> ReadFileAclAsync(Gen2Path file, CancellationToken ct = default)
    {
        try
        {
            var service = new DataLakeServiceClient(
                new Uri($"https://{file.Account}.dfs.core.windows.net"), credential);
            DataLakeFileSystemClient fs = service.GetFileSystemClient(file.FileSystem);
            DataLakeFileClient fileClient = fs.GetFileClient(file.Path);
            Response<PathAccessControl> ac = await fileClient.GetAccessControlAsync(cancellationToken: ct);
            Gen2Acl acl = MapAccessControl(ac.Value.Owner, ac.Value.Group, ac.Value.AccessControlList);
            return IsStructurallyComplete(acl) ? acl : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~DataLakeGen2DirectoryReaderMappingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Interfaces/IGen2FileAclReader.cs src/Connapse.Storage/CloudScope/DataLakeGen2DirectoryReader.cs tests/Connapse.Storage.Tests/CloudScope/DataLakeGen2DirectoryReaderMappingTests.cs
git commit -m "feat(azure): Gen2 file-ACL read seam (#490)"
```

---

### Task 4: Blob-tag read seam (Core interface + Storage)

**Files:**
- Create: `src/Connapse.Core/Interfaces/IBlobTagReader.cs`
- Create: `src/Connapse.Storage/CloudScope/BlobTagReader.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/BlobTagReaderTests.cs`

**Interfaces:**
- Consumes: `Gen2Path` (account/container/path is the same shape for a flat blob), `Azure.Core.TokenCredential`.
- Produces: `interface IBlobTagReader { Task<IReadOnlyDictionary<string,string>?> ReadTagsAsync(Gen2Path blob, CancellationToken ct = default); }`; `BlobTagReader : IBlobTagReader`. Returns `null` on failure (fail closed); an empty dictionary means "no tags" (a valid answer → tag conditions won't match → drop).

- [ ] **Step 1: Add the interface**

```csharp
// src/Connapse.Core/Interfaces/IBlobTagReader.cs
using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>Reads a blob's index tags (with Connapse's Data-Reader identity) for live ABAC tag
/// verification. <c>null</c> on any read failure (fail closed); an empty map is a valid "no tags".</summary>
public interface IBlobTagReader
{
    Task<IReadOnlyDictionary<string, string>?> ReadTagsAsync(Gen2Path blob, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing test**

The `BlobClient` is sealed-ish but its calls can be stubbed via a custom `BlobClientOptions.Transport`. To keep the test a pure unit test, isolate the mapping in an `internal static` helper and test that; the thin client shell is exercised by the integration test in Task 8.

```csharp
using Connapse.Core;
using Connapse.Storage.CloudScope;
using FluentAssertions;

namespace Connapse.Storage.Tests.CloudScope;

[Trait("Category", "Unit")]
public class BlobTagReaderTests
{
    [Fact]
    public void MapTags_CopiesAllPairs()
    {
        IReadOnlyDictionary<string, string> result = BlobTagReader.MapTags(
            new Dictionary<string, string> { ["Project"] = "Cascade", ["Env"] = "prod" });
        result.Should().HaveCount(2);
        result["Project"].Should().Be("Cascade");
        result["Env"].Should().Be("prod");
    }

    [Fact]
    public void MapTags_Null_ReturnsEmpty() =>
        BlobTagReader.MapTags(null).Should().BeEmpty();
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~BlobTagReaderTests"`
Expected: FAIL — `BlobTagReader` does not exist.

- [ ] **Step 4: Write minimal implementation**

```csharp
// src/Connapse.Storage/CloudScope/BlobTagReader.cs
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>Reads a blob's index tags over <c>Azure.Storage.Blobs</c>. Thin shell; fail-closed
/// (<c>null</c>) on any error, genuine caller cancellation propagates.</summary>
public sealed class BlobTagReader(TokenCredential credential) : IBlobTagReader
{
    public async Task<IReadOnlyDictionary<string, string>?> ReadTagsAsync(Gen2Path blob, CancellationToken ct = default)
    {
        try
        {
            var service = new BlobServiceClient(
                new Uri($"https://{blob.Account}.blob.core.windows.net"), credential);
            BlobClient client = service.GetBlobContainerClient(blob.FileSystem).GetBlobClient(blob.Path);
            Response<GetBlobTagResult> tags = await client.GetTagsAsync(cancellationToken: ct);
            return MapTags(tags.Value?.Tags);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> MapTags(IDictionary<string, string>? tags) =>
        tags is null ? new Dictionary<string, string>() : new Dictionary<string, string>(tags);
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~BlobTagReaderTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Core/Interfaces/IBlobTagReader.cs src/Connapse.Storage/CloudScope/BlobTagReader.cs tests/Connapse.Storage.Tests/CloudScope/BlobTagReaderTests.cs
git commit -m "feat(azure): blob-tag read seam (#490)"
```

---

### Task 5: Bulk resource_uri lookup (Core interface + Storage)

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IDocumentStore.cs` (add the method)
- Modify: the `IDocumentStore` implementation (find it: `grep -rl "class .*DocumentStore" src/Connapse.Storage`)
- Test: `tests/Connapse.Storage.Tests/...` (an integration-tagged test if the impl needs a DbContext; otherwise a focused unit test). Because this needs the real store, put the covering test in `tests/Connapse.Integration.Tests/` tagged `Category=Integration`, OR — preferred — verify it in Task 8's integration test and here add only the interface + a mapping check. Follow whichever the existing `IDocumentStore` tests do.

**Interfaces:**
- Produces: `Task<IReadOnlyDictionary<string, string?>> GetResourceUrisAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default)` — maps each requested document id to its `resource_uri` (null when the document has none or is not found). One query, not N.

- [ ] **Step 1: Add the interface method**

In `src/Connapse.Core/Interfaces/IDocumentStore.cs`:

```csharp
/// <summary>
/// The <c>resource_uri</c> of each requested document (null when it has none — uploads and non-cloud
/// connectors — or the id is unknown). One batched lookup, for the search verifier which must map
/// ranked hits back to their governing URIs.
/// </summary>
Task<IReadOnlyDictionary<string, string?>> GetResourceUrisAsync(
    IReadOnlyCollection<string> documentIds, CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing test**

Match the existing `IDocumentStore` test style. If the store is EF/Npgsql-backed (integration), add to a suitable integration test class; assert that for a set of document ids the returned map has the seeded `resource_uri`s and `null` for an unknown id. (Concrete seeding mirrors `AzureFlatEnforcementTests.SeedAsync` — a document with `ResourceUri = "azblob://acct/docs/a"` and one with `ResourceUri = null`.) Run it and confirm it fails to compile (method missing) then fails red.

- [ ] **Step 3: Implement in the store**

Use a single parameterized query. Example shape (adapt to the store's EF context / raw-SQL idiom already used in `PgVectorStore`):

```csharp
public async Task<IReadOnlyDictionary<string, string?>> GetResourceUrisAsync(
    IReadOnlyCollection<string> documentIds, CancellationToken ct = default)
{
    var result = new Dictionary<string, string?>();
    if (documentIds.Count == 0) return result;

    Guid[] ids = documentIds.Select(Guid.Parse).ToArray();
    await using var db = await _factory.CreateDbContextAsync(ct);
    var rows = await db.Documents
        .Where(d => ids.Contains(d.Id))
        .Select(d => new { d.Id, d.ResourceUri })
        .ToListAsync(ct);
    foreach (var r in rows)
        result[r.Id.ToString()] = r.ResourceUri;
    return result;
}
```

(Use the exact `DbContext`/entity names the store already uses — `KnowledgeDbContext`, `Documents`, `DocumentEntity.ResourceUri`, `Id`. Never string-interpolate SQL.)

- [ ] **Step 4: Run the test to verify it passes**

Run the added test's filter. Expected: PASS (seeded URIs returned; unknown id → null/absent).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(search): bulk resource_uri lookup on the document store (#490)"
```

---

### Task 6: The search-result verifier (Core interface + Storage impl + no-op)

**Files:**
- Create: `src/Connapse.Core/Interfaces/ISearchResultVerifier.cs`
- Create: `src/Connapse.Storage/CloudScope/NoOpSearchResultVerifier.cs`
- Create: `src/Connapse.Storage/CloudScope/AzureSearchResultVerifier.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureSearchResultVerifierTests.cs`

**Interfaces:**
- Consumes: `SearchHit(ChunkId, DocumentId, Content, Score, Metadata)` (Core); `IDocumentStore.GetResourceUrisAsync` (Task 5); `IAzureIdentityLinkReader`→`AzureIdentityRef(ObjectId, TenantId)`; `IAzureDirectoryReader`→`AzureIdentitySet(PrincipalOids, Outcome)`; `IAzureRbacReader`→`AzureRbacScopes(ReadablePrefixes[AzureScope(Prefix)], TagConditioned[AzureTagCondition], Outcome)`; `IGen2FileAclReader.ReadFileAclAsync`; `AncestorTraverseResolver.HoldsTraverseOnAllAncestorsAsync(Gen2Path, string userOid, IReadOnlySet<string> groupOids, ct)`; `IBlobTagReader.ReadTagsAsync`; `PosixAclEvaluator.Grants(Gen2Acl, string userOid, IReadOnlySet<string> groupOids, Gen2Permission)`; `AzureTagConditionEvaluator.Matches`; `AzblobUri.TryParse`; `PermissionEnforcementSettings.StateForAzure`; `AzureAdSignInSettings.IsConfigured`; `EnforcementMigration.Determined`.
- Produces: `interface ISearchResultVerifier { int CandidateMultiplier { get; } Task<IReadOnlyList<SearchHit>> VerifyAsync(IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default); }`. `VerifyAsync` returns the rank-ordered surviving hits, at most `topK` (backfill). `NoOpSearchResultVerifier`: `CandidateMultiplier => 1`, returns `rankedCandidates.Take(topK)`.

**Routing (per candidate, in rank order; azblob only):**
1. `resource_uri` not azblob (s3/null/unknown) → **pass**.
2. Azure enforcement not active (StateForAzure ≠ Enforcing) → **pass** all (no-op; the resolver didn't broaden either).
3. Identity: no link / deprovisioned / directory-failed → **drop** every azblob hit (fail closed).
4. Covered by an RBAC `ReadablePrefix` (hit URI starts with the prefix) → **pass**.
5. Under a `TagConditioned` scope (hit URI starts with its `Scope`) → **tag verify**: read tags; if any covering tag condition matches → pass, else drop.
6. Else → **Gen2 verify**: `ReadFileAclAsync` → `PosixAclEvaluator.Grants(fileAcl, userOid, groupOids, Read)` AND `HoldsTraverseOnAllAncestorsAsync(path, userOid, groupOids)`; both true → pass, else drop.

Bounded parallelism (`SemaphoreSlim`, degree from settings, default 16) over the candidate pool; preserve input rank order in the output; stop-collecting at `topK` survivors is unnecessary because the pool is already bounded by the over-fetch — verify the whole pool concurrently and take the first `topK` survivors by original rank.

- [ ] **Step 1: Add the interface + no-op**

```csharp
// src/Connapse.Core/Interfaces/ISearchResultVerifier.cs
using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Post-retrieval per-hit permission verify. Given ranked candidates, returns the subset the searcher
/// may actually read, in rank order, at most <paramref name="topK"/> (dropping unreadable hits and
/// backfilling from lower-ranked survivors). <see cref="CandidateMultiplier"/> tells the pipeline how
/// much to over-fetch so backfill has material.
/// </summary>
public interface ISearchResultVerifier
{
    int CandidateMultiplier { get; }

    Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default);
}
```

```csharp
// src/Connapse.Storage/CloudScope/NoOpSearchResultVerifier.cs
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.CloudScope;

/// <summary>The default: no Azure verification. Returns the top <c>topK</c> unchanged.</summary>
public sealed class NoOpSearchResultVerifier : ISearchResultVerifier
{
    public int CandidateMultiplier => 1;

    public Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SearchHit>>(rankedCandidates.Take(topK).ToList());
}
```

- [ ] **Step 2: Write the failing tests (fakes for every seam)**

```csharp
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
public class AzureSearchResultVerifierTests
{
    private const Gen2Permission RX = Gen2Permission.Read | Gen2Permission.Execute;
    private static readonly Guid User = Guid.NewGuid();
    private static readonly AzureIdentityRef Link = new("user-oid", "tid");

    private static SearchHit Hit(string docId, double score) =>
        new($"chunk-{docId}", docId, "content", (float)score, new Dictionary<string, string>());

    private static IOptionsMonitor<T> Opt<T>(T v) where T : class
    {
        var m = Substitute.For<IOptionsMonitor<T>>();
        m.CurrentValue.Returns(v);
        return m;
    }

    // Builds a verifier with all seams faked; each test overrides what it needs.
    private sealed class Harness
    {
        public IDocumentStore Docs = Substitute.For<IDocumentStore>();
        public IAzureIdentityLinkReader Links = Substitute.For<IAzureIdentityLinkReader>();
        public IAzureDirectoryReader Directory = Substitute.For<IAzureDirectoryReader>();
        public IAzureRbacReader Rbac = Substitute.For<IAzureRbacReader>();
        public IGen2FileAclReader FileAcl = Substitute.For<IGen2FileAclReader>();
        public IBlobTagReader Tags = Substitute.For<IBlobTagReader>();
        public IGen2DirectoryReader DirReader = Substitute.For<IGen2DirectoryReader>();
        public bool AzureConfigured = true;
        public bool AzureEnforcing = true;

        public AzureSearchResultVerifier Build()
        {
            Links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
            Directory.ResolveAsync(Link, Arg.Any<CancellationToken>())
                .Returns(AzureIdentitySet.Resolved(["user-oid", "group-1"]));
            Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
                .Returns(AzureRbacScopes.Resolved([], []));
            var azureAd = Opt(AzureConfigured
                ? new AzureAdSignInSettings { TenantId = "t", ClientId = "c", RedirectUri = "https://x/cb", ClientCertificatePath = "p.pem" }
                : new AzureAdSignInSettings());
            var enf = Opt(new PermissionEnforcementSettings { AzureEnforcing = AzureEnforcing });
            var traverse = new AncestorTraverseResolver(DirReader, new MemoryCache(new MemoryCacheOptions()));
            return new AzureSearchResultVerifier(
                Docs, Links, Directory, Rbac, FileAcl, Tags, traverse,
                azureAd, enf, EnforcementMigration.Completed(),
                Options.Create(new AzureVerifierSettings { MaxParallelism = 16, CandidateMultiplier = 5 }),
                NullLogger<AzureSearchResultVerifier>.Instance);
        }
    }

    private void ResourceUris(Harness h, params (string doc, string? uri)[] map) =>
        h.Docs.GetResourceUrisAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(map.ToDictionary(m => m.doc, m => m.uri));

    [Fact]
    public async Task NonAzureHits_PassUntouched()
    {
        var h = new Harness();
        ResourceUris(h, ("d1", "s3://b/k"), ("d2", null));
        var hits = new[] { Hit("d1", 0.9), Hit("d2", 0.8) };

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync(hits, User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("d1", "d2");
    }

    [Fact]
    public async Task AzureNotEnforcing_PassesEverything()
    {
        var h = new Harness { AzureEnforcing = false };
        ResourceUris(h, ("d1", "azblob://acct/docs/secret"));
        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);
        r.Should().ContainSingle();
    }

    [Fact]
    public async Task RbacCoveredHit_Passes_WithoutAnyLiveAclOrTagRead()
    {
        var h = new Harness();
        h.Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
            .Returns(AzureRbacScopes.Resolved([new AzureScope("azblob://acct/docs/")], []));
        ResourceUris(h, ("d1", "azblob://acct/docs/a/file.txt"));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);

        r.Should().ContainSingle();
        await h.FileAcl.DidNotReceive().ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
        await h.Tags.DidNotReceive().ReadTagsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LockedFileBeneathReadableFolder_IsDropped()
    {
        // Soundness: no RBAC/tag; file's own ACL denies Read → drop, even if ancestors traverse.
        var h = new Harness();
        ResourceUris(h, ("d1", "azblob://acct/docs/locked.txt"));
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, RX, Gen2Permission.None, null, [], [])); // other = no read
        h.DirReader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, RX, HasExtendedAcl: false)); // ancestors traverse

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);

        r.Should().BeEmpty();
    }

    [Fact]
    public async Task CaseC_PerFileGrantBeneathUnreadableFolder_IsReturned()
    {
        // Completeness: file's own ACL grants Read to the user AND ancestors are traversable (X),
        // even though the folder is not readable as a listing (R). No RBAC. → pass.
        var h = new Harness();
        ResourceUris(h, ("d1", "azblob://acct/docs/secret/case-c.txt"));
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2Acl("owner", "group", RX, Gen2Permission.None, Gen2Permission.None,
                Mask: RX, NamedUsers: [new Gen2NamedAce("user-oid", RX)], NamedGroups: []));
        h.DirReader.ReadModeBitsAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>())
            .Returns(new Gen2ModeBits("owner", "group", RX, RX, Gen2Permission.Execute, HasExtendedAcl: false));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10);

        r.Should().ContainSingle();
    }

    [Fact]
    public async Task Gen2FileAclUnreadable_IsDropped_FailClosed()
    {
        var h = new Harness();
        ResourceUris(h, ("d1", "azblob://acct/docs/x.txt"));
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2Acl?)null);

        (await h.Build().VerifyAsync([Hit("d1", 0.9)], User, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task TagConditioned_MatchingTag_Passes_And_NonMatching_Drops()
    {
        var h = new Harness();
        h.Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>()).Returns(
            AzureRbacScopes.Resolved([], [new AzureTagCondition("azblob://acct/docs/", "Project", "Cascade", true, false)]));
        ResourceUris(h, ("hit-ok", "azblob://acct/docs/a.txt"), ("hit-no", "azblob://acct/docs/b.txt"));
        h.Tags.ReadTagsAsync(Arg.Is<Gen2Path>(p => p.Path == "a.txt"), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Project"] = "Cascade" });
        h.Tags.ReadTagsAsync(Arg.Is<Gen2Path>(p => p.Path == "b.txt"), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["Project"] = "Other" });

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("hit-ok", 0.9), Hit("hit-no", 0.8)], User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("hit-ok");
    }

    [Fact]
    public async Task DeprovisionedIdentity_DropsAllAzure_ButKeepsNonCloud()
    {
        var h = new Harness();
        h.Directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Deprovisioned());
        ResourceUris(h, ("az", "azblob://acct/docs/x"), ("nc", null));

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync([Hit("az", 0.9), Hit("nc", 0.8)], User, 10);

        r.Select(x => x.DocumentId).Should().BeEquivalentTo("nc");
    }

    [Fact]
    public async Task Backfill_KeepsTopKInRankOrder_AfterDroppingUnreadable()
    {
        var h = new Harness();
        h.Rbac.ResolveAsync("user-oid", Arg.Any<CancellationToken>())
            .Returns(AzureRbacScopes.Resolved([new AzureScope("azblob://acct/ok/")], []));
        ResourceUris(h,
            ("d1", "azblob://acct/no/1"),  // dropped (not covered, file acl null)
            ("d2", "azblob://acct/ok/2"),  // pass
            ("d3", "azblob://acct/ok/3")); // pass
        h.FileAcl.ReadFileAclAsync(Arg.Any<Gen2Path>(), Arg.Any<CancellationToken>()).Returns((Gen2Acl?)null);

        IReadOnlyList<SearchHit> r = await h.Build().VerifyAsync(
            [Hit("d1", 0.9), Hit("d2", 0.8), Hit("d3", 0.7)], User, 2);

        r.Select(x => x.DocumentId).Should().Equal("d2", "d3"); // rank order preserved, d1 backfilled out
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureSearchResultVerifierTests"`
Expected: FAIL — `AzureSearchResultVerifier` / `AzureVerifierSettings` do not exist.

- [ ] **Step 4: Write the implementation**

Create `src/Connapse.Core/Models/AzureVerifierSettings.cs`:

```csharp
namespace Connapse.Core;

/// <summary>Tuning for the Phase 4e verifier.</summary>
public sealed class AzureVerifierSettings
{
    public const string SectionName = "Azure:Verifier";
    /// <summary>Bounded per-hit verify concurrency.</summary>
    public int MaxParallelism { get; set; } = 16;
    /// <summary>How many candidates to over-fetch per requested result, so backfill has material.</summary>
    public int CandidateMultiplier { get; set; } = 5;
}
```

Create `src/Connapse.Storage/CloudScope/AzureSearchResultVerifier.cs`:

```csharp
using System.Collections.Concurrent;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Live per-hit Azure permission verify (Phase 4e). Passes non-Azure hits untouched; for each
/// azblob hit, admits it only if covered by an RBAC prefix (in-memory), or a matching blob-tag
/// condition, or the file's own ACL grants Read AND every ancestor directory grants traverse-X
/// (4d). Fails closed on every uncertain read. Preserves rank order and returns at most topK.
/// </summary>
public sealed class AzureSearchResultVerifier(
    IDocumentStore documents,
    IAzureIdentityLinkReader links,
    IAzureDirectoryReader directory,
    IAzureRbacReader rbac,
    IGen2FileAclReader fileAcl,
    IBlobTagReader blobTags,
    AncestorTraverseResolver traverse,
    IOptionsMonitor<AzureAdSignInSettings> azureAd,
    IOptionsMonitor<PermissionEnforcementSettings> enforcement,
    EnforcementMigration migration,
    IOptions<AzureVerifierSettings> settings,
    ILogger<AzureSearchResultVerifier> logger) : ISearchResultVerifier
{
    public int CandidateMultiplier =>
        Math.Max(1, settings.Value.CandidateMultiplier);

    public async Task<IReadOnlyList<SearchHit>> VerifyAsync(
        IReadOnlyList<SearchHit> rankedCandidates, Guid? userId, int topK, CancellationToken ct = default)
    {
        // No Azure enforcement → the resolver did not broaden; nothing to tighten.
        if (enforcement.CurrentValue.StateForAzure(azureAd.CurrentValue.IsConfigured, migration.Determined)
            != EnforcementState.Enforcing)
            return rankedCandidates.Take(topK).ToList();

        // Map hits to their governing URIs (one batched query).
        IReadOnlyDictionary<string, string?> uris =
            await documents.GetResourceUrisAsync(rankedCandidates.Select(h => h.DocumentId).ToList(), ct);

        // Resolve the searcher's Azure context once. Any failure/deprovision → drop every azblob hit.
        AzureContext? azure = await ResolveContextAsync(userId, ct);

        // Verify azblob hits concurrently (bounded); non-azblob pass; decision keyed by index so we
        // can re-assemble in rank order.
        var verdicts = new ConcurrentDictionary<int, bool>();
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Value.MaxParallelism));

        await Task.WhenAll(rankedCandidates.Select(async (hit, i) =>
        {
            string? uri = uris.GetValueOrDefault(hit.DocumentId);
            if (!AzblobUri.TryParse(uri, out Gen2Path path))
            {
                verdicts[i] = true; // s3:// or non-cloud → pass untouched
                return;
            }
            if (azure is null)
            {
                verdicts[i] = false; // enforcing but no usable identity → drop azblob
                return;
            }
            await gate.WaitAsync(ct);
            try { verdicts[i] = await AdmitAzureHitAsync(uri!, path, azure, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Azure hit verify failed; dropping"); verdicts[i] = false; }
            finally { gate.Release(); }
        }));

        var survivors = new List<SearchHit>(topK);
        for (int i = 0; i < rankedCandidates.Count && survivors.Count < topK; i++)
            if (verdicts.GetValueOrDefault(i)) survivors.Add(rankedCandidates[i]);
        return survivors;
    }

    private async Task<bool> AdmitAzureHitAsync(string uri, Gen2Path path, AzureContext azure, CancellationToken ct)
    {
        // 1. RBAC read supersedes ACLs — covered by any readable prefix → pass, no live call.
        if (azure.ReadablePrefixes.Any(p => uri.StartsWith(p, StringComparison.Ordinal)))
            return true;

        // 2. Tag-conditioned residue → live tag verify against every covering condition.
        AzureTagCondition[] covering =
            azure.TagConditions.Where(t => uri.StartsWith(t.Scope, StringComparison.Ordinal)).ToArray();
        if (covering.Length > 0)
        {
            IReadOnlyDictionary<string, string>? tags = await blobTags.ReadTagsAsync(path, ct);
            if (tags is null) return false; // fail closed
            return covering.Any(c => AzureTagConditionEvaluator.Matches(c, tags));
        }

        // 3. Gen2 ACL: file's own Read AND traverse-X on every ancestor. Folder access never trusted.
        Gen2Acl? acl = await fileAcl.ReadFileAclAsync(path, ct);
        if (acl is null) return false; // unreadable / flat account / incomplete → drop
        if (!PosixAclEvaluator.Grants(acl, azure.UserOid, azure.GroupOids, Gen2Permission.Read))
            return false;
        return await traverse.HoldsTraverseOnAllAncestorsAsync(path, azure.UserOid, azure.GroupOids, ct);
    }

    private async Task<AzureContext?> ResolveContextAsync(Guid? userId, CancellationToken ct)
    {
        if (userId is null) return null;
        AzureIdentityRef? link = await links.GetLinkAsync(userId.Value, ct);
        if (link is null) return null;

        AzureIdentitySet identity = await directory.ResolveAsync(link, ct);
        if (identity.Outcome != AzureIdentityOutcome.Resolved) return null; // deprovisioned/failed → drop azblob

        AzureRbacScopes scopes = await rbac.ResolveAsync(link.ObjectId, ct);
        if (scopes.Outcome != RbacOutcome.Resolved) return null; // RBAC uncertain → fail closed

        var groups = identity.PrincipalOids.Where(o => o != link.ObjectId).ToHashSet(StringComparer.Ordinal);
        return new AzureContext(
            link.ObjectId, groups,
            scopes.ReadablePrefixes.Select(s => s.Prefix).ToArray(),
            scopes.TagConditioned.ToArray());
    }

    private sealed record AzureContext(
        string UserOid, IReadOnlySet<string> GroupOids,
        IReadOnlyList<string> ReadablePrefixes, IReadOnlyList<AzureTagCondition> TagConditions);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureSearchResultVerifierTests"`
Expected: PASS (all 9 cases).

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Core/Interfaces/ISearchResultVerifier.cs src/Connapse.Core/Models/AzureVerifierSettings.cs src/Connapse.Storage/CloudScope/NoOpSearchResultVerifier.cs src/Connapse.Storage/CloudScope/AzureSearchResultVerifier.cs tests/Connapse.Storage.Tests/CloudScope/AzureSearchResultVerifierTests.cs
git commit -m "feat(azure): live per-hit search-result verifier + no-op default (#490)"
```

---

### Task 7: Broaden Azure retrieval (Storage)

**Files:**
- Modify: `src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs`
- Test: `tests/Connapse.Storage.Tests/CloudScope/AzureSearchScopeResolverTests.cs`

**Interfaces:**
- Produces (change): for a valid enforcing identity (link present, not deprovisioned, directory not failed), `ResolveAsync` returns `SearchScopes.Of([new GrantMatch("azblob://", IsExact: false)])` — retrieve every Azure candidate. The enforcement gate, null-user `NoPrincipal`, no-link `NoPrincipal`, deprovisioned `NoPrincipal`, and directory-failed `Failed` paths are unchanged. **The RBAC call is removed from retrieval** (RBAC now lives in the verifier); drop the `IAzureRbacReader` dependency if it becomes unused.

- [ ] **Step 1: Update the tests to the broadened contract**

In `AzureSearchScopeResolverTests.cs`, the "enabled with RBAC prefixes → granted azblob matches" and "no RBAC prefixes → no-grants" cases are replaced: a valid enforcing identity now yields the single broad `azblob://` match. Keep every fail-closed case (`NotEnforcing`→Unrestricted, `EnforcingButAzureAdNotConfigured`→Failed, `Undetermined`→Failed, null user→NoPrincipal, no link→NoPrincipal, deprovisioned→NoPrincipal, identity-failed→Failed). Replace the two RBAC-specific tests with:

```csharp
[Fact]
public async Task ValidEnforcingIdentity_RetrievesAllAzblob_ForTheVerifierToTighten()
{
    var links = Substitute.For<IAzureIdentityLinkReader>();
    links.GetLinkAsync(User, Arg.Any<CancellationToken>()).Returns(Link);
    var directory = Substitute.For<IAzureDirectoryReader>();
    directory.ResolveAsync(Link, Arg.Any<CancellationToken>()).Returns(AzureIdentitySet.Resolved(["oid-1"]));
    var r = Build(links: links, directory: directory);

    SearchScopes s = await r.ResolveAsync(User);

    s.Outcome.Should().Be(ScopeOutcome.Granted);
    s.Matches.Should().ContainSingle().Which.Value.Should().Be("azblob://");
    s.Matches[0].IsExact.Should().BeFalse();
}
```

(Delete `Enabled_WithRbacPrefixes_ReturnsGrantedAzblobMatches` and `Enabled_NoRbacPrefixes_IsNoGrants`, and drop the now-unused `rbac`/`IAzureRbacReader` wiring from the test's `Build` helper.)

- [ ] **Step 2: Run to verify the new test fails**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureSearchScopeResolverTests"`
Expected: FAIL — resolver still emits RBAC prefixes / references removed members.

- [ ] **Step 3: Broaden the resolver**

In `AzureSearchScopeResolver.ResolveUncachedAsync`, after the deprovisioning gate, replace the RBAC resolution + prefix projection with the broad match:

```csharp
private async Task<SearchScopes> ResolveUncachedAsync(Guid userId, CancellationToken ct)
{
    AzureIdentityRef? link = await links.GetLinkAsync(userId, ct);
    if (link is null)
        return SearchScopes.NoPrincipal;

    AzureIdentitySet identity = await directory.ResolveAsync(link, ct);
    if (identity.Outcome is AzureIdentityOutcome.Deprovisioned)
    {
        logger.LogInformation("A linked Entra identity is disabled or gone; denying");
        return SearchScopes.NoPrincipal;
    }
    if (identity.Outcome is AzureIdentityOutcome.Failed)
        return SearchScopes.Failed;

    // Broad retrieve-then-verify (§E amendment): a valid enforcing identity retrieves EVERY Azure
    // candidate by relevance; the post-retrieval verifier (Phase 4e) tightens per hit via RBAC
    // coverage, tag conditions, and Gen2 file-ACL + ancestor traverse. Narrowing here (e.g. to RBAC
    // prefixes) would drop ACL-only "Case C" files, which can live in any container.
    return SearchScopes.Of([new GrantMatch("azblob://", IsExact: false)]);
}
```

Remove the `IAzureRbacReader rbac` constructor parameter and its `using`/field if now unused. (Leave `IAzureDirectoryReader` — the deprovisioning gate still needs it.)

- [ ] **Step 4: Run to verify tests pass**

Run: `dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --filter "FullyQualifiedName~AzureSearchScopeResolverTests"`
Expected: PASS. Also run the composite tests to confirm no break: `--filter "FullyQualifiedName~CompositeSearchScopeResolverTests"`.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Storage/CloudScope/AzureSearchScopeResolver.cs tests/Connapse.Storage.Tests/CloudScope/AzureSearchScopeResolverTests.cs
git commit -m "feat(azure): broaden Azure retrieval to azblob:// (verify tightens) (#490)"
```

---

### Task 8: Wire the verifier into the pipeline + DI + integration test

**Files:**
- Modify: `src/Connapse.Search/Hybrid/HybridSearchService.cs`
- Modify: `src/Connapse.Storage/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Connapse.Integration.Tests/AzureVerifyEnforcementTests.cs`

**Interfaces:**
- Consumes: `ISearchResultVerifier` (Task 6). The service resolves it in the per-search DI scope (like `vectorSearch`/`keywordSearch`).

- [ ] **Step 1: Over-fetch, verify, backfill in `HybridSearchService`**

The retrieval leaves use `options.TopK`. To give the verifier backfill headroom, retrieve with an inflated `TopK` (`options.TopK * verifier.CandidateMultiplier`), then verify down to `options.TopK`. Resolve the verifier in the existing `scope` (line 104 area) alongside the other scoped services, and compute the inflated options BEFORE dispatch:

```csharp
var verifier = scope.ServiceProvider.GetRequiredService<ISearchResultVerifier>();
int overFetch = Math.Max(1, verifier.CandidateMultiplier);
SearchOptions retrieveOptions = options with { TopK = options.TopK * overFetch };
```

Use `retrieveOptions` for the `vectorSearch.SearchAsync` / `keywordSearch.SearchAsync` / `PerformHybridSearchAsync` calls and for rerank. Then insert the verify AFTER the `filtered` ordering (line 171) and BEFORE `AutoCut`/substitution:

```csharp
var ordered = hits
    .Where(h => h.Score >= options.MinScore)
    .OrderByDescending(h => h.Score)
    .ToList();

IReadOnlyList<SearchHit> verified = await verifier.VerifyAsync(ordered, options.UserId, options.TopK, ct);

var filtered = verified.ToList();
if (searchSettings.AutoCut)
    filtered = ApplyAutoCut(filtered);

IReadOnlyList<SearchHit> substituted = SentenceWindowSubstitution
    .SubstituteIfEnabled(filtered, searchSettings.SentenceWindowSubstituteOnSearch);

var finalHits = substituted.Take(options.TopK).ToList();
```

(`SearchOptions` is a record — confirm `with` works; if it is not a record, construct a copy explicitly. Verify against `src/Connapse.Core/Models/SearchModels.cs`.)

- [ ] **Step 2: Register the verifier + seams in DI**

In `ServiceCollectionExtensions.cs`, alongside the CloudScope registrations, register the seams (Task 3/4 concretes) and choose the verifier based on whether Azure is in play. Simplest and safe: always register the Azure verifier (it self-no-ops when Azure isn't enforcing):

```csharp
services.AddSingleton<IGen2FileAclReader>(sp => (DataLakeGen2DirectoryReader)sp.GetRequiredService<IGen2DirectoryReader>());
services.AddSingleton<IBlobTagReader, BlobTagReader>();
services.AddScoped<ISearchResultVerifier, AzureSearchResultVerifier>();
services.Configure<AzureVerifierSettings>(configuration.GetSection(AzureVerifierSettings.SectionName));
```

(If `IGen2DirectoryReader` is registered as the concrete `DataLakeGen2DirectoryReader`, resolve the same singleton for `IGen2FileAclReader` as shown so both interfaces share one instance. `AzureSearchResultVerifier` depends on `IDocumentStore`, the Azure readers, `AncestorTraverseResolver`, and the options — all already registered. Confirm `configuration`/`IConfiguration` is in scope in this extension method; if not, bind settings the way the file binds other sections.)

- [ ] **Step 3: Write the integration test**

`tests/Connapse.Integration.Tests/AzureVerifyEnforcementTests.cs` — mirror `AzureFlatEnforcementTests`' fixture usage. Seed documents with `azblob://` and `s3://` and NULL `resource_uri`, drive `IKnowledgeSearch.SearchAsync` (or the verifier directly with a seeded `IDocumentStore`) and assert: an azblob hit with no grant is dropped; an azblob hit under an RBAC prefix passes; an s3 and a non-cloud hit pass untouched; Azure-not-enforcing passes everything. Because live Azure calls can't run in CI, inject fake `IGen2FileAclReader`/`IBlobTagReader`/`IAzureRbacReader`/`IAzureDirectoryReader`/`IAzureIdentityLinkReader` into the verifier and exercise the real `IDocumentStore.GetResourceUrisAsync` against the seeded database (this is the piece that needs the container). Assert the resource_uri lookup + routing end to end.

```csharp
// Shape (adapt fixture/DI to the SharedWebAppFixture pattern):
// 1. Seed 4 docs: azblob in-RBAC, azblob no-grant, s3, non-cloud.
// 2. Build AzureSearchResultVerifier with the real IDocumentStore (from the fixture) + fake Azure readers.
// 3. VerifyAsync(rankedHits, user, topK) → assert the expected survivors.
```

- [ ] **Step 4: Build + run the full unit suites and the new integration test**

Run:
```bash
dotnet build -clp:ErrorsOnly
dotnet test tests/Connapse.Core.Tests/Connapse.Core.Tests.csproj --no-build --filter "Category=Unit"
dotnet test tests/Connapse.Storage.Tests/Connapse.Storage.Tests.csproj --no-build --filter "Category=Unit"
dotnet test tests/Connapse.Search.Tests/Connapse.Search.Tests.csproj --no-build --filter "Category=Unit"   # if present
dotnet test tests/Connapse.Integration.Tests/Connapse.Integration.Tests.csproj --no-build --filter "FullyQualifiedName~AzureVerifyEnforcementTests"
```
Expected: build clean; unit suites green; the new integration test green (the 23 pre-existing Ollama integration failures are unrelated and environmental).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(azure): wire per-hit verifier into HybridSearchService + DI (#490)"
```

---

## Self-Review

**1. Spec coverage (§E + amendment):**
- `ISearchResultVerifier` + no-op default → Tasks 6.
- Over-fetch + backfill + bounded parallelism → Task 6 (parallelism, backfill) + Task 8 (over-fetch via `CandidateMultiplier`).
- Retrieve by relevance over a broad over-approximation, folder structure never narrows retrieval → Task 7 (`azblob://`).
- Routing: exact/prefix RBAC → pass; HNS → file ACL + ancestor traverse; flat tag → tag verify; AWS/non-cloud → untouched → Task 6 `AdmitAzureHitAsync`.
- Reading a Gen2 file requires the file's own Read AND traverse-X on every ancestor (folder access never trusted) → Task 6 (both checks) using 4d.
- Soundness (locked file dropped) + Completeness (Case C returned) → Task 6 tests `LockedFileBeneathReadableFolder_IsDropped`, `CaseC_PerFileGrantBeneathUnreadableFolder_IsReturned`.
- Fail-closed matrix (no link / deprovisioned / failed / uncertain read) → Task 6 (`ResolveContextAsync` + null-drops).
- AWS path unchanged → verifier passes s3/non-cloud; resolver change is Azure-only; SQL stores untouched.

**2. Placeholder scan:** Every code step has full code except Task 5 (store impl adapts to the existing DbContext idiom) and Task 8 Step 3 (integration test shape) — both name the exact types and assertions required and defer only to existing local patterns, which the implementer must read. No TBD/TODO.

**3. Type consistency:** `Gen2Path`, `Gen2Acl`, `Gen2Permission`, `PosixAclEvaluator.Grants(acl, userOid, groupOids, perm)`, `AncestorTraverseResolver.HoldsTraverseOnAllAncestorsAsync(path, userOid, groupOids, ct)`, `AzureRbacScopes.ReadablePrefixes`/`TagConditioned`, `AzureTagCondition(Scope, TagKey, TagValue, KeyCaseSensitive, ValueCaseSensitive)`, `AzureIdentitySet.PrincipalOids`/`Outcome`, `AzureIdentityRef.ObjectId`, `SearchHit(ChunkId, DocumentId, Content, Score, Metadata)`, `ISearchResultVerifier.VerifyAsync/CandidateMultiplier` — names match across producing and consuming tasks and the Explore map. The implementer MUST confirm three things against the real code before relying on them: (a) `SearchOptions` supports `with` (record) — Task 8; (b) the `IDocumentStore` implementation's DbContext/entity names — Task 5; (c) the DI extension method's access to `IConfiguration` for binding `AzureVerifierSettings` — Task 8. Each is flagged in its task.
