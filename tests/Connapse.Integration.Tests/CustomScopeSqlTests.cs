using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Keyword;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// A custom source's grant is its <c>custom://</c> prefix plus the access keys the user holds: a document
/// is ranked only when its keys overlap them. Asserted against the real keyword and vector SQL, negatively —
/// a key filter that does not filter would return everything and pass every positive assertion.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CustomScopeSqlTests(SharedWebAppFixture fixture)
{
    private const string Term = "quuxbarnacle";
    private const string Model = "custom-scope-model";
    private static readonly Guid SourceA = Guid.NewGuid();

    private static string Prefix => Connapse.Core.Utilities.CustomResourceUri.SourcePrefix(SourceA);

    private async Task<(Guid ContainerId, Dictionary<string, Guid> Documents)> SeedAsync()
    {
        HttpResponseMessage response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"cs-{Guid.NewGuid():N}"[..20] });
        response.EnsureSuccessStatusCode();
        Guid containerId = Guid.Parse((await response.Content.ReadFromJsonAsync<ContainerDto>())!.Id);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await using KnowledgeDbContext db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();

        Dictionary<string, Guid> documents = [];
        foreach ((string name, string uri, string[]? keys) in new (string, string, string[]?)[]
                 {
                     ("eng.md", Prefix + "eng", ["team:eng"]),
                     ("ops.md", Prefix + "ops", ["team:ops"]),
                     ("both.md", Prefix + "both", ["team:eng", "team:ops"]),
                     ("other-source.md", Connapse.Core.Utilities.CustomResourceUri.SourcePrefix(Guid.NewGuid()) + "x", ["team:eng"]),
                 })
        {
            Guid id = Guid.NewGuid();
            documents[name] = id;
            db.Documents.Add(new DocumentEntity
            {
                Id = id,
                ContainerId = containerId,
                FileName = name,
                Path = "/" + name,
                ResourceUri = uri,
                AccessKeys = keys,
                ContentHash = Guid.NewGuid().ToString("N"),
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            });
            Guid chunkId = Guid.NewGuid();
            db.Chunks.Add(new ChunkEntity
            {
                Id = chunkId,
                DocumentId = id,
                OwnerId = containerId,
                ChunkIndex = 0,
                Content = $"the {Term} is here",
                TokenCount = 4,
                StartOffset = 0,
                EndOffset = 16,
                Metadata = [],
            });
            db.ChunkVectors.Add(new ChunkVectorEntity
            {
                ChunkId = chunkId,
                DocumentId = id,
                OwnerId = containerId,
                Embedding = new Vector(new[] { 1f, 0f, 0f }),
                ModelId = Model,
                ContentHash = $"hash-{chunkId:N}",
                Dimensions = 3,
            });
        }

        await db.SaveChangesAsync();
        return (containerId, documents);
    }

    private static SearchScopes Keys(params string[] keys) =>
        SearchScopes.Of([new GrantMatch(Prefix, IsExact: false, AccessKeys: keys)]);

    private async Task<HashSet<string>> KeywordAsync(Guid containerId, SearchScopes scopes)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await using KnowledgeDbContext db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        IReadOnlyList<SearchHit> hits = await new KeywordSearchService(db, NullLogger<KeywordSearchService>.Instance)
            .SearchAsync(Term, new SearchOptions(TopK: 20, ContainerId: containerId.ToString(), Mode: SearchMode.Keyword), scopes);
        return hits.Select(h => h.Metadata.GetValueOrDefault("fileName") ?? "").ToHashSet();
    }

    private async Task<HashSet<Guid>> VectorAsync(Guid containerId, SearchScopes scopes)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IVectorStore store = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        IReadOnlyList<VectorSearchResult> hits = await store.SearchAsync(
            [1f, 0f, 0f], 20, new Dictionary<string, string> { ["containerId"] = containerId.ToString(), ["modelId"] = Model }, scopes);
        return hits.Select(h => Guid.Parse(h.Metadata["documentId"])).ToHashSet();
    }

    [Fact]
    public async Task Keyword_RanksOnlyDocumentsWhoseKeysTheUserHolds()
    {
        (Guid containerId, _) = await SeedAsync();

        (await KeywordAsync(containerId, Keys("team:eng"))).Should().BeEquivalentTo("eng.md", "both.md");
        (await KeywordAsync(containerId, Keys("team:sales"))).Should().BeEmpty();
    }

    [Fact]
    public async Task Keyword_BarePrefix_LeavesTheDecisionToTheCheck()
    {
        (Guid containerId, _) = await SeedAsync();

        (await KeywordAsync(containerId, SearchScopes.OfPrefixes([Prefix]))).Should().BeEquivalentTo("eng.md", "ops.md", "both.md");
    }

    [Fact]
    public async Task Vector_RanksOnlyDocumentsWhoseKeysTheUserHolds()
    {
        (Guid containerId, Dictionary<string, Guid> documents) = await SeedAsync();

        (await VectorAsync(containerId, Keys("team:ops"))).Should().BeEquivalentTo([documents["ops.md"], documents["both.md"]]);
        (await VectorAsync(containerId, Keys("team:sales"))).Should().BeEmpty();
    }

    private record ContainerDto(string Id, string Name);
}
