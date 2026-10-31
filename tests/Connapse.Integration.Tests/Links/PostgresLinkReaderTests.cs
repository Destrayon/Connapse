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

        await scope.ServiceProvider.GetRequiredService<PostgresLinkStore>().ReplaceAsync(ToDocuments(world));
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
        await scope.ServiceProvider.GetRequiredService<PostgresLinkStore>().ReplaceAsync([
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

/// <summary>Rows that share a link id but disagree on its ends are not one link; neither opens.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ConflictingLinkRowsTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ExpandAsync_RowsOfOneIdWithDifferentEnds_WalkNeither()
    {
        IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDbContextFactory<KnowledgeDbContext> contexts =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        string run = Guid.NewGuid().ToString("N");
        string a = $"a-{run}", b = $"b-{run}", c = $"c-{run}", pub = $"pub-{run}", secret = $"secret-{run}";
        await scope.ServiceProvider.GetRequiredService<PostgresLinkStore>().ReplaceAsync([
            new DocumentLinks(a, null, [new LinkFact(a, LinkFactField.Name, "a"), new LinkFact(b, LinkFactField.Name, "b"),
                new LinkFact(c, LinkFactField.Name, "c")], []),
            new DocumentLinks(pub, null, [], [new LinkAssertion($"l-{run}", a, b, "closes")]),
            new DocumentLinks(secret, null, [], [new LinkAssertion($"l-{run}", a, c, "closes")]),
        ]);
        DocumentAccessStub access = new();
        Guid reader = Guid.NewGuid();
        access.Grant(reader, [a, pub]);

        LinkExpansion answer = await new PostgresLinkReader(contexts, access)
            .ExpandAsync(new LinkExpandRequest(reader, [a], MaxHops: 2));

        answer.Nodes.Select(n => n.Id).Should().Equal(a);
        answer.Edges.Should().BeEmpty();
    }
}

/// <summary>A cached "no links" never hides links written after it.</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class LinkIndexAnyTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task AnyAsync_CachedEmptyThenAWrite_SeesTheWriteAtOnce()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        await using (KnowledgeDbContext context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("TRUNCATE link_edges, link_node_facts, link_node_documents");
        PostgresLinkStore store = scope.ServiceProvider.GetRequiredService<PostgresLinkStore>();
        // The truncate went around the store, so an earlier "yes" may still be cached; a write clears either.
        await store.ReplaceAsync([new DocumentLinks($"d-{Guid.NewGuid():N}", null, [], [])]);
        (await store.AnyAsync()).Should().BeFalse();

        string doc = $"d-{Guid.NewGuid():N}";
        await store.ReplaceAsync([new DocumentLinks(doc, $"n-{doc}", [new LinkFact($"n-{doc}", LinkFactField.Name, "n")], [])]);

        (await store.AnyAsync()).Should().BeTrue();
    }
}
