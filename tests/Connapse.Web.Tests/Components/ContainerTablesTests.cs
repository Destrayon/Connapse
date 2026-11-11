using Bunit;
using Connapse.Application.Ports;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Tables;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>The Tables view on a container's page (CON-319): its tables a page at a time, each opening to its schema.</summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class ContainerTablesTests : IDisposable
{
    private static readonly DateTime Loaded = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitContext ctx = new();
    private readonly IContainerStore containers = ContainerStoreFakes.Create();
    private readonly ITableStore tables = Substitute.For<ITableStore>();
    private readonly IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
    private readonly IDocumentStore documents = Substitute.For<IDocumentStore>();
    private readonly Connapse.Application.Ports.IDocumentAccess access = Substitute.For<Connapse.Application.Ports.IDocumentAccess>();
    private readonly Guid containerId = Guid.NewGuid();

    public ContainerTablesTests()
    {
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        containers.GetAsync(containerId, Arg.Any<CancellationToken>())
            .Returns(new Container(containerId.ToString(), "q3", null, Loaded, Loaded, 0, Kind: ContainerKind.Documents));
        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(tables);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton(Substitute.For<ITableQueryService>());
        ctx.Services.AddSingleton(documents);
        ctx.Services.AddSingleton(access);
        ctx.Services.AddSingleton(Substitute.For<IMemoryService>());
        ctx.Services.AddSingleton<ListTables>();
        documents.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new Document(call.ArgAt<string>(0), containerId.ToString(), "t.csv", null, "/t.csv", 1, Loaded, []));
        access.ReadableAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<IReadOnlyList<Document>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<string>)call.ArgAt<IReadOnlyList<Document>>(1).Select(d => d.Id).ToHashSet());
        ctx.AddAuthorization().SetAuthorized("viewer");
    }

    public void Dispose() => ctx.Dispose();

    private TableInfo Table(string name, long rows, params TableColumn[] columns) =>
        new(Guid.NewGuid(), containerId, name, null, Guid.NewGuid(), columns, rows, false, [], "s", "t", 1, Loaded, Loaded);

    private void Holds(params TableInfo[] all) =>
        tables.ListAsync(containerId, Arg.Any<CancellationToken>()).Returns(all);

    private IRenderedComponent<ContainerTables> Render(int pageSize = ContainerTables.DefaultPageSize)
    {
        var cut = ctx.Render<ContainerTables>(p => p.Add(x => x.ContainerId, containerId).Add(x => x.PageSize, pageSize));
        cut.WaitForAssertion(() => cut.FindAll(".table-row, #tables-empty, #tables-error").Should().NotBeEmpty());
        return cut;
    }

    private static List<string> Names(IRenderedComponent<ContainerTables> cut) =>
        cut.FindAll(".table-row .entry-name").Select(n => n.TextContent.Trim()).ToList();

    [Fact]
    public void Render_ContainerWithTables_ListsEachWithItsColumnAndRowCounts()
    {
        Holds(Table("costs", 1234, new TableColumn("Region", TableColumnType.Text), new TableColumn("Cost", TableColumnType.Number)),
            Table("orders", 3, new TableColumn("Id", TableColumnType.WholeNumber)));

        var cut = Render();

        Names(cut).Should().Equal("costs", "orders");
        var first = cut.FindAll(".table-row")[0];
        first.QuerySelector(".col-columns")!.TextContent.Trim().Should().Be("2");
        first.QuerySelector(".col-rows")!.TextContent.Trim().Should().Be(1234L.ToString("N0"));
        cut.FindAll(".table-schema-row").Should().BeEmpty("every table starts collapsed");
        cut.Find("#tables-range").TextContent.Should().Be("1–2 of 2 tables");
    }

    [Fact]
    public void ClickTable_ShowsItsColumnsAndTypes_AndClickingAgainHidesThem()
    {
        Holds(Table("orders", 3,
            new TableColumn("Id", TableColumnType.WholeNumber),
            new TableColumn("Placed", TableColumnType.Date, Format: new TableColumnFormat(Pattern: "%d/%m/%Y")),
            new TableColumn("Paid", TableColumnType.Boolean)));
        var cut = Render();

        cut.Find(".table-row").Click();

        var schema = cut.FindAll(".schema-column").Select(r =>
            (r.QuerySelector(".schema-name")!.TextContent, r.QuerySelector(".schema-type")!.TextContent)).ToList();
        schema.Should().Equal(("Id", "Whole number"), ("Placed", "Date (%d/%m/%Y)"), ("Paid", "True/false"));
        cut.Find(".table-row").GetAttribute("aria-expanded").Should().Be("true");

        cut.Find(".table-row").Click();

        cut.FindAll(".table-schema-row").Should().BeEmpty();
    }

    [Fact]
    public void NextThenPrevious_ManyTables_PagesThroughThemAll()
    {
        Holds(Enumerable.Range(1, 5).Select(i => Table($"t{i}", i, new TableColumn("A", TableColumnType.Text))).ToArray());
        var cut = Render(pageSize: 2);
        cut.Find("#tables-prev").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#tables-next").Click();
        cut.WaitForAssertion(() => Names(cut).Should().Equal("t3", "t4"));
        cut.Find("#tables-range").TextContent.Should().Be("3–4 of 5 tables");

        cut.Find("#tables-next").Click();
        cut.WaitForAssertion(() => Names(cut).Should().Equal("t5"));
        cut.Find("#tables-next").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#tables-prev").Click();
        cut.WaitForAssertion(() => Names(cut).Should().Equal("t3", "t4"));
    }

    [Fact]
    public void Render_ContainerWithNoTables_SaysHowTablesArrive()
    {
        Holds();

        var cut = Render();

        cut.Find("#tables-empty").TextContent.Should().Contain("CSV files uploaded to this container load as tables");
        cut.FindAll("#tables-next").Should().BeEmpty();
    }

    [Fact]
    public void Render_ReaderNotAllowed_ShowsTheError()
    {
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(false);

        var cut = Render();

        cut.Find("#tables-error").TextContent.Should().Contain("permission to view tables");
        tables.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 25, 60, "1–25 of 60 tables")]
    [InlineData(50, 10, 60, "51–60 of 60 tables")]
    [InlineData(0, 1, 1, "1–1 of 1 table")]
    [InlineData(25, 0, 20, "0 of 20")]
    public void Range_SaysWhichTablesThePageShows(int skip, int shown, int total, string expected) =>
        ContainerTables.Range(skip, shown, total).Should().Be(expected);

    [Theory]
    [InlineData(TableColumnType.Text, "Text")]
    [InlineData(TableColumnType.WholeNumber, "Whole number")]
    [InlineData(TableColumnType.Number, "Number")]
    [InlineData(TableColumnType.Boolean, "True/false")]
    [InlineData(TableColumnType.Date, "Date")]
    [InlineData(TableColumnType.DateTime, "Date and time")]
    public void TypeName_EachType_ReadsInWords(TableColumnType type, string expected) =>
        ContainerTables.TypeName(new TableColumn("c", type)).Should().Be(expected);
}
