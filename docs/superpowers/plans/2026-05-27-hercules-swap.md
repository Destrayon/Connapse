# HERCULES Swap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `document-clustering` container-summary method (HERCULES-style) as the new default — clusters on pooled chunk embeddings and lazy-summarizes K medoid docs at rollup time. Existing `summary-clustering` behavior stays available behind a setting.

**Architecture:** Adds `SummarySettings.ContainerSummaryMethod` (G+C scope, resolves through existing `ContainerSettingsResolver`). `IngestionJobs.PerDocSummaryAsync` early-returns in `document-clustering` mode. `SummaryJobs.RollupContainerAsync` branches: existing path for `summary-clustering`, new path that pools chunk vectors via `IVectorStore.GetPooledDocumentEmbeddingsAsync`, runs farthest-first medoid selection, inline-summarizes K medoids (cached in `documents.summary`), then calls the existing `ContainerSummarizer` reduce step. `ComputeDocSetHash` switches to `(docId, content_hash)` pairs so it works without per-doc summary text. No DB schema changes.

**Tech Stack:** .NET 10, EF Core, pgvector, Hangfire, xUnit + FluentAssertions + NSubstitute, Testcontainers for integration.

**Spec:** `docs/superpowers/specs/2026-05-27-hercules-swap-design.md`

**Sequencing:** Ships after PR #333 (Hangfire migration) merges. Do not start until #333 is on `main`.

---

## File Structure

**New files:**
- `src/Connapse.Core/Models/SummaryStrategy.cs` — string constants for allowed `ContainerSummaryMethod` values
- `tests/Connapse.Background.Tests/Jobs/SummaryJobsHerculesTests.cs` — unit tests for new rollup branch
- `tests/Connapse.Background.Tests/Jobs/IngestionJobsHerculesTests.cs` — unit tests for early-return
- `tests/Connapse.Storage.Tests/Vectors/PgVectorStorePooledEmbeddingsTests.cs` — integration tests for pooling SQL
- `tests/Connapse.Web.Tests/Summarization/HerculesIntegrationTests.cs` — end-to-end integration tests

**Modified files:**
- `src/Connapse.Core/Models/SettingsModels.cs` — add `ContainerSummaryMethod` field + validator
- `src/Connapse.Core/Interfaces/IVectorStore.cs` — add `GetPooledDocumentEmbeddingsAsync`
- `src/Connapse.Storage/Vectors/PgVectorStore.cs` — implement pooled embeddings query
- `src/Connapse.Background/Jobs/IngestionJobs.cs` — early-return branch in `PerDocSummaryAsync`
- `src/Connapse.Background/Jobs/SummaryJobs.cs` — split rollup into two methods, swap hash function
- `src/Connapse.Web/Components/Settings/SummarySettingsTab.razor` — add method dropdown
- `tests/Connapse.Storage.Tests/Settings/ContainerSettingsResolverTests.cs` — cover new field
- `docs/manual-qa/summary-generation.md` — add document-clustering scenarios

---

## Task 1: Prep — feature branch + baseline build/test

**Files:**
- N/A (env setup)

- [ ] **Step 1: Verify PR #333 has merged**

Run from `D:\CodeProjects\Connapse`:
```
git fetch origin
git log --oneline origin/main -5
```
Expected: PR #333's merge commit appears on `origin/main`. If not, STOP — do not start this work until #333 is merged.

- [ ] **Step 2: Pull latest main and create the feature branch**

The actual issue number will come from Step 3. Use a placeholder branch name first, rename after the issue exists.

```
git checkout main
git pull origin main
```

- [ ] **Step 3: File GitHub issue**

```
gh issue create \
  --title "feat: add document-clustering container summary method (HERCULES swap)" \
  --body "$(cat <<'EOF'
Adds a new \`document-clustering\` value for \`SummarySettings.ContainerSummaryMethod\` (G+C scope) that becomes the default for new installs. The new method clusters documents using mean-pooled chunk embeddings (free — they already exist for vector search) and lazy-summarizes only the K medoid docs at rollup time, instead of eagerly summarizing every document at upload.

The current behavior remains available as \`summary-clustering\`.

Design: \`docs/superpowers/specs/2026-05-27-hercules-swap-design.md\`
Plan: \`docs/superpowers/plans/2026-05-27-hercules-swap.md\`

Built on:
- HERCULES paper (arXiv:2506.19992): +76% ARI on clustering quality vs summary embeddings, p<0.001
- Follow-up research \`docs/research/lazy-vs-eager-per-doc-summarization-2026-05-26.md\`
EOF
)"
```

Capture the issue number returned (e.g., `#334`).

- [ ] **Step 4: Create the feature branch with the issue number**

Replace `<N>` with the issue number from Step 3:
```
git checkout -b feature/<N>-hercules-swap
```

- [ ] **Step 5: Baseline build and test**

```
dotnet build
dotnet test --filter "Category=Unit"
```

Expected: build succeeds, unit tests all green. If anything fails, STOP — the failure is pre-existing and must be addressed (or noted as known-bad) before starting feature work.

- [ ] **Step 6: Commit the design + plan docs**

```
git add docs/superpowers/specs/2026-05-27-hercules-swap-design.md docs/superpowers/plans/2026-05-27-hercules-swap.md
git commit -m "docs: add HERCULES swap design + implementation plan (#<N>)"
```

---

## Task 2: SummaryStrategy constants

**Files:**
- Create: `src/Connapse.Core/Models/SummaryStrategy.cs`

- [ ] **Step 1: Create the constants file**

```csharp
namespace Connapse.Core;

/// <summary>
/// Allowed values for <see cref="SummarySettings.ContainerSummaryMethod"/>.
/// </summary>
/// <remarks>
/// Stored as a string in <c>SummarySettings</c> to match the existing
/// <c>LlmProvider</c> / <c>LlmModel</c> convention. Validation happens in the
/// <c>SummarySettings</c> validator and rejects unknown values.
/// </remarks>
public static class SummaryStrategy
{
    /// <summary>
    /// HERCULES-style: cluster documents by mean-pooled chunk embeddings and
    /// lazy-summarize K medoid documents at rollup time. Default for new installs.
    /// </summary>
    public const string DocumentClustering = "document-clustering";

    /// <summary>
    /// Legacy: summarize every document at ingest, cluster by summary embeddings,
    /// reduce K medoid summaries. Original behavior from PR #329.
    /// </summary>
    public const string SummaryClustering = "summary-clustering";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentClustering,
        SummaryClustering,
    };
}
```

- [ ] **Step 2: Build**

```
dotnet build src/Connapse.Core
```
Expected: PASS

- [ ] **Step 3: Commit**

```
git add src/Connapse.Core/Models/SummaryStrategy.cs
git commit -m "feat: add SummaryStrategy constants for container summary method"
```

---

## Task 3: SummarySettings.ContainerSummaryMethod field + validator

**Files:**
- Modify: `src/Connapse.Core/Models/SettingsModels.cs` (add field to `SummarySettings` record)

- [ ] **Step 1: Add the new field to the SummarySettings record**

In `src/Connapse.Core/Models/SettingsModels.cs`, find the `SummarySettings` record and add this field directly after `public bool Enabled { get; init; } = false;`:

```csharp
    /// <summary>
    /// Container summary generation method. See <see cref="SummaryStrategy"/> for allowed values.
    /// Default: <c>document-clustering</c> — clusters by pooled chunk embeddings and lazy-summarizes
    /// K medoid documents at rollup time. Set to <c>summary-clustering</c> to use the legacy
    /// eager per-doc summarization path.
    /// </summary>
    public string ContainerSummaryMethod { get; init; } = SummaryStrategy.DocumentClustering;
```

- [ ] **Step 2: Add validation by implementing IValidatableObject**

If `SummarySettings` does not already implement `IValidatableObject`, change the declaration line:

```csharp
public record SummarySettings : IValidatableObject
```

Then add at the bottom of the record body, before the closing brace:

```csharp
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!SummaryStrategy.All.Contains(ContainerSummaryMethod))
        {
            yield return new ValidationResult(
                $"ContainerSummaryMethod must be one of: {string.Join(", ", SummaryStrategy.All)}. Got: '{ContainerSummaryMethod}'.",
                new[] { nameof(ContainerSummaryMethod) });
        }
    }
```

