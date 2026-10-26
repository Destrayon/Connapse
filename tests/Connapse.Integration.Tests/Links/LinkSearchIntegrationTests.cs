using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Hybrid;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>
/// The links search through the real search pipeline: off changes nothing, on without links changes nothing,
/// on with links adds the linked record, and the walk never passes through a document the search cannot reach.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class LinkSearchIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Term = "quarvelinth";

    private sealed class FixedSettings(SearchSettings value) : IOptionsMonitor<SearchSettings>
    {
        public SearchSettings CurrentValue => value;
        public SearchSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<SearchSettings, string?> listener) => null;
    }

    private HybridSearchService Search(bool linksOn) => new(
        fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
        [],
        new FixedSettings(new SearchSettings { EnableLinkSearch = linksOn, KeywordRanker = "TsRank" }),
        NullLogger<HybridSearchService>.Instance);

    private static SearchOptions Keyword(Guid container) =>
        new(TopK: 10, ContainerId: container.ToString(), Mode: SearchMode.Keyword);

    /// <summary>A container with the named documents, each one chunk; returns the container and document ids.</summary>
    private async Task<(Guid Container, Dictionary<string, Guid> Docs)> SeedAsync(params (string Name, string Text)[] docs)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using KnowledgeDbContext db = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        ContainerEntity container = new() { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        Dictionary<string, Guid> ids = [];
        foreach ((string name, string text) in docs)
        {
            DocumentEntity document = new()
            {
                Id = Guid.NewGuid(),
                ContainerId = container.Id,
                FileName = name,
                Path = "/" + name,
                ContentHash = Guid.NewGuid().ToString("N"),
                IngestionStatus = DocumentStatus.Ready,
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            };
            db.Documents.Add(document);
            db.Chunks.Add(new ChunkEntity
            {
                Id = Guid.NewGuid(),
                DocumentId = document.Id,
                OwnerId = container.Id,
                Content = text,
                ChunkIndex = 0,
                Metadata = [],
            });
            ids[name] = document.Id;
        }
        await db.SaveChangesAsync();
        return (container.Id, ids);
    }

    private async Task LinkAsync(params DocumentLinks[] documents)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILinkWriter>().ReplaceAsync(documents);
    }

    private static DocumentLinks Record(Guid doc, string node, params LinkAssertion[] links) =>
        new(doc.ToString(), node, [new LinkFact(node, LinkFactField.Name, node)], links);

    [Fact]
    public async Task Search_LinkLayerOnWithoutLinks_IdenticalToOff()
    {
        (Guid container, _) = await SeedAsync(
            ("one.md", $"the {Term} engine stalls"), ("two.md", $"{Term} {Term} restart"), ("three.md", "unrelated text"));

        SearchResult off = await Search(linksOn: false).SearchAsync(Term, Keyword(container));
        SearchResult on = await Search(linksOn: true).SearchAsync(Term, Keyword(container));

        off.Hits.Should().HaveCount(2);
        on.Hits.Select(h => (h.ChunkId, h.Score, h.Content)).Should().Equal(off.Hits.Select(h => (h.ChunkId, h.Score, h.Content)));
        on.Hits.Select(h => h.Metadata).Should().BeEquivalentTo(off.Hits.Select(h => h.Metadata), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Search_LinkLayerOn_AddsThePullRequestThatClosedTheIssue()
    {
        (Guid container, Dictionary<string, Guid> docs) = await SeedAsync(
            ("issue.md", $"the {Term} engine stalls on start"), ("pr.md", "rewrite the frobnicator warm-up"), ("other.md", "nothing here"));
        string issue = $"I-{Guid.NewGuid():N}", pr = $"P-{Guid.NewGuid():N}";
        await LinkAsync(
            Record(docs["issue.md"], issue),
            Record(docs["pr.md"], pr, new LinkAssertion($"{pr}-closes", pr, issue, "closes")));

        SearchResult off = await Search(linksOn: false).SearchAsync(Term, Keyword(container));
        SearchResult on = await Search(linksOn: true).SearchAsync(Term, Keyword(container));

        off.Hits.Select(h => h.DocumentId).Should().Equal(docs["issue.md"].ToString());
        on.Hits.Select(h => h.DocumentId).Should().Equal(docs["issue.md"].ToString(), docs["pr.md"].ToString());
        on.Hits[1].Metadata["source"].Should().Be("links");
    }

    [Fact]
    public async Task Search_LinkThroughAnotherContainersDocument_ReachesNeitherItNorBeyond()
    {
        (Guid container, Dictionary<string, Guid> docs) = await SeedAsync(
            ("issue.md", $"the {Term} engine stalls"), ("file.md", "frobnicator source"));
        (_, Dictionary<string, Guid> elsewhere) = await SeedAsync(("pr.md", "private pull request"));
        string issue = $"I-{Guid.NewGuid():N}", pr = $"P-{Guid.NewGuid():N}", file = $"F-{Guid.NewGuid():N}";
        // Both links have evidence in this container (the issue and the file each name the pull request).
        await LinkAsync(
            Record(docs["issue.md"], issue, new LinkAssertion($"{pr}-closes", pr, issue, "closes")),
            Record(docs["file.md"], file, new LinkAssertion($"{pr}-changes", pr, file, "changes")),
            Record(elsewhere["pr.md"], pr));

        SearchResult on = await Search(linksOn: true).SearchAsync(Term, Keyword(container));

        on.Hits.Select(h => h.DocumentId).Should().Equal(docs["issue.md"].ToString());
    }
}
