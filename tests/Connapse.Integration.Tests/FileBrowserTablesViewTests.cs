using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Integration.Tests.CrossSurface;
using Connapse.Storage.Data;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-319: a container's page switches between its files and its tables, pages through the tables, and opens each one
/// to its columns and their types, read from the real table store.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FileBrowserTablesViewTests(SharedWebAppFixture fixture)
{
    private static readonly TableColumn[] Columns =
    [
        new("Region", TableColumnType.Text),
        new("Units", TableColumnType.WholeNumber),
    ];

    [Fact]
    public async Task TablesTab_ContainerWithManyTables_PagesThemAndOpensASchema()
    {
        int count = Web.Components.Tables.ContainerTables.DefaultPageSize + 2;
        string containerId = await SeedAsync(Enumerable.Range(1, count).Select(i => $"table-{i:D2}"));

        using var ui = await Surfaces.UiAsync(fixture);
        var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, containerId));
        page.WaitForAssertion(() => page.Find("#tables-tab"), TimeSpan.FromSeconds(10));

        page.Find("#tables-tab").Click();
        page.WaitForAssertion(() => page.FindAll(".table-row").Should().HaveCount(Web.Components.Tables.ContainerTables.DefaultPageSize),
            TimeSpan.FromSeconds(10));
        page.Find("#fileDropZone").GetAttribute("style").Should().Contain("display:none", "the files hide while the tables show");
        page.Find("#tables-range").TextContent.Should().Be($"1–25 of {count} tables");

        page.Find(".table-row").Click();
        page.FindAll(".schema-column .schema-name").Select(n => n.TextContent).Should().Equal("Region", "Units");
        page.FindAll(".schema-column .schema-type").Select(n => n.TextContent).Should().Equal("Text", "Whole number");
        page.Find(".table-row .col-rows").TextContent.Trim().Should().Be("2");

        page.Find("#tables-next").Click();
        page.WaitForAssertion(() => page.FindAll(".table-row .entry-name").Select(n => n.TextContent.Trim())
            .Should().Equal("table-26", "table-27"), TimeSpan.FromSeconds(10));

        page.Find("#files-tab").Click();
        page.WaitForAssertion(() => page.FindAll("#tables-view").Should().BeEmpty(), TimeSpan.FromSeconds(10));
        page.Find("#fileDropZone").GetAttribute("style").Should().BeNullOrEmpty();
    }

    /// <summary>
    /// A documents container holding a two-row table under each name, each loaded from its own uploaded file, as every
    /// real table is: a table is listed only to callers who may read its file (CON-304).
    /// </summary>
    private async Task<string> SeedAsync(IEnumerable<string> names)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var container = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"tview-{Guid.NewGuid():N}"[..20]));
        Guid containerId = Guid.Parse(container.Id);
        var tables = scope.ServiceProvider.GetRequiredService<ITableStore>();
        foreach (string name in names)
            await tables.CreateAsync(containerId, new TableLoad(name, Columns, Rows(), await NewDocumentAsync(scope, containerId, name)));
        return container.Id;
    }

    private static async Task<Guid> NewDocumentAsync(AsyncServiceScope scope, Guid containerId, string name)
    {
        Guid id = Guid.NewGuid();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using KnowledgeDbContext context = await factory.CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, ingestion_status, created_at) "
            + "VALUES ({0}, {1}, NULL, {2}, {3}, '', 1, 'Ready', now())",
            id, containerId, $"{name}.csv", $"/{name}.csv");
        return id;
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> Rows()
    {
        await Task.Yield();
        yield return ["North", 4L];
        yield return ["South", 7L];
    }
}
