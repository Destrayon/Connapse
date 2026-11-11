using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-312: the tables found inside a document are synced as a set, keyed by their placement, so a re-ingest reloads
/// the same tables, keeping their ids and an admin's hides, and a failed sync leaves the old ones as they were.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DocumentTableSyncTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Sync_ReIngestWithATableInsertedBefore_KeepsIdsAndHides()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, documentId) = await NewDocumentAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            var first = await store.SyncDocumentTablesAsync(containerId, documentId,
                [Load(documentId, new(1, 1, "aaaa"), "Prices"), Load(documentId, new(2, 2, "bbbb"), "Staff")]);
            await store.SetHiddenAsync(containerId, first[1].Id, true);
            await store.SetHiddenColumnsAsync(containerId, first[0].Id, ["Value"]);

            var second = await store.SyncDocumentTablesAsync(containerId, documentId,
                [Load(documentId, new(1, 1, "cccc"), "New"), Load(documentId, new(1, 2, "aaaa"), "Prices"),
                 Load(documentId, new(2, 3, "bbbb"), "Staff")]);

            second[1].Id.Should().Be(first[0].Id);
            second[1].Placement.Should().Be(new TablePlacement(1, 2, "aaaa"));
            second[1].HiddenColumns.Should().Equal("Value");
            second[2].Id.Should().Be(first[1].Id);
            second[2].Hidden.Should().BeTrue();
            second[0].Id.Should().NotBe(first[0].Id).And.NotBe(first[1].Id);
            second[0].Hidden.Should().BeFalse();
            (await store.ListByDocumentAsync(containerId, documentId)).Select(t => t.Name).Should().Equal("New", "Prices", "Staff");
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Sync_TableGone_DeletesItButNotTheDocumentsOtherTables()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, documentId) = await NewDocumentAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            TableInfo own = await store.CreateAsync(containerId, Load(documentId, null, "FromFile"));
            await store.SyncDocumentTablesAsync(containerId, documentId,
                [Load(documentId, new(1, 1, "aaaa"), "A"), Load(documentId, new(1, 2, "bbbb"), "B")]);

            await store.SyncDocumentTablesAsync(containerId, documentId, [Load(documentId, new(1, 1, "aaaa"), "A")]);

            (await store.ListByDocumentAsync(containerId, documentId)).Select(t => t.Name)
                .Should().BeEquivalentTo(["FromFile", "A"]);
            (await store.GetAsync(containerId, own.Id))!.IsFromPassage.Should().BeFalse();
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Sync_FailsPartWay_LeavesTheOldTables()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, documentId) = await NewDocumentAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            var first = await store.SyncDocumentTablesAsync(containerId, documentId, [Load(documentId, new(1, 1, "aaaa"), "A")]);
            await store.SetHiddenAsync(containerId, first[0].Id, true);

            var broken = Load(documentId, new(1, 2, "bbbb"), "B") with { Rows = Rows([["x", "not a number"]]) };
            Func<Task> act = () => store.SyncDocumentTablesAsync(containerId, documentId,
                [Load(documentId, new(1, 1, "aaaa"), "Renamed"), broken]);

            await act.Should().ThrowAsync<ArgumentException>();
            var after = await store.ListByDocumentAsync(containerId, documentId);
            after.Should().ContainSingle();
            after[0].Id.Should().Be(first[0].Id);
            after[0].Name.Should().Be("A");
            after[0].Hidden.Should().BeTrue();
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Sync_LoadWithoutPlacement_Throws()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
        Guid documentId = Guid.NewGuid();

        Func<Task> act = () => store.SyncDocumentTablesAsync(Guid.NewGuid(), documentId, [Load(documentId, null, "A")]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Links_ReplaceAndRead_RoundTripAndGoWithTheirTable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, documentId) = await NewDocumentAsync(scope);
        try
        {
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            var links = scope.ServiceProvider.GetRequiredService<ITablePassageLinks>();
            var tables = await store.SyncDocumentTablesAsync(containerId, documentId,
                [Load(documentId, new(1, 1, "aaaa"), "A"), Load(documentId, new(1, 2, "bbbb"), "B")]);
            Guid chunk = await NewChunkAsync(scope, documentId, containerId);

            await links.ReplaceAsync(containerId, documentId,
                [new(tables[1].Id, chunk, 40, 10), new(tables[0].Id, chunk, 5, 20)]);
            var read = await links.ForChunksAsync([chunk, Guid.NewGuid()]);

            read.Should().ContainKey(chunk).And.HaveCount(1);
            read[chunk].Should().Equal(new TablePassageLink(tables[0].Id, chunk, 5, 20), new TablePassageLink(tables[1].Id, chunk, 40, 10));

            await store.SyncDocumentTablesAsync(containerId, documentId, [Load(documentId, new(1, 1, "aaaa"), "A")]);
            (await links.ForChunksAsync([chunk]))[chunk].Select(l => l.TableId).Should().Equal(tables[0].Id);

            await links.ReplaceAsync(containerId, documentId, []);
            (await links.ForChunksAsync([chunk])).Should().BeEmpty();
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    [Fact]
    public async Task Links_TableOfAnotherDocument_Throws()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var (containers, containerId, documentId) = await NewDocumentAsync(scope);
        try
        {
            Guid other = await InsertDocumentAsync(scope, containerId);
            var store = scope.ServiceProvider.GetRequiredService<ITableStore>();
            var links = scope.ServiceProvider.GetRequiredService<ITablePassageLinks>();
            var foreign = await store.SyncDocumentTablesAsync(containerId, other, [Load(other, new(1, 1, "aaaa"), "A")]);
            Guid chunk = await NewChunkAsync(scope, documentId, containerId);

            Func<Task> act = () => links.ReplaceAsync(containerId, documentId, [new(foreign[0].Id, chunk, 0, 5)]);

            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            await DeleteAsync(scope, containers, containerId);
        }
    }

    private static TableLoad Load(Guid documentId, TablePlacement? placement, string name) =>
        new(name, [new TableColumn("Item", TableColumnType.Text), new TableColumn("Value", TableColumnType.WholeNumber)],
            Rows([["a", 1L], ["b", 2L]]), documentId, Placement: placement);

    private static async Task<(IContainerStore Containers, Guid ContainerId, Guid DocumentId)> NewDocumentAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"sync-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        return (containers, containerId, await InsertDocumentAsync(scope, containerId));
    }

    private static async Task<Guid> InsertDocumentAsync(AsyncServiceScope scope, Guid containerId)
    {
        Guid id = Guid.NewGuid();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
            + "VALUES ({0}, {1}, NULL, {2}, {3}, '', 1, 'Ready', now())",
            id, containerId, $"{id:N}.pdf", $"/{id:N}.pdf");
        return id;
    }

    private static async Task<Guid> NewChunkAsync(AsyncServiceScope scope, Guid documentId, Guid ownerId)
    {
        Guid id = Guid.NewGuid();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO chunks (id, document_id, owner_id, content, chunk_index, token_count, start_offset, end_offset) "
            + "VALUES ({0}, {1}, {2}, 'passage text with a table in it', 0, 7, 0, 31)",
            id, documentId, ownerId);
        return id;
    }

    private static async Task DeleteAsync(AsyncServiceScope scope, IContainerStore containers, Guid containerId)
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext context = await factory.CreateDbContextAsync())
            await context.Database.ExecuteSqlRawAsync("DELETE FROM documents WHERE container_id = {0}", containerId);
        await containers.DeleteAsync(containerId);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows(object?[][] rows)
    {
        foreach (object?[] row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }
}
