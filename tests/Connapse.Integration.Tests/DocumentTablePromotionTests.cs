using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-312: ingesting a document loads its clean tables into its container and links each passage to the tables in it.
/// A re-ingest keeps the tables' ids and an admin's hides, and one that fails leaves the tables and links as they were.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DocumentTablePromotionTests(SharedWebAppFixture fixture)
{
    private const string Prices = "| Item | Price |\n|---|---|\n| Tea | 3 |\n| Cake | 5 |";
    private const string Staff = "| Name | Role |\n|---|---|\n| Ada | Lead |\n| Lin | Dev |";
    private const string Rooms = "| Room | Seats |\n|---|---|\n| North | 12 |\n| South | 8 |";
    private const string OneRow = "| Note | Value |\n|---|---|\n| only | row |";

    [Fact]
    public async Task Ingest_MarkdownWithTables_LoadsTheCleanOnesAndLinksTheirPassages()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid containerId = await NewContainerAsync(scope);
        try
        {
            Guid documentId = await IngestAsync(scope, containerId, null, $"# Menu\n\n{Prices}\n\nA callout:\n\n{OneRow}\n\nWho works here:\n\n{Staff}\n");

            var tables = await scope.ServiceProvider.GetRequiredService<ITableStore>().ListByDocumentAsync(containerId, documentId);
            tables.Select(t => t.Name).Should().BeEquivalentTo(["menu, table 1", "menu, table 3"]);
            tables.Should().OnlyContain(t => t.IsFromPassage && !t.Hidden);
            tables.Select(t => t.Placement!.Number).Should().BeEquivalentTo([1, 3]);

            var linked = await LinkedTextAsync(scope, documentId);
            linked.Should().HaveCount(2);
            linked[tables.Single(t => t.Placement!.Number == 1).Id].Should().Be(Prices);
            linked[tables.Single(t => t.Placement!.Number == 3).Id].Should().Be(Staff);
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task Ingest_ReIngestWithATableInsertedBefore_KeepsIdsAndHidesAndRelinks()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid containerId = await NewContainerAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            Guid documentId = await IngestAsync(scope, containerId, null, $"{Prices}\n\ntext\n\n{Staff}\n");
            var first = await store.ListByDocumentAsync(containerId, documentId);
            Guid staffId = first.Single(t => t.Placement!.Number == 2).Id;
            await store.SetHiddenAsync(containerId, staffId, true);

            await IngestAsync(scope, containerId, documentId, $"{Rooms}\n\ntext\n\n{Prices}\n\ntext\n\n{Staff}\n");

            var second = await store.ListByDocumentAsync(containerId, documentId);
            second.Should().HaveCount(3);
            second.Select(t => t.Id).Should().Contain(first.Select(t => t.Id));
            second.Single(t => t.Id == staffId).Placement!.Number.Should().Be(3);
            second.Single(t => t.Id == staffId).Hidden.Should().BeTrue();
            var linked = await LinkedTextAsync(scope, documentId);
            linked.Should().HaveCount(3);
            linked[staffId].Should().Be(Staff);
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task Ingest_ReIngestThatFails_LeavesTheTablesHidesAndLinks()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid containerId = await NewContainerAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            Guid documentId = await IngestAsync(scope, containerId, null, $"{Prices}\n\ntext\n\n{Staff}\n");
            var before = await store.ListByDocumentAsync(containerId, documentId);
            await store.SetHiddenAsync(containerId, before[0].Id, true);

            Func<Task> act = () => IngestAsync(scope, containerId, documentId, "   \n  ");

            await act.Should().ThrowAsync<Exception>();
            var after = await store.ListByDocumentAsync(containerId, documentId);
            after.Select(t => (t.Id, t.Hidden)).Should().BeEquivalentTo(before.Select(t => (t.Id, t.Id == before[0].Id)));
            (await LinkedTextAsync(scope, documentId)).Should().HaveCount(2);
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    [Fact]
    public async Task Ingest_PlainTextFile_LoadsNoTables()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid containerId = await NewContainerAsync(scope);
        try
        {
            Guid documentId = await IngestAsync(scope, containerId, null, $"{Prices}\n", "notes.txt");

            (await scope.ServiceProvider.GetRequiredService<ITableStore>().ListByDocumentAsync(containerId, documentId))
                .Should().BeEmpty();
        }
        finally
        {
            await DeleteAsync(scope, containerId);
        }
    }

    private static async Task<Guid> IngestAsync(
        AsyncServiceScope scope, Guid containerId, Guid? documentId, string text, string fileName = "menu.md")
    {
        // The upload endpoint creates the document's row before its ingestion job runs; so does this.
        if (documentId is null)
        {
            documentId = Guid.NewGuid();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
                + "VALUES ({0}, {1}, NULL, {2}, {3}, '', 1, 'Queued', now())",
                documentId, containerId, fileName, $"/{fileName}");
        }
        var result = await scope.ServiceProvider.GetRequiredService<IKnowledgeIngester>().IngestAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(text)),
            new IngestionOptions(DocumentId: documentId?.ToString(), FileName: fileName, ContentType: "text/markdown", Path: $"/{fileName}")
            {
                Owner = OwnerRef.ForContainer(containerId),
            });
        result.Warnings.Should().BeEmpty();
        return Guid.Parse(result.DocumentId);
    }

    /// <summary>Each linked table's span, read out of its passage.</summary>
    private static async Task<Dictionary<Guid, string>> LinkedTextAsync(AsyncServiceScope scope, Guid documentId)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        var chunks = await context.Chunks.AsNoTracking().Where(c => c.DocumentId == documentId)
            .ToDictionaryAsync(c => c.Id, c => c.Content);
        var links = await scope.ServiceProvider.GetRequiredService<ITablePassageLinks>().ForChunksAsync(chunks.Keys);
        return links.Values.SelectMany(l => l)
            .GroupBy(l => l.TableId)
            .ToDictionary(g => g.Key, g => string.Join("", g.Select(l => chunks[l.ChunkId].Substring(l.Start, l.Length))));
    }

    private static async Task<Guid> NewContainerAsync(AsyncServiceScope scope)
    {
        var container = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"promo-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        return Guid.Parse(container.Id);
    }

    private static async Task DeleteAsync(AsyncServiceScope scope, Guid containerId)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM documents WHERE container_id = {0}", containerId);
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }
}
