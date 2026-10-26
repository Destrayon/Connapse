using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Links;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>The PostgreSQL link layer keeps the permission-leak contract, unchanged.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class PostgresLinkReaderContractTests(SharedWebAppFixture fixture) : LinkReaderContract
{
    protected override LinkReaderFactory Factory => async (world, access) =>
    {
        // Not disposed: the reader returned below outlives this lambda and keeps using the scope's factory.
        IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDbContextFactory<KnowledgeDbContext> contexts =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await contexts.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("TRUNCATE link_edges, link_node_facts, link_node_documents");

        await new PostgresLinkStore(contexts).ReplaceAsync(ToDocuments(world));
        return new PostgresLinkReader(contexts, access);
    };

    /// <summary>The world as its documents would write it: each document's names, kinds, summaries and links.</summary>
    internal static List<DocumentLinks> ToDocuments(LinkWorld world)
    {
        IEnumerable<(string Doc, LinkFact Fact)> facts = world.Nodes.SelectMany(n =>
            n.Kinds.Select(t => (t.DocumentId, new LinkFact(n.Id, LinkFactField.Kind, t.Text)))
                .Concat(n.Names.Select(t => (t.DocumentId, new LinkFact(n.Id, LinkFactField.Name, t.Text))))
                .Concat(n.Summaries.Select(t => (t.DocumentId, new LinkFact(n.Id, LinkFactField.Summary, t.Text)))));
        IEnumerable<(string Doc, LinkAssertion Link)> links = world.Edges.SelectMany(e =>
            e.Evidence.Select(x => (x.DocumentId, new LinkAssertion(e.Id, e.FromId, e.ToId, e.Type, x.Text))));

        ILookup<string, LinkFact> factsByDoc = facts.ToLookup(x => x.Doc, x => x.Fact);
        ILookup<string, LinkAssertion> linksByDoc = links.ToLookup(x => x.Doc, x => x.Link);
        return factsByDoc.Select(g => g.Key).Union(linksByDoc.Select(g => g.Key))
            .Select(doc => new DocumentLinks(doc, null, [.. factsByDoc[doc]], [.. linksByDoc[doc]]))
            .ToList();
    }
}

/// <summary>Deleting a document removes exactly what it asserted, however it is deleted.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class LinkCleanupTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task DeleteDocument_RemovesOnlyTheLinksItAsserted()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDbContextFactory<KnowledgeDbContext> contexts =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext db = await contexts.CreateDbContextAsync();

        Connapse.Storage.Data.Entities.ContainerEntity container = new() { Id = Guid.NewGuid(), Name = $"c-{Guid.NewGuid():N}" };
        db.Containers.Add(container);
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid id in ids)
            db.Documents.Add(new Connapse.Storage.Data.Entities.DocumentEntity
            {
                Id = id,
                ContainerId = container.Id,
                FileName = $"{id}.md",
                Path = $"/{id}.md",
                ContentHash = id.ToString("N"),
                CreatedAt = DateTime.UtcNow,
                Metadata = [],
            });
        await db.SaveChangesAsync();

        string gone = ids[0].ToString(), kept = ids[1].ToString();
        await new PostgresLinkStore(contexts).ReplaceAsync([
            new DocumentLinks(gone, $"n:{gone}", [new LinkFact($"n:{gone}", LinkFactField.Name, "issue")],
                [new LinkAssertion($"l:{gone}", $"n:{gone}", $"n:{kept}", "closes")]),
            new DocumentLinks(kept, $"n:{kept}", [new LinkFact($"n:{kept}", LinkFactField.Name, "pr")],
                [new LinkAssertion($"l:{gone}", $"n:{gone}", $"n:{kept}", "closes")]),
        ]);

        db.Documents.Remove(await db.Documents.SingleAsync(d => d.Id == ids[0]));
        await db.SaveChangesAsync();

        string[] both = [gone, kept];
        (await db.LinkEdges.Where(e => both.Contains(e.DocumentId)).Select(e => e.DocumentId).ToListAsync())
            .Should().Equal(kept);
        (await db.LinkNodeFacts.Where(e => both.Contains(e.DocumentId)).Select(e => e.DocumentId).ToListAsync())
            .Should().Equal(kept);
        (await db.LinkNodeDocuments.Where(e => both.Contains(e.DocumentId)).Select(e => e.DocumentId).ToListAsync())
            .Should().Equal(kept);
    }
}
