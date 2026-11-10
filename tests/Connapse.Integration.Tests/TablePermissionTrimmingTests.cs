using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Tables;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-304 planted-leak tests: each plants data a caller must not see — rows from a document they can't read in a
/// merged table, a column an admin hid, rejected rows that quote either — and shows no request surfaces it. The merged
/// table's rows are checked twice: through the service, whose compiled <c>WHERE</c> keeps them out, and on the reader
/// role with a token, which shows the row-level security policy keeps them out on its own.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TablePermissionTrimmingTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] Columns =
    [
        new("Region", TableColumnType.Text),
        new("Amount", TableColumnType.Number),
    ];

    [Fact]
    public async Task Merged_ReadAndAggregate_ReturnOnlyTheReadableDocumentsRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess access = Readable(merged.Open);

            TableQueryResult all = await service.ReadAsync(merged.ContainerId, access, new TableReadRequest(merged.Table.Id));
            TableQueryResult planted = await service.ReadAsync(merged.ContainerId, access, new TableReadRequest(
                merged.Table.Id, Where: new TableCondition(new TableField("Region"), TableFilterOperator.Equal, ["secret"])));
            TableQueryResult totals = await service.AggregateAsync(merged.ContainerId, access, new TableAggregateRequest(
                merged.Table.Id, [new TableField(null, TableAggregateFunction.Count), new TableField("Amount", TableAggregateFunction.Sum)]));

            all.Columns.Should().Equal("Region", "Amount");
            all.Rows.Select(r => r[0]).Should().BeEquivalentTo(["north", "south"]);
            planted.Rows.Should().BeEmpty();
            totals.Rows.Single().Should().Equal(2L, 30m);
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task Merged_RowSecurityPolicy_ShowsOnlyTheTokensDocumentsRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            await Login.EnsureGrantsAsync(merged.ContainerId, merged.Table.StorageSchema, default);

            long openOnly = await CountUnderRoleAsync(merged, await TokenAsync(merged.Open));
            long secretOnly = await CountUnderRoleAsync(merged, await TokenAsync(merged.Secret));
            long both = await CountUnderRoleAsync(merged, await TokenAsync(merged.Open, merged.Secret));
            long none = await CountUnderRoleAsync(merged, token: null);

            openOnly.Should().Be(2, "the policy checks each row's own document, with no WHERE from the compiler");
            secretOnly.Should().Be(1);
            both.Should().Be(3);
            none.Should().Be(0);
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task Merged_NoReadableSource_ReadsAsAMissingTable()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            Func<Task> read = () => service.ReadAsync(merged.ContainerId, Readable(Guid.NewGuid()), new TableReadRequest(merged.Table.Id));
            Func<Task> describe = () => service.DescribeAsync(merged.ContainerId, Readable(Guid.NewGuid()), merged.Table.Id);

            await read.Should().ThrowAsync<TableQueryException>().WithMessage($"There is no table with id {merged.Table.Id} in this container.");
            await describe.Should().ThrowAsync<TableQueryException>().WithMessage($"There is no table with id {merged.Table.Id} in this container.");
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task Describe_MergedTable_CountsSamplesAndRejectsOnlyReadableRows()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();

            TableDescription description = await service.DescribeAsync(merged.ContainerId, Readable(merged.Open), merged.Table.Id);

            description.RowCount.Should().Be(2);
            description.SampleRows.Rows.Select(r => r[0]).Should().Equal("north", "south");
            description.SampleRows.Columns.Should().Equal("Region", "Amount");
            description.Rejects.Select(r => r.Value).Should().Equal("open-bad");
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task Describe_OnlyUnreadableRowsChange_IsIdentical()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
            TableDescription before = await service.DescribeAsync(merged.ContainerId, Readable(merged.Open), merged.Table.Id);

            // Same readable rows; the unreadable document now has far more rows, other values and other rejects.
            object?[][] rows =
            [
                ["north", 10m, merged.Open],
                ["south", 20m, merged.Open],
                .. Enumerable.Range(0, 50).Select(i => new object?[] { $"aaa-secret-{i}", (decimal)-i, merged.Secret }),
            ];
            await tables.ReloadAsync(merged.ContainerId, merged.Table.Id, MergedLoad(merged, rows,
                [new TableReject(2, 2, "open-bad", "not a number", merged.Open),
                 new TableReject(9, 2, "secret-bad-2", "not a number", merged.Secret),
                 new TableReject(10, 1, "secret-bad-3", "too long", merged.Secret)]));
            TableDescription after = await service.DescribeAsync(merged.ContainerId, Readable(merged.Open), merged.Table.Id);

            after.Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Theory]
    [InlineData("select")]
    [InlineData("where")]
    [InlineData("order")]
    [InlineData("group")]
    [InlineData("aggregate")]
    public async Task HiddenColumn_InAnyClause_ReadsExactlyAsAMissingOne(string clause)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            await Hide(scope, merged, "Amount");
            TableAccess access = Readable(merged.Open, merged.Secret);

            string hidden = await FailureAsync(service, merged, access, clause, "Amount");
            string missing = await FailureAsync(service, merged, access, clause, "Nope");

            hidden.Should().Be(missing.Replace("Nope", "Amount", StringComparison.Ordinal));
            hidden.Should().Contain("has no column \"Amount\"");
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task HiddenColumn_IsLeftOutOfSelectAllAndDescribe()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            await Hide(scope, merged, "Amount");
            TableAccess access = Readable(merged.Open, merged.Secret);

            TableQueryResult all = await service.ReadAsync(merged.ContainerId, access, new TableReadRequest(merged.Table.Id));
            TableDescription description = await service.DescribeAsync(merged.ContainerId, access, merged.Table.Id);

            all.Columns.Should().Equal("Region");
            all.Rows.Should().OnlyContain(r => r.Count == 1);
            description.Columns.Select(c => c.Name).Should().Equal("Region");
            description.SampleRows.Columns.Should().Equal("Region");
            description.Rejects.Should().BeEmpty("every planted reject is in the hidden column, and would quote it");
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task HiddenColumn_StaysHiddenAcrossAReload()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            await Hide(scope, merged, "Amount");

            TableInfo? reloaded = await tables.ReloadAsync(merged.ContainerId, merged.Table.Id,
                MergedLoad(merged, [["west", 1m, merged.Open]], []));
            TableQueryResult all = await service.ReadAsync(merged.ContainerId, Readable(merged.Open), new TableReadRequest(merged.Table.Id));

            reloaded!.HiddenColumns.Should().Equal("Amount");
            all.Columns.Should().Equal("Region");
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task HideColumns_UnknownName_IsRefusedAndHidesNothing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();

            Func<Task> act = () => tables.SetHiddenColumnsAsync(merged.ContainerId, merged.Table.Id, ["Amount", "Nope"]);

            await act.Should().ThrowAsync<ArgumentException>();
            (await tables.GetAsync(merged.ContainerId, merged.Table.Id))!.HiddenColumns.Should().BeEmpty();
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task UncheckedAccess_AnswersNothingForEveryRequest()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var service = scope.ServiceProvider.GetRequiredService<ITableQueryService>();
            TableAccess access = TableAccess.Unchecked;

            Func<Task>[] requests =
            [
                () => service.ReadAsync(merged.ContainerId, access, new TableReadRequest(merged.Table.Id)),
                () => service.AggregateAsync(merged.ContainerId, access, new TableAggregateRequest(merged.Table.Id, [new TableField(null, TableAggregateFunction.Count)])),
                () => service.DescribeAsync(merged.ContainerId, access, merged.Table.Id),
                () => service.CountAsync(merged.ContainerId, access, merged.Table.Id),
            ];

            foreach (Func<Task> request in requests)
                await request.Should().ThrowAsync<TableQueryException>().WithMessage(TableAccess.UncheckedMessage);
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    [Fact]
    public async Task MergedLoad_RowFromADocumentNotListed_IsRefused()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Merged merged = await NewMergedAsync(scope);
        try
        {
            var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();

            Func<Task> stray = () => tables.CreateAsync(merged.ContainerId,
                MergedLoad(merged, [["x", 1m, Guid.NewGuid()]], []));
            Func<Task> both = () => tables.CreateAsync(merged.ContainerId,
                MergedLoad(merged, [["x", 1m, merged.Open]], []) with { DocumentId = merged.Open });

            await stray.Should().ThrowAsync<ArgumentException>();
            await both.Should().ThrowAsync<ArgumentException>();
        }
        finally
        {
            await DeleteAsync(scope, merged.Containers, merged.ContainerId);
        }
    }

    private sealed record Merged(IContainerStore Containers, Guid ContainerId, Guid Open, Guid Secret, TableInfo Table);

    private TableReaderLogin Login => fixture.Factory.Services.GetRequiredService<TableReaderLogin>();

    private static TableAccess Readable(params Guid[] documents) => new(documents.ToHashSet());

    private Task<string> TokenAsync(params Guid[] documents) =>
        Login.IssueTokenAsync(documents.ToHashSet(), TimeSpan.FromMinutes(1), default);

    private static async Task Hide(AsyncServiceScope scope, Merged merged, params string[] columns) =>
        (await scope.ServiceProvider.GetRequiredService<ITableStore>()
            .SetHiddenColumnsAsync(merged.ContainerId, merged.Table.Id, columns))!.HiddenColumns.Should().Equal(columns);

    /// <summary>The message a request naming <paramref name="column"/> in <paramref name="clause"/> fails with.</summary>
    private static async Task<string> FailureAsync(
        ITableQueryService service, Merged merged, TableAccess access, string clause, string column)
    {
        Guid id = merged.Table.Id;
        TableField field = new(column);
        Func<Task> act = clause switch
        {
            "select" => () => service.ReadAsync(merged.ContainerId, access, new TableReadRequest(id, [column])),
            "where" => () => service.ReadAsync(merged.ContainerId, access,
                new TableReadRequest(id, Where: new TableCondition(field, TableFilterOperator.GreaterOrEqual, [0]))),
            "order" => () => service.ReadAsync(merged.ContainerId, access,
                new TableReadRequest(id, OrderBy: [new TableOrder(field, TableSortDirection.Descending)])),
            "group" => () => service.AggregateAsync(merged.ContainerId, access,
                new TableAggregateRequest(id, [new TableField(null, TableAggregateFunction.Count)], [column])),
            _ => () => service.AggregateAsync(merged.ContainerId, access,
                new TableAggregateRequest(id, [new TableField(column, TableAggregateFunction.Sum)])),
        };
        return (await act.Should().ThrowAsync<TableQueryException>()).Which.Message;
    }

    /// <summary>Counts a merged table's rows on the reader login as the container's role, with only the policy filtering.</summary>
    private async Task<long> CountUnderRoleAsync(Merged merged, string? token)
    {
        await using NpgsqlConnection connection = await Login.OpenConnectionAsync(default);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand role = new($"SET LOCAL ROLE {TableDdl.Quote(Login.ContainerRoleName(merged.ContainerId))}", connection, transaction))
            await role.ExecuteNonQueryAsync();
        if (token is not null)
        {
            await using NpgsqlCommand bind = new("SELECT pg_catalog.set_config('tables_auth.token', $1, true)", connection, transaction);
            bind.Parameters.Add(new NpgsqlParameter { Value = token });
            await bind.ExecuteNonQueryAsync();
        }
        TableInfo current = (await fixture.Factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<ITableStore>().GetAsync(merged.ContainerId, merged.Table.Id))!;
        await using NpgsqlCommand count = new(
            $"SELECT count(*) FROM {TableDdl.Quote(current.StorageSchema)}.{TableDdl.Quote(current.StorageName)}", connection, transaction);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static TableLoad MergedLoad(Merged merged, object?[][] rows, IReadOnlyList<TableReject> rejects) =>
        new("Sales", Columns, Rows(rows), Rejects: rejects, NeedsReview: rejects.Count > 0, SourceDocuments: [merged.Open, merged.Secret]);

    /// <summary>
    /// A container with a table merged from two documents: "open" with two rows, and "secret" with one planted row,
    /// each document with a rejected row of its own.
    /// </summary>
    private static async Task<Merged> NewMergedAsync(AsyncServiceScope scope)
    {
        var containers = scope.ServiceProvider.GetRequiredService<IContainerStore>();
        var container = await containers.CreateAsync(new CreateContainerRequest($"trim-{Guid.NewGuid():N}", null, ContainerKind.Documents));
        Guid containerId = Guid.Parse(container.Id);
        Guid open = await NewDocumentAsync(scope, containerId), secret = await NewDocumentAsync(scope, containerId);
        var shell = new Merged(containers, containerId, open, secret, null!);
        TableInfo table = await scope.ServiceProvider.GetRequiredService<ITableStore>().CreateAsync(containerId, MergedLoad(shell,
            [["north", 10m, open], ["secret", 999m, secret], ["south", 20m, open]],
            [new TableReject(2, 2, "open-bad", "not a number", open), new TableReject(3, 2, "secret-bad", "not a number", secret)]));
        return shell with { Table = table };
    }

    private static async Task<Guid> NewDocumentAsync(AsyncServiceScope scope, Guid containerId)
    {
        Guid id = Guid.NewGuid();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
            + "VALUES ({0}, {1}, NULL, {2}, {3}, '', 1, 'Ready', now())",
            id, containerId, $"{id:N}.csv", $"/{id:N}.csv");
        return id;
    }

    /// <summary>Deletes the test's source documents, which a container must not hold when it is deleted, then the container.</summary>
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
