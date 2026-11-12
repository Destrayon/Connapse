using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-310: a search over a container with more than 50 tables ranks every one of them, best match first, and says so;
/// with 50 or fewer it still filters by substring. A hidden column never moves its table up.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class TableListSearchTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task ListTables_SearchOverManyTables_RanksAllWithTheMatchFirst()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            for (int i = 0; i < 53; i++)
                await AddTableAsync(containerId, $"fill_{i:D2}", null, ["code", "label"]);
            await AddTableAsync(containerId, "people", "Everyone on the team.", ["person", "salary"], hidden: ["salary"]);
            await AddTableAsync(containerId, "staff_payroll", "What each employee is paid.", ["employee", "salary"]);

            using JsonDocument page = await GetJsonAsync($"/api/containers/{containerId}/tables?take=5&search=employee%20salary");

            JsonElement root = page.RootElement;
            root.GetProperty("mode").GetString().Should().Be("ranked");
            root.GetProperty("totalCount").GetInt32().Should().Be(55, "a ranking drops no table");
            root.GetProperty("items").GetArrayLength().Should().Be(5);
            root.GetProperty("hasMore").GetBoolean().Should().BeTrue();
            root.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("staff_payroll");
            root.GetProperty("meaningUsed").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    [Fact]
    public async Task ListTables_SearchOverFewTables_StillFilters()
    {
        Guid containerId = await NewContainerAsync();
        try
        {
            await AddTableAsync(containerId, "staff_payroll", null, ["employee", "salary"]);
            await AddTableAsync(containerId, "fruit_prices", null, ["fruit", "price"]);

            using JsonDocument page = await GetJsonAsync($"/api/containers/{containerId}/tables?search=salary");

            page.RootElement.GetProperty("mode").GetString().Should().Be("filtered");
            page.RootElement.GetProperty("totalCount").GetInt32().Should().Be(1);
            page.RootElement.GetProperty("meaningUsed").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await DeleteContainerAsync(containerId);
        }
    }

    private async Task<Guid> NewContainerAsync()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"tls-{Guid.NewGuid():N}"[..20], null, ContainerKind.Documents));
        return Guid.Parse(created.Id);
    }

    /// <summary>A table from its own ready document in the container, with <paramref name="hidden"/> columns hidden.</summary>
    private async Task AddTableAsync(Guid containerId, string name, string? description, string[] columns, string[]? hidden = null)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            ContainerId = containerId,
            FileName = $"{name}.csv",
            Path = $"/{name}.csv",
            ContentHash = Guid.NewGuid().ToString("N"),
            IngestionStatus = DocumentStatus.Ready,
            CreatedAt = DateTime.UtcNow,
            Metadata = [],
        };
        await using (KnowledgeDbContext db = await factory.CreateDbContextAsync())
        {
            db.Documents.Add(document);
            await db.SaveChangesAsync();
        }

        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        TableInfo table = await tables.CreateAsync(containerId, new TableLoad(name,
            [.. columns.Select(c => new TableColumn(c, TableColumnType.Text))], Rows(), document.Id, description));
        if (hidden is not null)
            await tables.SetHiddenColumnsAsync(containerId, table.Id, hidden);
    }

    private async Task DeleteContainerAsync(Guid containerId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        foreach (TableInfo table in await tables.ListAsync(containerId))
            await tables.DeleteAsync(containerId, table.Id);
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (KnowledgeDbContext db = await factory.CreateDbContextAsync())
            await db.Documents.Where(d => d.ContainerId == containerId).ExecuteDeleteAsync();
        await scope.ServiceProvider.GetRequiredService<IContainerStore>().DeleteAsync(containerId);
    }

    private async Task<JsonDocument> GetJsonAsync(string url)
    {
        using HttpResponseMessage response = await fixture.AdminClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
    {
        await Task.Yield();
        yield break;
    }
}