Add to the file's `using` block if not present:
```csharp
using System.ComponentModel.DataAnnotations;
```
(It's already there for `[Range]`.)

- [ ] **Step 3: Build**

```
dotnet build src/Connapse.Core
```
Expected: PASS

- [ ] **Step 4: Commit**

```
git add src/Connapse.Core/Models/SettingsModels.cs
git commit -m "feat: add ContainerSummaryMethod field to SummarySettings + validator"
```

---

## Task 4: ContainerSettingsResolver tests for new field

**Files:**
- Modify: `tests/Connapse.Storage.Tests/Settings/ContainerSettingsResolverTests.cs`

- [ ] **Step 1: Locate the existing test class**

```
grep -n "GetSummarySettingsAsync" tests/Connapse.Storage.Tests/Settings/ContainerSettingsResolverTests.cs
```
Find the existing block of `GetSummarySettingsAsync_*` tests and confirm the test scaffolding pattern (DB fixture, `_settingsStore` mock, `_resolver` instance).

- [ ] **Step 2: Add the override test**

Add this test method inside the existing test class, alongside the other `GetSummarySettingsAsync_*` tests. Adapt the helpers (`SeedContainerAsync`, the resolver factory) to match what the surrounding tests already use:

```csharp
[Fact]
[Trait("Category", "Integration")]
public async Task GetSummarySettingsAsync_ContainerSummaryMethodOverride_TakesPrecedence()
{
    // Arrange: global says summary-clustering, container override says document-clustering
    var global = new SummarySettings
    {
        Enabled = true,
        ContainerSummaryMethod = SummaryStrategy.SummaryClustering,
    };
    _settingsStore.GetAsync<SummarySettings>("Summary", Arg.Any<CancellationToken>())
                  .Returns(global);

    var overrideJson = JsonSerializer.Serialize(new
    {
        summary = new
        {
            enabled = true,
            containerSummaryMethod = SummaryStrategy.DocumentClustering,
        }
    });
    Guid containerId = await SeedContainerWithOverridesAsync(overrideJson);

    // Act
    SummarySettings resolved = await _resolver.GetSummarySettingsAsync(containerId, CancellationToken.None);

    // Assert
    resolved.ContainerSummaryMethod.Should().Be(SummaryStrategy.DocumentClustering);
}

[Fact]
[Trait("Category", "Integration")]
public async Task GetSummarySettingsAsync_NoOverride_UsesDocumentClusteringDefault()
{
    // Arrange: no global, no override → record default applies
    _settingsStore.GetAsync<SummarySettings>("Summary", Arg.Any<CancellationToken>())
                  .Returns((SummarySettings?)null);
    Guid containerId = await SeedContainerWithoutOverridesAsync();

    // Act
    SummarySettings resolved = await _resolver.GetSummarySettingsAsync(containerId, CancellationToken.None);

    // Assert
    resolved.ContainerSummaryMethod.Should().Be(SummaryStrategy.DocumentClustering);
}
```

If `SeedContainerWithOverridesAsync` / `SeedContainerWithoutOverridesAsync` don't exist with those exact names, use whatever the existing tests in the file use to seed an override JSON (they typically write a `ContainerEntity` with `SettingsOverridesJson` set). Mirror that pattern exactly.

- [ ] **Step 3: Run the tests**

```
dotnet test --filter "FullyQualifiedName~ContainerSettingsResolverTests.GetSummarySettingsAsync_ContainerSummaryMethodOverride_TakesPrecedence"
dotnet test --filter "FullyQualifiedName~ContainerSettingsResolverTests.GetSummarySettingsAsync_NoOverride_UsesDocumentClusteringDefault"
```
Expected: PASS

- [ ] **Step 4: Commit**

```
git add tests/Connapse.Storage.Tests/Settings/ContainerSettingsResolverTests.cs
git commit -m "test: cover ContainerSummaryMethod G+C resolution"
```

---

## Task 5: Add IVectorStore.GetPooledDocumentEmbeddingsAsync interface method

**Files:**
- Modify: `src/Connapse.Core/Interfaces/IVectorStore.cs`

- [ ] **Step 1: Add the new method to the interface**

Edit `src/Connapse.Core/Interfaces/IVectorStore.cs` to:

```csharp
namespace Connapse.Core.Interfaces;

public interface IVectorStore
{
    Task UpsertAsync(string id, float[] vector, Dictionary<string, string> metadata, CancellationToken ct = default);
    Task UpsertBatchAsync(IReadOnlyList<(string Id, float[] Vector, Dictionary<string, string> Metadata)> items, CancellationToken ct = default);
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(float[] queryVector, int topK, Dictionary<string, string>? filters = null, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteByDocumentIdAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Returns one mean-pooled embedding per document in the container, computed across
    /// that document's chunk vectors. Used by the <c>document-clustering</c> container
    /// summary path to cluster docs without re-embedding them.
    /// </summary>
    /// <remarks>
    /// Implementations should:
    /// 1. Pick the dominant <c>model_id</c> in the container and filter to vectors of that model.
    ///    Containers can hold mixed-model chunk vectors; clustering requires a single dimensionality.
    /// 2. Skip documents that have zero chunks of the dominant model.
    /// 3. L2-normalize each pooled vector before returning (pgvector AVG does not renormalize).
    /// 4. Log a warning naming the count of documents excluded due to non-dominant model_id.
    /// </remarks>
    Task<IReadOnlyList<(Guid DocumentId, float[] Embedding)>> GetPooledDocumentEmbeddingsAsync(
        Guid containerId,
        CancellationToken ct = default);
}
```

- [ ] **Step 2: Build (expect failure)**

```
dotnet build
```
Expected: FAIL — `PgVectorStore` doesn't implement the new method yet. This is fine; the next task adds it. Confirm the only build errors are about `PgVectorStore` missing `GetPooledDocumentEmbeddingsAsync`.

- [ ] **Step 3: Commit the interface alone (intentional broken build between tasks)**

```
git add src/Connapse.Core/Interfaces/IVectorStore.cs
git commit -m "feat: add IVectorStore.GetPooledDocumentEmbeddingsAsync interface"
```

(Build is restored in Task 6.)

---

## Task 6: Implement PgVectorStore.GetPooledDocumentEmbeddingsAsync

**Files:**
- Modify: `src/Connapse.Storage/Vectors/PgVectorStore.cs`

- [ ] **Step 1: Add the implementation**

Append this method to the `PgVectorStore` class (before the closing brace):

```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid DocumentId, float[] Embedding)>> GetPooledDocumentEmbeddingsAsync(
        Guid containerId,
        CancellationToken ct = default)
    {
        // Step 1: Pick the dominant model_id in the container. ChunkVectorEntity carries
        // ContainerId and ModelId directly (no JOIN through chunks needed).
        var modelCounts = await _context.ChunkVectors
            .Where(cv => cv.ContainerId == containerId)
            .GroupBy(cv => cv.ModelId)
            .Select(g => new { ModelId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        if (modelCounts.Count == 0)
        {
            return Array.Empty<(Guid, float[])>();
        }

        string dominantModelId = modelCounts[0].ModelId;

        if (modelCounts.Count > 1)
        {
            int excludedDocs = modelCounts.Skip(1).Sum(m => m.Count);
            _logger.LogWarning(
                "GetPooledDocumentEmbeddingsAsync: container {ContainerId} has mixed embedding models. " +
                "Using dominant model '{DominantModelId}' ({DominantCount} vectors). " +
                "Excluding {ExcludedCount} vectors from {OtherModelCount} other model(s).",
                containerId, dominantModelId, modelCounts[0].Count, excludedDocs, modelCounts.Count - 1);
        }

        // Step 2: Pool per-document. AVG(embedding) returns a vector at the same dimensionality.
        // ChunkVectorEntity has document_id directly, so no JOINs are needed.
        var conn = _context.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT document_id, AVG(embedding)::vector AS pooled
            FROM chunk_vectors
            WHERE container_id = @cid
              AND model_id = @model_id
            GROUP BY document_id
            """;

        var p = cmd.Parameters;
        p.Add(new NpgsqlParameter("cid", containerId));
        p.Add(new NpgsqlParameter("model_id", dominantModelId));

        var results = new List<(Guid DocumentId, float[] Embedding)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            Guid docId = reader.GetGuid(0);
            // Pgvector binds as Pgvector.Vector; ToArray() gives float[].
            Vector pooled = reader.GetFieldValue<Vector>(1);
            float[] raw = pooled.ToArray();
            float[] normalized = L2Normalize(raw);
            results.Add((docId, normalized));
        }

        return results;
    }

    private static float[] L2Normalize(float[] v)
    {
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        double norm = Math.Sqrt(sumSq);
        if (norm < 1e-12) return v; // degenerate; return as-is rather than div-by-zero
        float[] result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }
```

- [ ] **Step 2: Build**

```
dotnet build
```
Expected: PASS — interface now satisfied.

- [ ] **Step 3: Commit**

```
git add src/Connapse.Storage/Vectors/PgVectorStore.cs
git commit -m "feat: implement PgVectorStore.GetPooledDocumentEmbeddingsAsync"
```

---

## Task 7: Integration test for PgVectorStore pooled embeddings

**Files:**
- Create: `tests/Connapse.Storage.Tests/Vectors/PgVectorStorePooledEmbeddingsTests.cs`

- [ ] **Step 1: Examine existing PgVectorStore integration test setup**

```
grep -rn "class.*PgVectorStore.*Tests" tests/Connapse.Storage.Tests/
```
Find an existing PgVectorStore integration test file to copy the fixture/DI scaffolding pattern. The shared collection is `SharedWebAppFixture` per `connapse/CLAUDE.md`.

- [ ] **Step 2: Write the failing test**

Create `tests/Connapse.Storage.Tests/Vectors/PgVectorStorePooledEmbeddingsTests.cs`. Adapt the fixture wiring to match the sibling tests in the same folder:

```csharp
using Connapse.Storage.Vectors;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Pgvector;
using Xunit;

namespace Connapse.Storage.Tests.Vectors;

[Trait("Category", "Integration")]
[Collection("SharedWebApp")]
public class PgVectorStorePooledEmbeddingsTests
{
    private readonly SharedWebAppFixture _fixture;

    public PgVectorStorePooledEmbeddingsTests(SharedWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_OneModelTwoDocs_PoolsCorrectly()
    {
        // Arrange: seed one container with 2 docs, each with 2 chunks under the same model.
        Guid containerId = Guid.NewGuid();
        Guid doc1 = Guid.NewGuid();
        Guid doc2 = Guid.NewGuid();
        const string modelId = "test-model";

        await _fixture.SeedContainerAsync(containerId, "test-pool-1");
        await _fixture.SeedDocumentAsync(doc1, containerId);
        await _fixture.SeedDocumentAsync(doc2, containerId);

        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc1, containerId, modelId, new float[] { 1f, 0f, 0f });
        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc1, containerId, modelId, new float[] { 0f, 1f, 0f });
        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc2, containerId, modelId, new float[] { 0f, 0f, 1f });
        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc2, containerId, modelId, new float[] { 0f, 0f, 1f });

        PgVectorStore store = _fixture.CreatePgVectorStore();

        // Act
        var result = await store.GetPooledDocumentEmbeddingsAsync(containerId, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        var doc1Pooled = result.First(r => r.DocumentId == doc1).Embedding;
        // Doc1 pool of (1,0,0) and (0,1,0) is (0.5, 0.5, 0); L2-normalized → (~0.707, ~0.707, 0)
        doc1Pooled[0].Should().BeApproximately(0.7071f, 0.001f);
        doc1Pooled[1].Should().BeApproximately(0.7071f, 0.001f);
        doc1Pooled[2].Should().BeApproximately(0f, 0.001f);

        var doc2Pooled = result.First(r => r.DocumentId == doc2).Embedding;
        // Doc2 pool of (0,0,1) and (0,0,1) is (0,0,1); L2-normalized → (0,0,1)
        doc2Pooled[2].Should().BeApproximately(1f, 0.001f);
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_MixedModels_UsesDominantAndLogsWarning()
    {
        Guid containerId = Guid.NewGuid();
        Guid doc1 = Guid.NewGuid();
        Guid doc2 = Guid.NewGuid();
        Guid doc3 = Guid.NewGuid();

        await _fixture.SeedContainerAsync(containerId, "test-pool-mixed");
        await _fixture.SeedDocumentAsync(doc1, containerId);
        await _fixture.SeedDocumentAsync(doc2, containerId);
        await _fixture.SeedDocumentAsync(doc3, containerId);

        // doc1 + doc2 under model-A (dominant: 2 vectors)
        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc1, containerId, "model-A", new float[] { 1f, 0f });
        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc2, containerId, "model-A", new float[] { 0f, 1f });
        // doc3 under model-B (excluded: 1 vector). NOTE: dimensions differ here, but the
        // dominant filter excludes them before they affect the pool.
        await _fixture.SeedChunkVectorAsync(Guid.NewGuid(), doc3, containerId, "model-B", new float[] { 1f, 0f, 0f });

        PgVectorStore store = _fixture.CreatePgVectorStore();
        var result = await store.GetPooledDocumentEmbeddingsAsync(containerId, CancellationToken.None);

        // Assert: only doc1 and doc2 (both under dominant model-A) are returned
        result.Should().HaveCount(2);
        result.Select(r => r.DocumentId).Should().BeEquivalentTo(new[] { doc1, doc2 });
    }

    [Fact]
    public async Task GetPooledDocumentEmbeddingsAsync_EmptyContainer_ReturnsEmpty()
    {
        Guid containerId = Guid.NewGuid();
        await _fixture.SeedContainerAsync(containerId, "test-pool-empty");

        PgVectorStore store = _fixture.CreatePgVectorStore();
        var result = await store.GetPooledDocumentEmbeddingsAsync(containerId, CancellationToken.None);

        result.Should().BeEmpty();
    }
}
```

If `SeedContainerAsync` / `SeedDocumentAsync` / `SeedChunkVectorAsync` / `CreatePgVectorStore` aren't already on `SharedWebAppFixture`, add the minimum helpers needed by inspecting other `*Tests.cs` files in `tests/Connapse.Storage.Tests/Vectors/` and copying their seeding approach (typically direct `KnowledgeDbContext` writes).

- [ ] **Step 3: Run the tests**

```
dotnet test --filter "FullyQualifiedName~PgVectorStorePooledEmbeddingsTests"
```
Expected: PASS (all three tests)

- [ ] **Step 4: Commit**

```
git add tests/Connapse.Storage.Tests/Vectors/PgVectorStorePooledEmbeddingsTests.cs
git commit -m "test: integration coverage for PgVectorStore.GetPooledDocumentEmbeddingsAsync"
```

---

## Task 8: IngestionJobs.PerDocSummaryAsync early-return in document-clustering mode

**Files:**
- Modify: `src/Connapse.Background/Jobs/IngestionJobs.cs`

- [ ] **Step 1: Add the early-return guard**

In `src/Connapse.Background/Jobs/IngestionJobs.cs`, locate the existing block in `PerDocSummaryAsync`:

```csharp
            SummarySettings settings = await _settingsResolver.GetSummarySettingsAsync(containerId, ct);
            if (!settings.Enabled)
            {
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason=summaries_disabled",
                    LogSanitizer.Sanitize(documentId));
                return;
            }
```

Add this guard immediately after the `summaries_disabled` check:

```csharp
            if (settings.ContainerSummaryMethod == SummaryStrategy.DocumentClustering)
            {
                // Document-clustering mode summarizes K medoids lazily at rollup time;
                // per-doc summaries are not generated at ingest.
                _logger.LogInformation(
                    "PerDocSummarySkipped {DocumentId} reason=document_clustering_mode",
                    LogSanitizer.Sanitize(documentId));

                // Still advance the ingestion state so the UI doesn't show a stuck spinner.
                // SummaryIndexed is the correct terminal state — in document-clustering mode,
                // "summary processing is complete" means "we decided not to summarize this doc."
                await _docStore.UpdateIngestionStateAsync(documentId, IngestionState.SummaryIndexed, CancellationToken.None);
                await _stateBroadcaster.BroadcastIngestionStateChangedAsync(
                    documentId, IngestionState.SummaryIndexed, CancellationToken.None);
                return;
            }
```

- [ ] **Step 2: Build**

```
dotnet build src/Connapse.Background
```
Expected: PASS

- [ ] **Step 3: Commit**

```
git add src/Connapse.Background/Jobs/IngestionJobs.cs
git commit -m "feat: PerDocSummaryAsync early-returns in document-clustering mode"
```

---

## Task 9: Unit test IngestionJobs early-return

**Files:**
- Create: `tests/Connapse.Background.Tests/Jobs/IngestionJobsHerculesTests.cs`

- [ ] **Step 1: Check existing IngestionJobs test scaffolding**

```
grep -rn "class IngestionJobsTests" tests/Connapse.Background.Tests/
```
Examine the existing IngestionJobs tests to copy the mock-wiring pattern (NSubstitute for `IDocumentStore`, `IPerDocSummarizer`, etc.).

- [ ] **Step 2: Write the test**

Create `tests/Connapse.Background.Tests/Jobs/IngestionJobsHerculesTests.cs`:

```csharp
using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Background.Tests.Jobs;

[Trait("Category", "Unit")]
public class IngestionJobsHerculesTests
{
    [Fact]
    public async Task PerDocSummaryAsync_DocumentClusteringMode_EarlyReturnsWithoutCallingSummarizer()
    {
        // Arrange
        Guid docId = Guid.NewGuid();
        Guid containerId = Guid.NewGuid();
        var doc = new Document(
            Id: docId.ToString(),
            ContainerId: containerId.ToString(),
            FileName: "test.txt",
            ContentType: "text/plain",
            Path: "/test.txt",
            SizeBytes: 100,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string>());

        var docStore = Substitute.For<IDocumentStore>();
        docStore.GetAsync(docId.ToString(), Arg.Any<CancellationToken>()).Returns(doc);

        var summarizer = Substitute.For<IPerDocSummarizer>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(new SummarySettings
            {
                Enabled = true,
                ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
            });

        var broadcaster = Substitute.For<IIngestionStateBroadcaster>();

        var jobs = new IngestionJobs(
            ingester: Substitute.For<IKnowledgeIngester>(),
            docStore: docStore,
            containerStore: Substitute.For<IContainerStore>(),
            connectorFactory: Substitute.For<IConnectorFactory>(),
            parsers: Array.Empty<IDocumentParser>(),
            summarizer: summarizer,
            settingsResolver: settingsResolver,
            bgClient: Substitute.For<IBackgroundJobClient>(),
            stateBroadcaster: broadcaster,
            logger: NullLogger<IngestionJobs>.Instance);

        // Act
        await jobs.PerDocSummaryAsync(docId.ToString(), CancellationToken.None);

        // Assert: summarizer was never invoked
        await summarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default);

        // And state was advanced to SummaryIndexed so the UI doesn't hang
        await docStore.Received(1).UpdateIngestionStateAsync(
            docId.ToString(), IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());
        await broadcaster.Received(1).BroadcastIngestionStateChangedAsync(
            docId.ToString(), IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerDocSummaryAsync_SummaryClusteringMode_CallsSummarizerAsBefore()
    {
        // Sanity regression: existing path still works.
        Guid docId = Guid.NewGuid();
        Guid containerId = Guid.NewGuid();
        var doc = new Document(
            Id: docId.ToString(),
            ContainerId: containerId.ToString(),
            FileName: "test.txt",
            ContentType: "text/plain",
            Path: "/test.txt",
            SizeBytes: 100,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string>());

        var docStore = Substitute.For<IDocumentStore>();
        docStore.GetAsync(docId.ToString(), Arg.Any<CancellationToken>()).Returns(doc);

        var summarizer = Substitute.For<IPerDocSummarizer>();
        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(new SummarySettings
            {
                Enabled = true,
                ContainerSummaryMethod = SummaryStrategy.SummaryClustering,
            });

        // Container/connector wiring: return null so the existing path bails on container_not_found
        // — we only need to prove the early-return branch was NOT taken (summarizer would still
        // not be called, but the skip reason would be "container_not_found" not "document_clustering_mode").
        var containerStore = Substitute.For<IContainerStore>();
        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>()).Returns((Container?)null);

        var jobs = new IngestionJobs(
            ingester: Substitute.For<IKnowledgeIngester>(),
            docStore: docStore,
            containerStore: containerStore,
            connectorFactory: Substitute.For<IConnectorFactory>(),
            parsers: Array.Empty<IDocumentParser>(),
            summarizer: summarizer,
            settingsResolver: settingsResolver,
            bgClient: Substitute.For<IBackgroundJobClient>(),
            stateBroadcaster: Substitute.For<IIngestionStateBroadcaster>(),
            logger: NullLogger<IngestionJobs>.Instance);

        await jobs.PerDocSummaryAsync(docId.ToString(), CancellationToken.None);

        // Verify we did NOT advance state to SummaryIndexed via the early-return path
        // (the existing path bailed earlier with container_not_found and didn't touch state).
        await docStore.DidNotReceive().UpdateIngestionStateAsync(
            docId.ToString(), IngestionState.SummaryIndexed, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run the tests**

```
dotnet test --filter "FullyQualifiedName~IngestionJobsHerculesTests"
```
Expected: PASS

- [ ] **Step 4: Commit**

```
git add tests/Connapse.Background.Tests/Jobs/IngestionJobsHerculesTests.cs
git commit -m "test: IngestionJobs.PerDocSummaryAsync early-returns in document-clustering mode"
```

---

## Task 10: ComputeDocSetHash — switch to content_hash

**Files:**
- Modify: `src/Connapse.Background/Jobs/SummaryJobs.cs`

- [ ] **Step 1: Verify Document model exposes content_hash**

```
grep -n "ContentHash" src/Connapse.Core/Models/*.cs
```
Confirm `Document` record (or its metadata bag) carries a `ContentHash`. If `Document` doesn't have it directly, check how `PostgresDocumentStore.MapToModel` exposes it: it's added to the metadata dictionary as `"ContentHash"`. Use that.

- [ ] **Step 2: Replace ComputeDocSetHash**

In `src/Connapse.Background/Jobs/SummaryJobs.cs`, replace the existing `ComputeDocSetHash` method with this implementation. The new version hashes `(docId, content_hash)` pairs, which works for both `document-clustering` (where most docs have null summaries) and `summary-clustering` (where all docs have summaries — but their content_hash equally identifies their state).

```csharp
    /// <summary>
    /// Deterministic hash of the (sorted) set of {docId, content_hash} pairs for all docs
    /// in the container. Works in both <c>document-clustering</c> mode (where most docs have
    /// null summaries) and <c>summary-clustering</c> mode (where content_hash equally
    /// identifies stale-vs-fresh state).
    /// </summary>
    /// <remarks>
    /// Migration note: hashes will differ from the prior (docId, summary-hash) formula across
    /// the deploy boundary, causing one extra rollup per container on first run post-deploy.
    /// Accepted one-shot cost.
    /// </remarks>
    internal static string ComputeDocSetHash(IEnumerable<Document> docs)
    {
        IEnumerable<string> parts = docs
            .OrderBy(d => d.Id)
            .Select(d =>
            {
                string contentHash = d.Metadata?.GetValueOrDefault("ContentHash") ?? string.Empty;
                return $"{d.Id}|{contentHash}";
            });
        return HexHash.Sha256(string.Join("\n", parts));
    }
```

- [ ] **Step 3: Build**

```
dotnet build src/Connapse.Background
```
Expected: PASS

- [ ] **Step 4: Commit**

```
git add src/Connapse.Background/Jobs/SummaryJobs.cs
git commit -m "refactor: ComputeDocSetHash uses content_hash instead of summary text"
```

---

## Task 11: Unit test ComputeDocSetHash

**Files:**
- Create or extend: `tests/Connapse.Background.Tests/Jobs/SummaryJobsHashTests.cs`

- [ ] **Step 1: Write the failing test**

Create the file (or add to it if it already exists from a prior task):

```csharp
using Connapse.Background.Jobs;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Background.Tests.Jobs;

[Trait("Category", "Unit")]
public class SummaryJobsHashTests
{
    [Fact]
    public void ComputeDocSetHash_SameContentHashes_ProducesSameHash()
    {
        Document MakeDoc(string id, string contentHash) => new(
            Id: id,
            ContainerId: Guid.NewGuid().ToString(),
            FileName: "f.txt",
            ContentType: "text/plain",
            Path: "/f.txt",
            SizeBytes: 1,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string> { ["ContentHash"] = contentHash });

        var docsA = new[]
        {
            MakeDoc("11111111-1111-1111-1111-111111111111", "hash-a"),
            MakeDoc("22222222-2222-2222-2222-222222222222", "hash-b"),
        };
        // Same docs in different order
        var docsB = new[]
        {
            MakeDoc("22222222-2222-2222-2222-222222222222", "hash-b"),
            MakeDoc("11111111-1111-1111-1111-111111111111", "hash-a"),
        };

        SummaryJobs.ComputeDocSetHash(docsA).Should().Be(SummaryJobs.ComputeDocSetHash(docsB));
    }

    [Fact]
    public void ComputeDocSetHash_NullContentHash_IsStableNotCrashing()
    {
        var docs = new[]
        {
            new Document(
                Id: Guid.NewGuid().ToString(),
                ContainerId: Guid.NewGuid().ToString(),
                FileName: "f.txt",
                ContentType: "text/plain",
                Path: "/f.txt",
                SizeBytes: 1,
                CreatedAt: DateTime.UtcNow,
                Metadata: new Dictionary<string, string>()) // no ContentHash key
        };

        Action act = () => SummaryJobs.ComputeDocSetHash(docs);
        act.Should().NotThrow();
    }

    [Fact]
    public void ComputeDocSetHash_DifferentContentHash_ProducesDifferentHash()
    {
        Document MakeDoc(string contentHash) => new(
            Id: "11111111-1111-1111-1111-111111111111",
            ContainerId: Guid.NewGuid().ToString(),
            FileName: "f.txt",
            ContentType: "text/plain",
            Path: "/f.txt",
            SizeBytes: 1,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string> { ["ContentHash"] = contentHash });

        string hashA = SummaryJobs.ComputeDocSetHash(new[] { MakeDoc("hash-a") });
        string hashB = SummaryJobs.ComputeDocSetHash(new[] { MakeDoc("hash-b") });

        hashA.Should().NotBe(hashB);
    }
}
```

- [ ] **Step 2: Run the tests**

```
dotnet test --filter "FullyQualifiedName~SummaryJobsHashTests"
```
Expected: PASS

- [ ] **Step 3: Commit**

```
git add tests/Connapse.Background.Tests/Jobs/SummaryJobsHashTests.cs
git commit -m "test: ComputeDocSetHash covers content_hash variant"
```

---

## Task 12: SummaryJobs.RollupContainerAsync — split into two branches

**Files:**
- Modify: `src/Connapse.Background/Jobs/SummaryJobs.cs`

This is the largest single change. We split `RollupContainerAsync` into a top-level dispatcher and two branch methods. Existing behavior moves into `RollupSummaryClusteringAsync` (no logic change). The new `RollupDocumentClusteringAsync` does pooled-embedding clustering and lazy medoid summarization.

- [ ] **Step 1: Add new constructor dependencies**

We need `IVectorStore` and `IPerDocSummarizer` injected into `SummaryJobs`. Update the constructor:

```csharp
public sealed class SummaryJobs : ISummaryJobs
{
    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _docStore;
    private readonly IContainerSettingsResolver _settingsResolver;
    private readonly IDocumentSummaryEmbeddingProvider _embeddingProvider;
    private readonly IVectorStore _vectorStore;
    private readonly IPerDocSummarizer _perDocSummarizer;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IEnumerable<IDocumentParser> _parsers;
    private readonly SummaryLlmResolver _llmResolver;
    private readonly ITokenCounter _tokenCounter;
    private readonly IBackgroundJobClient _bgClient;
    private readonly ILogger<SummaryJobs> _logger;

    public SummaryJobs(
        IContainerStore containerStore,
        IDocumentStore docStore,
        IContainerSettingsResolver settingsResolver,
        IDocumentSummaryEmbeddingProvider embeddingProvider,
        IVectorStore vectorStore,
        IPerDocSummarizer perDocSummarizer,
        IConnectorFactory connectorFactory,
        IEnumerable<IDocumentParser> parsers,
        SummaryLlmResolver llmResolver,
        ITokenCounter tokenCounter,
        IBackgroundJobClient bgClient,
        ILogger<SummaryJobs> logger)
    {
        _containerStore = containerStore;
        _docStore = docStore;
        _settingsResolver = settingsResolver;
        _embeddingProvider = embeddingProvider;
        _vectorStore = vectorStore;
        _perDocSummarizer = perDocSummarizer;
        _connectorFactory = connectorFactory;
        _parsers = parsers;
        _llmResolver = llmResolver;
        _tokenCounter = tokenCounter;
        _bgClient = bgClient;
        _logger = logger;
    }
```

- [ ] **Step 2: Replace RollupContainerAsync with dispatcher**

Replace the existing `RollupContainerAsync` body with this dispatcher:

```csharp
    [Queue(JobQueues.Summarization)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task RollupContainerAsync(Guid containerId, CancellationToken ct)
    {
        Container? container = await _containerStore.GetAsync(containerId, ct);
        if (container is null) return;

        SummarySettings settings = await _settingsResolver.GetSummarySettingsAsync(containerId, ct);
        if (!settings.Enabled)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=summaries_disabled",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        if (settings.ContainerSummaryMethod == SummaryStrategy.DocumentClustering)
        {
            await RollupDocumentClusteringAsync(container, settings, ct);
        }
        else
        {
            await RollupSummaryClusteringAsync(container, settings, ct);
        }
    }
```

- [ ] **Step 3: Add RollupSummaryClusteringAsync (existing logic moved verbatim)**

Add this private method. It contains the previous body of `RollupContainerAsync` from the doc-load step onward, with no behavioral changes:

```csharp
    private async Task RollupSummaryClusteringAsync(Container container, SummarySettings settings, CancellationToken ct)
    {
        Guid containerId = container.Id;

        IReadOnlyList<Document> docs = await _docStore.ListAsync(
            containerId, pathPrefix: null, skip: 0, take: 10_000, ct);
        List<Document> withSummaries = docs.Where(d => !string.IsNullOrEmpty(d.Summary)).ToList();

        if (withSummaries.Count == 0)
        {
            await _containerStore.UpdateSummaryAsync(containerId, null, null, null, ct);
            return;
        }

        string docSetHash = ComputeDocSetHash(withSummaries);
        if (docSetHash == container.SummaryDocSetHash)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=doc_set_hash_match",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        ILlmProvider? llm = _llmResolver.Resolve(settings);
        IReadOnlyList<DocumentWithSummary> docsWithEmbeddings =
            await _embeddingProvider.GetSummaryEmbeddingsAsync(withSummaries, ct);

        IContainerSummarizer summarizer = new ContainerSummarizer(llm, _tokenCounter);
        ContainerSummarizationResult result = await summarizer.GenerateAsync(
            container.Name, docsWithEmbeddings, settings, ct);

        if (result.Skipped)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason={Reason}",
                LogSanitizer.Sanitize(containerId.ToString()),
                LogSanitizer.Sanitize(result.SkipReason ?? ""));
            return;
        }

        await _containerStore.UpdateSummaryAsync(
            containerId, result.Summary, DateTime.UtcNow, docSetHash, ct);

        _logger.LogInformation(
            "ContainerRollupCompleted {ContainerId} method=summary-clustering regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok}",
            LogSanitizer.Sanitize(containerId.ToString()),
            LogSanitizer.Sanitize(result.Regime ?? ""),
            result.NumDocs,
            result.KClusters,
            result.InputTokens,
            result.OutputTokens);
    }
```

- [ ] **Step 4: Add RollupDocumentClusteringAsync (new lazy path)**

Add this private method right after `RollupSummaryClusteringAsync`:

```csharp
    private const int LazyStuffThreshold = 30; // mirrors ContainerSummarizer.StuffThreshold
    private const int LazyMaxClusters = 20;    // mirrors ContainerSummarizer.MaxClusters

    private async Task RollupDocumentClusteringAsync(Container container, SummarySettings settings, CancellationToken ct)
    {
        Guid containerId = container.Id;

        IReadOnlyList<Document> allDocs = await _docStore.ListAsync(
            containerId, pathPrefix: null, skip: 0, take: 10_000, ct);

        if (allDocs.Count == 0)
        {
            await _containerStore.UpdateSummaryAsync(containerId, null, null, null, ct);
            return;
        }

        // Hash gate: skip rollup if the (docId, content_hash) set is unchanged since last rollup.
        string docSetHash = ComputeDocSetHash(allDocs);
        if (docSetHash == container.SummaryDocSetHash)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=doc_set_hash_match",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        // Pick which docs to summarize. Below the stuff threshold we summarize all of them;
        // above it we cluster on pooled chunk embeddings and pick K medoids.
        IReadOnlyList<Document> docsToSummarize;
        string regime;
        int? kClusters = null;

        if (allDocs.Count <= LazyStuffThreshold)
        {
            docsToSummarize = allDocs;
            regime = "stuff";
        }
        else
        {
            var pooled = await _vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, ct);
            if (pooled.Count == 0)
            {
                _logger.LogInformation(
                    "ContainerRollupSkipped {ContainerId} reason=no_pooled_embeddings",
                    LogSanitizer.Sanitize(containerId.ToString()));
                return;
            }

            int k = Math.Min(LazyMaxClusters, (int)Math.Ceiling(pooled.Count / 3.0));
            kClusters = k;
            var medoids = MedoidSelector.SelectFarthestFirst(pooled, k);
            var medoidIds = medoids.Select(m => m.Id).ToHashSet();

            // Map medoid Guids back to Document instances. Skip any whose docs no longer exist
            // (deleted between pooling query and now) — defensive.
            var docsById = allDocs.ToDictionary(d => Guid.Parse(d.Id));
            docsToSummarize = medoidIds
                .Where(id => docsById.ContainsKey(id))
                .Select(id => docsById[id])
                .ToList();
            regime = "cluster";
        }

        // Lazy-summarize each selected doc: cache hit when content_hash matches; otherwise call LLM.
        var summarizedDocs = new List<DocumentWithSummary>();
        foreach (var doc in docsToSummarize)
        {
            ct.ThrowIfCancellationRequested();

            string contentHash = doc.Metadata?.GetValueOrDefault("ContentHash") ?? string.Empty;
            bool cacheHit = !string.IsNullOrEmpty(doc.Summary)
                            && !string.IsNullOrEmpty(doc.SummaryContentHash)
                            && doc.SummaryContentHash == contentHash;

            string? summary;
            if (cacheHit)
            {
                summary = doc.Summary;
                _logger.LogDebug(
                    "LazyMedoidSummaryCacheHit {DocumentId}",
                    LogSanitizer.Sanitize(doc.Id));
            }
            else
            {
                summary = await GenerateAndCacheSummaryAsync(doc, settings, ct);
                if (summary is null) continue; // skip docs that couldn't be summarized
            }

            // The ContainerSummarizer reduce step needs an Embedding too (for its internal stuff
            // path it's not used, but the DocumentWithSummary type requires it). Use a placeholder.
            // Empty array is fine because docsToSummarize is already <= LazyMaxClusters, so the
            // ContainerSummarizer will take its stuff path (N <= StuffThreshold) and never touch
            // .Embedding.
            summarizedDocs.Add(new DocumentWithSummary(
                Id: Guid.Parse(doc.Id),
                Summary: summary!,
                Embedding: Array.Empty<float>()));
        }

        if (summarizedDocs.Count == 0)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason=no_summarizable_docs",
                LogSanitizer.Sanitize(containerId.ToString()));
            return;
        }

        ILlmProvider? llm = _llmResolver.Resolve(settings);
        IContainerSummarizer summarizer = new ContainerSummarizer(llm, _tokenCounter);
        ContainerSummarizationResult result = await summarizer.GenerateAsync(
            container.Name, summarizedDocs, settings, ct);

        if (result.Skipped)
        {
            _logger.LogInformation(
                "ContainerRollupSkipped {ContainerId} reason={Reason}",
                LogSanitizer.Sanitize(containerId.ToString()),
                LogSanitizer.Sanitize(result.SkipReason ?? ""));
            return;
        }

        await _containerStore.UpdateSummaryAsync(
            containerId, result.Summary, DateTime.UtcNow, docSetHash, ct);

        _logger.LogInformation(
            "ContainerRollupCompleted {ContainerId} method=document-clustering regime={Regime} N={N} k={K} inTok={InTok} outTok={OutTok}",
            LogSanitizer.Sanitize(containerId.ToString()),
            regime,
            allDocs.Count,
            kClusters,
            result.InputTokens,
            result.OutputTokens);
    }

    /// <summary>
    /// Runs the per-doc summarizer for one document in document-clustering mode, then
    /// writes the result through to <c>documents.summary</c> as the cache for future rollups.
    /// Returns null if the doc could not be summarized (parser failure, empty content, etc).
    /// </summary>
    private async Task<string?> GenerateAndCacheSummaryAsync(Document doc, SummarySettings settings, CancellationToken ct)
    {
        if (!Guid.TryParse(doc.ContainerId, out Guid containerId)) return null;
        Container? container = await _containerStore.GetAsync(containerId, ct);
        if (container is null) return null;

        // Re-parse doc text through the container's connector — same pattern as IngestionJobs.PerDocSummaryAsync.
        string parsedText;
        try
        {
            IConnector connector = _connectorFactory.Create(container);
            string jobPath = connector.ResolveJobPath(doc.Path.TrimStart('/'));
            await using Stream stream = await connector.ReadFileAsync(jobPath, ct);

            string extension = Path.GetExtension(doc.FileName).ToLowerInvariant();
            IDocumentParser? parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
            if (parser is null)
            {
                _logger.LogInformation(
                    "LazyMedoidSummarySkipped {DocumentId} reason=no_parser_for_extension",
                    LogSanitizer.Sanitize(doc.Id));
                return null;
            }

            ParsedDocument parsed = await parser.ParseAsync(stream, doc.FileName, ct);
            parsedText = parsed.Content;
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning(
                "LazyMedoidSummarySkipped {DocumentId} reason=file_not_found",
                LogSanitizer.Sanitize(doc.Id));
            return null;
        }

        if (string.IsNullOrWhiteSpace(parsedText))
        {
            _logger.LogInformation(
                "LazyMedoidSummarySkipped {DocumentId} reason=empty_parsed_content",
                LogSanitizer.Sanitize(doc.Id));
            return null;
        }

        PerDocSummarizationResult result = await _perDocSummarizer.GenerateAsync(
            doc.Id, parsedText, doc.ContentType, doc.FileName, settings, ct);

        if (result.Skipped || string.IsNullOrEmpty(result.Summary))
        {
            _logger.LogInformation(
                "LazyMedoidSummarySkipped {DocumentId} reason={Reason}",
                LogSanitizer.Sanitize(doc.Id),
                LogSanitizer.Sanitize(result.SkipReason ?? "no_summary_returned"));
            return null;
        }

        // Write through to cache: documents.summary + summary_content_hash + summary_generated_at
        string contentHash = doc.Metadata?.GetValueOrDefault("ContentHash") ?? string.Empty;
        await _docStore.UpdateSummaryAsync(
            doc.Id, result.Summary, DateTime.UtcNow, contentHash, ct);

        return result.Summary;
    }
```

- [ ] **Step 5: Update DI registration**

Find where `SummaryJobs` is registered in DI. Likely `src/Connapse.Background/ServiceCollectionExtensions.cs` or similar:

```
grep -rn "AddScoped<ISummaryJobs" src/
```

`IVectorStore`, `IPerDocSummarizer`, `IConnectorFactory`, and `IEnumerable<IDocumentParser>` are already registered for `IngestionJobs`, so no new registrations are needed — the existing `services.AddScoped<ISummaryJobs, SummaryJobs>()` call will pick them up automatically.

Confirm by running:
```
dotnet build
```
Expected: PASS

- [ ] **Step 6: Commit**

```
git add src/Connapse.Background/Jobs/SummaryJobs.cs
git commit -m "feat: SummaryJobs.RollupContainerAsync branches on ContainerSummaryMethod"
```

---

## Task 13: Unit test SummaryJobs routing per ContainerSummaryMethod

**Files:**
- Create: `tests/Connapse.Background.Tests/Jobs/SummaryJobsHerculesTests.cs`

- [ ] **Step 1: Write the routing test**

```csharp
using Connapse.Background.Jobs;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Llm;
using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Connapse.Background.Tests.Jobs;

[Trait("Category", "Unit")]
public class SummaryJobsHerculesTests
{
    private static SummaryJobs BuildJobs(
        IContainerStore? containerStore = null,
        IDocumentStore? docStore = null,
        IContainerSettingsResolver? settingsResolver = null,
        IVectorStore? vectorStore = null,
        IPerDocSummarizer? perDocSummarizer = null,
        IDocumentSummaryEmbeddingProvider? embeddingProvider = null)
    {
        return new SummaryJobs(
            containerStore: containerStore ?? Substitute.For<IContainerStore>(),
            docStore: docStore ?? Substitute.For<IDocumentStore>(),
            settingsResolver: settingsResolver ?? Substitute.For<IContainerSettingsResolver>(),
            embeddingProvider: embeddingProvider ?? Substitute.For<IDocumentSummaryEmbeddingProvider>(),
            vectorStore: vectorStore ?? Substitute.For<IVectorStore>(),
            perDocSummarizer: perDocSummarizer ?? Substitute.For<IPerDocSummarizer>(),
            connectorFactory: Substitute.For<IConnectorFactory>(),
            parsers: Array.Empty<IDocumentParser>(),
            llmResolver: new SummaryLlmResolver(Substitute.For<IServiceProvider>(), NullLogger<SummaryLlmResolver>.Instance),
            tokenCounter: Substitute.For<ITokenCounter>(),
            bgClient: Substitute.For<IBackgroundJobClient>(),
            logger: NullLogger<SummaryJobs>.Instance);
    }

    [Fact]
    public async Task RollupContainerAsync_DocumentClusteringMode_QueriesPooledEmbeddings()
    {
        Guid containerId = Guid.NewGuid();
        var container = new Container(containerId, "test", null, null, null, null);

        var containerStore = Substitute.For<IContainerStore>();
        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>()).Returns(container);

        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(new SummarySettings { Enabled = true, ContainerSummaryMethod = SummaryStrategy.DocumentClustering });

        var docStore = Substitute.For<IDocumentStore>();
        // Return many docs (>StuffThreshold) so clustering path is taken
        var docs = Enumerable.Range(0, 35).Select(i => new Document(
            Id: Guid.NewGuid().ToString(),
            ContainerId: containerId.ToString(),
            FileName: $"f{i}.txt",
            ContentType: "text/plain",
            Path: $"/f{i}.txt",
            SizeBytes: 1,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string> { ["ContentHash"] = $"hash-{i}" }))
            .ToList();
        docStore.ListAsync(containerId, null, 0, 10_000, Arg.Any<CancellationToken>())
            .Returns(docs);

        var vectorStore = Substitute.For<IVectorStore>();
        // Return empty so we exit before trying to summarize — test only proves the call happened
        vectorStore.GetPooledDocumentEmbeddingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Guid, float[])>());

        var embeddingProvider = Substitute.For<IDocumentSummaryEmbeddingProvider>();

        var jobs = BuildJobs(
            containerStore: containerStore,
            docStore: docStore,
            settingsResolver: settingsResolver,
            vectorStore: vectorStore,
            embeddingProvider: embeddingProvider);

        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // Document-clustering path called the pooled query, not the summary-embedding provider
        await vectorStore.Received(1).GetPooledDocumentEmbeddingsAsync(containerId, Arg.Any<CancellationToken>());
        await embeddingProvider.DidNotReceiveWithAnyArgs().GetSummaryEmbeddingsAsync(default!, default);
    }

    [Fact]
    public async Task RollupContainerAsync_SummaryClusteringMode_QueriesSummaryEmbeddings()
    {
        Guid containerId = Guid.NewGuid();
        var container = new Container(containerId, "test", null, null, null, null);

        var containerStore = Substitute.For<IContainerStore>();
        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>()).Returns(container);

        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(new SummarySettings { Enabled = true, ContainerSummaryMethod = SummaryStrategy.SummaryClustering });

        var docStore = Substitute.For<IDocumentStore>();
        docStore.ListAsync(containerId, null, 0, 10_000, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Document>()); // empty so we exit fast; routing test only

        var vectorStore = Substitute.For<IVectorStore>();
        var embeddingProvider = Substitute.For<IDocumentSummaryEmbeddingProvider>();

        var jobs = BuildJobs(
            containerStore: containerStore,
            docStore: docStore,
            settingsResolver: settingsResolver,
            vectorStore: vectorStore,
            embeddingProvider: embeddingProvider);

        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // Summary-clustering path did NOT call the pooled query
        await vectorStore.DidNotReceiveWithAnyArgs().GetPooledDocumentEmbeddingsAsync(default, default);
    }

    [Fact]
    public async Task RollupDocumentClusteringAsync_CacheHit_SkipsLlmCall()
    {
        Guid containerId = Guid.NewGuid();
        Guid docId = Guid.NewGuid();
        var container = new Container(containerId, "test", null, null, null, null);

        // One doc, with summary cache that matches its content hash
        const string contentHash = "matching-hash";
        var doc = new Document(
            Id: docId.ToString(),
            ContainerId: containerId.ToString(),
            FileName: "cached.txt",
            ContentType: "text/plain",
            Path: "/cached.txt",
            SizeBytes: 1,
            CreatedAt: DateTime.UtcNow,
            Metadata: new Dictionary<string, string> { ["ContentHash"] = contentHash },
            Summary: "Cached summary text",
            SummaryGeneratedAt: DateTime.UtcNow.AddDays(-1),
            SummaryContentHash: contentHash);

        var containerStore = Substitute.For<IContainerStore>();
        containerStore.GetAsync(containerId, Arg.Any<CancellationToken>()).Returns(container);

        var settingsResolver = Substitute.For<IContainerSettingsResolver>();
        settingsResolver.GetSummarySettingsAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(new SummarySettings { Enabled = true, ContainerSummaryMethod = SummaryStrategy.DocumentClustering });

        var docStore = Substitute.For<IDocumentStore>();
        docStore.ListAsync(containerId, null, 0, 10_000, Arg.Any<CancellationToken>())
            .Returns(new[] { doc });

        var perDocSummarizer = Substitute.For<IPerDocSummarizer>();

        // N=1 ≤ StuffThreshold so vector store is NOT queried; we go straight to lazy summarization
        var jobs = BuildJobs(
            containerStore: containerStore,
            docStore: docStore,
            settingsResolver: settingsResolver,
            perDocSummarizer: perDocSummarizer);

        await jobs.RollupContainerAsync(containerId, CancellationToken.None);

        // Cache hit means PerDocSummarizer was NEVER invoked
        await perDocSummarizer.DidNotReceiveWithAnyArgs().GenerateAsync(
            default!, default!, default!, default!, default!, default);
    }
}
```

Note: `Container` and `Document` record constructors above are placeholders — adjust positional args to match what's actually in `src/Connapse.Core/Models/*.cs`. The fields used in the tests (`Summary`, `SummaryGeneratedAt`, `SummaryContentHash`, `Metadata`) all exist per `PostgresDocumentStore.MapToModel`.

- [ ] **Step 2: Run the tests**

```
dotnet test --filter "FullyQualifiedName~SummaryJobsHerculesTests"
```
Expected: PASS

- [ ] **Step 3: Commit**

```
git add tests/Connapse.Background.Tests/Jobs/SummaryJobsHerculesTests.cs
git commit -m "test: SummaryJobs routes per ContainerSummaryMethod, honors lazy cache"
```

---

## Task 14: SummarySettingsTab.razor — add Container summary method dropdown

**Files:**
- Modify: `src/Connapse.Web/Components/Settings/SummarySettingsTab.razor`

- [ ] **Step 1: Add the dropdown markup**

In `src/Connapse.Web/Components/Settings/SummarySettingsTab.razor`, find the existing **"LLM Model (override)"** form group block (around lines 36–43) and add this block immediately after it:

```html
    <div class="mb-3">
        <label class="form-label">Container summary method</label>
        <select class="form-select" value="@containerSummaryMethod"
                @onchange="OnContainerSummaryMethodChanged">
            <option value="document-clustering">Document clustering (recommended)</option>
            <option value="summary-clustering">Summary clustering</option>
        </select>
        <div class="form-text">
            <strong>Document clustering</strong> — does not summarize each uploaded document. At container summary generation time, the system clusters documents by their embeddings and summarizes only the K most representative ones. Lower LLM cost; container summary generation is slower the first time it runs.<br />
            <strong>Summary clustering</strong> — summarizes every uploaded document at ingest. Container summary generation reuses those summaries. Higher LLM cost overall; faster container summary generation.
        </div>
    </div>
```

- [ ] **Step 2: Add state field and handler**

In the `@code` block, add the local state field next to the other intermediate fields (`enabled`, `llmProvider`, etc.):

```csharp
    private string containerSummaryMethod = SummaryStrategy.DocumentClustering;
```

Add the change handler next to the other `On*Changed` methods:

```csharp
    private Task OnContainerSummaryMethodChanged(ChangeEventArgs e)
    {
        string? raw = e.Value as string;
        containerSummaryMethod = string.IsNullOrWhiteSpace(raw) ? SummaryStrategy.DocumentClustering : raw;
        return EmitChanged();
    }
```

Update `OnParametersSet` to initialize from the bound settings:

```csharp
    protected override void OnParametersSet()
    {
        enabled = Settings.Enabled;
        llmProvider = Settings.LlmProvider;
        llmModel = Settings.LlmModel;
        maxInputTokens = Settings.MaxInputTokens;
        containerSummaryMethod = Settings.ContainerSummaryMethod;  // NEW
        perDocPromptText = !string.IsNullOrWhiteSpace(Settings.PerDocSystemPrompt)
            ? Settings.PerDocSystemPrompt!
            : SummaryPrompts.PerDocSystemPrompt;
        containerPromptText = !string.IsNullOrWhiteSpace(Settings.ContainerRollupSystemPrompt)
            ? Settings.ContainerRollupSystemPrompt!
            : SummaryPrompts.ContainerRollupSystemPrompt;
    }
```

Update `BuildSettingsFromLocal` to include the new field:

```csharp
    private SummarySettings BuildSettingsFromLocal()
    {
        string? perDoc = IsPerDocAtDefault ? null : perDocPromptText;
        string? containerRollup = IsContainerAtDefault ? null : containerPromptText;

        return new SummarySettings
        {
            Enabled = enabled,
            ContainerSummaryMethod = containerSummaryMethod,  // NEW
            LlmProvider = llmProvider,
            LlmModel = llmModel,
            MaxInputTokens = maxInputTokens,
            PerDocSystemPrompt = perDoc,
            ContainerRollupSystemPrompt = containerRollup
        };
    }
```

- [ ] **Step 3: Build**

```
dotnet build src/Connapse.Web
```
Expected: PASS

- [ ] **Step 4: Commit**

```
git add src/Connapse.Web/Components/Settings/SummarySettingsTab.razor
git commit -m "feat: add Container summary method dropdown to SummarySettingsTab"
```

---

## Task 15: Integration test — DocumentClustering end-to-end

**Files:**
- Create: `tests/Connapse.Web.Tests/Summarization/HerculesIntegrationTests.cs`

- [ ] **Step 1: Examine the existing integration test fixture**

```
grep -rn "SharedWebApp" tests/Connapse.Web.Tests/ | head -20
```
Find an existing end-to-end test (e.g., `SummarySettingsIntegrationTests.cs`) and copy its fixture pattern, including how it uploads docs through the API and waits for ingestion to settle.

- [ ] **Step 2: Write the test**

Create `tests/Connapse.Web.Tests/Summarization/HerculesIntegrationTests.cs`:

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Web.Tests.Summarization;

[Trait("Category", "Integration")]
[Collection("SharedWebApp")]
public class HerculesIntegrationTests
{
    private readonly SharedWebAppFixture _fixture;

    public HerculesIntegrationTests(SharedWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DocumentClusteringMode_NoPerDocSummariesAreGenerated()
    {
        // Arrange: create a container and set it to document-clustering mode
        Guid containerId = await _fixture.CreateContainerAsync("hercules-e2e-1");
        await _fixture.SetContainerSummarySettingsAsync(containerId, new SummarySettings
        {
            Enabled = true,
            ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
        });

        // Act: upload 5 docs (below stuff threshold, so all 5 will be summarized at rollup)
        for (int i = 0; i < 5; i++)
        {
            await _fixture.UploadDocAsync(containerId, $"/doc{i}.txt", $"Sample content for doc {i}.");
        }

        await _fixture.WaitForIngestionCompletionAsync(containerId, expectedDocCount: 5);

        // Assert: per-doc summaries are still null because we didn't trigger a rollup yet
        using var scope = _fixture.Services.CreateScope();
        var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var docs = await docStore.ListAsync(containerId, null, 0, 100, CancellationToken.None);
        docs.Should().HaveCount(5);
        docs.All(d => string.IsNullOrEmpty(d.Summary)).Should().BeTrue(
            "document-clustering mode should not summarize docs at ingest");
    }

    [Fact]
    public async Task DocumentClusteringMode_RollupSummarizesOnlyMedoids()
    {
        Guid containerId = await _fixture.CreateContainerAsync("hercules-e2e-2");
        await _fixture.SetContainerSummarySettingsAsync(containerId, new SummarySettings
        {
            Enabled = true,
            ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
        });

        // Upload 35 docs (>StuffThreshold so we get the clustering regime)
        for (int i = 0; i < 35; i++)
        {
            await _fixture.UploadDocAsync(containerId, $"/doc{i}.txt", $"Sample content for doc {i}.");
        }
        await _fixture.WaitForIngestionCompletionAsync(containerId, expectedDocCount: 35);

        // Act: trigger rollup via the regenerate endpoint
        await _fixture.TriggerRollupAsync(containerId);
        await _fixture.WaitForContainerSummaryAsync(containerId);

        // Assert: container has a summary, and ONLY K medoid docs have per-doc summaries cached
        using var scope = _fixture.Services.CreateScope();
        var containerStore = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var container = await containerStore.GetAsync(containerId, CancellationToken.None);
        container!.Summary.Should().NotBeNullOrWhiteSpace();

        var docs = await docStore.ListAsync(containerId, null, 0, 100, CancellationToken.None);
        int withSummary = docs.Count(d => !string.IsNullOrEmpty(d.Summary));
        int expectedK = Math.Min(20, (int)Math.Ceiling(35.0 / 3.0)); // 12
        withSummary.Should().Be(expectedK,
            "only K medoid docs should be summarized in document-clustering mode");
    }

    [Fact]
    public async Task SummaryClusteringMode_AllDocsGetSummariesAtIngest()
    {
        Guid containerId = await _fixture.CreateContainerAsync("hercules-e2e-3");
        await _fixture.SetContainerSummarySettingsAsync(containerId, new SummarySettings
        {
            Enabled = true,
            ContainerSummaryMethod = SummaryStrategy.SummaryClustering,
        });

        for (int i = 0; i < 5; i++)
        {
            await _fixture.UploadDocAsync(containerId, $"/doc{i}.txt", $"Sample content for doc {i}.");
        }
        await _fixture.WaitForIngestionCompletionAsync(containerId, expectedDocCount: 5);
        // Wait for per-doc summary jobs to settle
        await _fixture.WaitForAllSummariesAsync(containerId, expectedDocCount: 5);

        using var scope = _fixture.Services.CreateScope();
        var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var docs = await docStore.ListAsync(containerId, null, 0, 100, CancellationToken.None);
        docs.Should().HaveCount(5);
        docs.All(d => !string.IsNullOrEmpty(d.Summary)).Should().BeTrue(
            "summary-clustering mode should summarize every doc at ingest");
    }
}
```

If `SetContainerSummarySettingsAsync` / `TriggerRollupAsync` / `WaitForContainerSummaryAsync` / `WaitForAllSummariesAsync` aren't already on `SharedWebAppFixture`, copy the pattern from `SummarySettingsIntegrationTests` (or whichever existing test exercises these endpoints) — typically a `PostAsync("/api/containers/{id}/settings", ...)` and polling loops.

- [ ] **Step 3: Run the tests**

```
dotnet test --filter "FullyQualifiedName~HerculesIntegrationTests"
```
Expected: PASS (may take 1–3 minutes per test because LLM calls happen against the configured provider)

- [ ] **Step 4: Commit**

```
git add tests/Connapse.Web.Tests/Summarization/HerculesIntegrationTests.cs
git commit -m "test: end-to-end document-clustering and summary-clustering modes"
```

---

## Task 16: Integration test — Method switch lazy → eager backfills on ingest

**Files:**
- Modify: `tests/Connapse.Web.Tests/Summarization/HerculesIntegrationTests.cs`

- [ ] **Step 1: Add the switch test to the existing test class**

Append to the test class created in Task 15:

```csharp
    [Fact]
    public async Task MethodSwitch_LazyToEager_BackfillsNewDocsOnIngest()
    {
        Guid containerId = await _fixture.CreateContainerAsync("hercules-switch");
        await _fixture.SetContainerSummarySettingsAsync(containerId, new SummarySettings
        {
            Enabled = true,
            ContainerSummaryMethod = SummaryStrategy.DocumentClustering,
        });

        // Upload one doc under document-clustering — no per-doc summary
        await _fixture.UploadDocAsync(containerId, "/doc-lazy.txt", "Content in lazy mode.");
        await _fixture.WaitForIngestionCompletionAsync(containerId, expectedDocCount: 1);

        using (var scope = _fixture.Services.CreateScope())
        {
            var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var d = (await docStore.ListAsync(containerId, null, 0, 100, CancellationToken.None)).Single();
            d.Summary.Should().BeNullOrEmpty();
        }

        // Switch to summary-clustering
        await _fixture.SetContainerSummarySettingsAsync(containerId, new SummarySettings
        {
            Enabled = true,
            ContainerSummaryMethod = SummaryStrategy.SummaryClustering,
        });

        // Upload another doc — should be summarized at ingest now
        await _fixture.UploadDocAsync(containerId, "/doc-eager.txt", "Content in eager mode.");
        await _fixture.WaitForIngestionCompletionAsync(containerId, expectedDocCount: 2);
        await _fixture.WaitForDocSummaryAsync(containerId, "/doc-eager.txt");

        using (var scope = _fixture.Services.CreateScope())
        {
            var docStore = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var docs = await docStore.ListAsync(containerId, null, 0, 100, CancellationToken.None);
            var lazy = docs.Single(d => d.Path == "/doc-lazy.txt");
            var eager = docs.Single(d => d.Path == "/doc-eager.txt");

            lazy.Summary.Should().BeNullOrEmpty(
                "doc uploaded in lazy mode keeps its null summary");
            eager.Summary.Should().NotBeNullOrEmpty(
                "doc uploaded after switch to eager mode should have a summary");
        }
    }
```

- [ ] **Step 2: Run the test**

```
dotnet test --filter "FullyQualifiedName~HerculesIntegrationTests.MethodSwitch_LazyToEager_BackfillsNewDocsOnIngest"
```
Expected: PASS

- [ ] **Step 3: Commit**

```
git add tests/Connapse.Web.Tests/Summarization/HerculesIntegrationTests.cs
git commit -m "test: switching from document-clustering to summary-clustering backfills new docs"
```

---

## Task 17: Manual QA doc — add document-clustering scenarios

**Files:**
- Modify: `docs/manual-qa/summary-generation.md` (or whichever manual-QA file the prior cycles updated)

- [ ] **Step 1: Locate the existing manual QA doc**

```
grep -rn "summary" docs/manual-qa/ 2>/dev/null || find docs -name "*qa*" -o -name "*manual*"
```

Look for the file that prior summary-related cycles (PR #329, the Hangfire migration) extended. If multiple candidates exist, use the most-recently-modified one.

- [ ] **Step 2: Append a new section**

Add this section to the bottom of the file (adjust heading level to match the surrounding doc):

```markdown
## HERCULES — Document-clustering container summary method

The default container summary method for new installs is `document-clustering`. The legacy method is `summary-clustering`. Verify both end-to-end.

### Scenario 1: Document-clustering default for new container

1. Create a fresh container "qa-hercules-default".
2. Open Settings tab → Container summary method should show "Document clustering (recommended)" by default.
3. Upload 5 documents (any text files).
4. Wait for ingestion to complete (per-doc spinners stop).
5. **Expected:** Each doc's row shows no "summary" indicator. The container row shows no summary yet (rollup hasn't happened).
6. Click "Regenerate summary now".
7. Wait ~30s for the rollup to complete.
8. **Expected:** Container summary appears. Each doc's row STILL shows no per-doc summary (N=5 ≤ stuff threshold of 30, so all 5 docs were summarized inline at rollup but cached against their own row — open the doc detail panel and confirm summary IS now present).

### Scenario 2: Document-clustering clustering regime (>30 docs)

1. Create container "qa-hercules-cluster".
2. Upload 35 documents.
3. Wait for ingestion, click "Regenerate summary now".
4. **Expected:** Container summary appears. Open the FileBrowser doc list — exactly K = `min(20, ceil(35/3))` = 12 docs have summaries; the remaining 23 do not.

### Scenario 3: Switch to summary-clustering

1. Open container "qa-hercules-default" settings.
2. Change Container summary method to "Summary clustering". Save.
3. Upload one new document "doc-after-switch.txt".
4. Wait for ingestion.
5. **Expected:** "doc-after-switch.txt" gets a per-doc summary at ingest (open detail panel to confirm). The 5 docs uploaded before the switch still don't have per-doc summaries (lazy-mode cache state preserved).

### Scenario 4: Cache reuse on re-rollup

1. In container "qa-hercules-cluster" (from Scenario 2), click "Regenerate summary now" a second time WITHOUT uploading anything.
2. **Expected:** Rollup completes quickly. Watch the Hangfire dashboard / logs — there should be **zero** LLM calls for per-doc summaries this time (all 12 medoid docs have cached summaries with matching content_hash).
```

- [ ] **Step 3: Commit**

```
git add docs/manual-qa/summary-generation.md
git commit -m "docs: add manual QA scenarios for document-clustering summary method"
```

---

## Task 18: Push branch + open PR + final code review

**Files:**
- N/A

- [ ] **Step 1: Full build + test**

```
dotnet build
dotnet test --filter "Category=Unit"
```
Expected: all green.

```
dotnet test --filter "Category=Integration"
```
Expected: all green (requires Docker for Testcontainers + a running LLM provider configured in test settings).

- [ ] **Step 2: Push the branch**

Replace `<N>` with the issue number from Task 1:
```
git push -u origin feature/<N>-hercules-swap
```

- [ ] **Step 3: Open the PR**

```
gh pr create \
  --base main \
  --title "feat: add document-clustering container summary method (HERCULES) (closes #<N>)" \
  --body "$(cat <<'EOF'
## Summary

Adds a new \`document-clustering\` value for \`SummarySettings.ContainerSummaryMethod\` and makes it the new default. The method clusters documents using mean-pooled chunk embeddings (which already exist in \`chunk_vectors\` for vector search) and lazy-summarizes only the K medoid documents at rollup time. Existing \`summary-clustering\` behavior remains available behind the setting.

Built on research:
- HERCULES paper (arXiv:2506.19992): +76% ARI on clustering quality vs summary embeddings
- \`docs/research/lazy-vs-eager-per-doc-summarization-2026-05-26.md\`

Design: \`docs/superpowers/specs/2026-05-27-hercules-swap-design.md\`
Plan: \`docs/superpowers/plans/2026-05-27-hercules-swap.md\`

## What changed

- New \`SummaryStrategy\` constants (\`document-clustering\` | \`summary-clustering\`)
- \`SummarySettings.ContainerSummaryMethod\` field, G+C scope through existing resolver, default \`document-clustering\`
- New \`IVectorStore.GetPooledDocumentEmbeddingsAsync\` (PgVectorStore impl uses pgvector \`AVG(embedding)\` over chunk vectors, filtered to dominant model)
- \`IngestionJobs.PerDocSummaryAsync\` early-returns in \`document-clustering\` mode
- \`SummaryJobs.RollupContainerAsync\` branches: existing path unchanged for \`summary-clustering\`; new lazy path for \`document-clustering\`
- \`ComputeDocSetHash\` switched from summary-text to content-hash (works for both methods)
- Settings UI dropdown
- Unit + integration test coverage for both paths

## Migration impact

- Existing containers continue working under whichever method they're set to. The \`ContainerSummaryMethod\` default of \`document-clustering\` only applies to new \`SummarySettings\` records.
- \`ComputeDocSetHash\` formula changed → one extra rollup per container on first run post-deploy. Accepted one-shot cost.
- No DB schema changes.

## Test plan

- [ ] \`dotnet test --filter Category=Unit\` all green
- [ ] \`dotnet test --filter Category=Integration\` all green (Docker required)
- [ ] Manual QA per \`docs/manual-qa/summary-generation.md\` HERCULES scenarios 1–4
EOF
)"
```

- [ ] **Step 4: Request CodeRabbit review on the PR**

```
gh pr comment --body "/coderabbit review"
```

Wait for the review, then triage findings: implement reasonable ones inline, dismiss noise.

- [ ] **Step 5: Mark task complete and stop here**

Do not merge. Patrick reviews + merges manually after CI passes.

---

## Self-Review

Spec coverage check (against `docs/superpowers/specs/2026-05-27-hercules-swap-design.md`):

| Spec requirement | Implemented in |
|---|---|
| `SummarySettings.ContainerSummaryMethod` field with default `document-clustering` | Task 3 |
| Validator rejects unknown values | Task 3 |
| Resolves through `ContainerSettingsResolver` (G+C) | Task 4 (test only — resolver already handles arbitrary `Summary` fields) |
| `SummaryStrategy` constants | Task 2 |
| `IVectorStore.GetPooledDocumentEmbeddingsAsync` interface + impl | Tasks 5, 6 |
| Pooled-embedding SQL with `AVG`, mixed-model warning, L2 normalize | Task 6 |
| Empty-chunk doc exclusion | Task 6 (handled implicitly by `GROUP BY` on `chunk_vectors`) |
| `PerDocSummaryAsync` early-return in document-clustering | Task 8 |
| `RollupContainerAsync` branches per method | Task 12 |
| Lazy summarization of K medoids with cache hit check | Task 12 |
| `ComputeDocSetHash` uses content_hash | Task 10 |
| UI dropdown in `SummarySettingsTab.razor` | Task 14 |
| Unit tests for early-return | Task 9 |
| Unit tests for routing | Task 13 |
| Unit tests for cache hit | Task 13 |
| Unit tests for hash function | Task 11 |
| Integration test: DocumentClustering end-to-end | Task 15 |
| Integration test: SummaryClustering end-to-end | Task 15 |
| Integration test: Method switch | Task 16 |
| Integration test: Pooled embeddings mixed-model filter | Task 7 |
| Manual QA scenarios | Task 17 |

All spec sections have task coverage. The "RollupDocumentClusteringAsync only writes K cache entries" claim from Task 15 is what the integration test in Task 15 verifies.

**One known simplification vs spec:** Spec section "New components → ContainerSummarizer" implies cluster annotations like "(represents N similar docs)" might appear in the lazy path's reduce prompt. In this plan, lazy mode passes K ≤ 20 doc summaries to `ContainerSummarizer.GenerateAsync`, which (because N ≤ `StuffThreshold = 30`) takes its stuff path and does NOT add cluster-size prefixes. The reduce prompt receives K summaries without annotation. This is documented as Open Question / follow-up in the spec, not a regression. If post-merge experimentation shows quality suffers, the follow-up is a small `ContainerSummarizer` change to accept pre-clustered input with sizes.
