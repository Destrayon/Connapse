using System.Security.Claims;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Tables;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Tests.Mcp;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>CON-319: a container's tables are paged through by name, for anyone who can read the container.</summary>
[Trait("Category", "Unit")]
public class TableOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IContainerStore _containers = Substitute.For<IContainerStore>();
    private readonly ITableStore _tables = Substitute.For<ITableStore>();
    private readonly IOperationAuthorizer _authorizer = Substitute.For<IOperationAuthorizer>();

    public TableOperationTests() =>
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);

    private ListTables List => new(_containers, _tables, _authorizer);

    private Guid ContainerOfKind(ContainerKind kind)
    {
        Guid id = Guid.NewGuid();
        _containers.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: kind));
        return id;
    }

    private static TableInfo Table(Guid containerId, string name) =>
        new(Guid.NewGuid(), containerId, name, null, Guid.NewGuid(), [new TableColumn("a", TableColumnType.Text)], 1, false, [],
            "tables_x", "t_x", 1, Epoch, Epoch);

    [Fact]
    public async Task List_NotAReader_IsForbiddenAndReadsNothing()
    {
        _authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Policies.Reader).Returns(false);

        var listed = await List.RunAsync(Caller, Guid.NewGuid(), 0, 10);

        listed.Status.Should().Be(TableStatus.Forbidden);
        await _tables.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(-1, 10, "skip must be >= 0")]
    [InlineData(0, 0, "take must be >= 1")]
    [InlineData(0, 201, "take must be <= 200")]
    public async Task List_PageOutOfRange_IsInvalidPage(int skip, int take, string detail)
    {
        var listed = await List.RunAsync(Caller, ContainerOfKind(ContainerKind.Documents), skip, take);

        listed.Status.Should().Be(TableStatus.InvalidPage);
        listed.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task List_MissingContainer_IsNotFound()
    {
        var listed = await List.RunAsync(Caller, Guid.NewGuid(), 0, 10);

        listed.Status.Should().Be(TableStatus.ContainerNotFound);
    }

    [Fact]
    public async Task List_MemoryContainer_IsRefusedAndReadsNoTables()
    {
        var listed = await List.RunAsync(Caller, ContainerOfKind(ContainerKind.Memory), 0, 10);

        listed.Status.Should().Be(TableStatus.MemoryContainer);
        await _tables.DidNotReceiveWithAnyArgs().ListAsync(default, default, default, default);
    }

    [Theory]
    [InlineData(0, 2, 5, true)]
    [InlineData(2, 2, 5, true)]
    [InlineData(4, 2, 5, false)]
    [InlineData(0, 50, 2, false)]
    public async Task List_DocumentsContainer_ReturnsThePageTheTotalAndWhetherMoreFollow(int skip, int take, int total, bool hasMore)
    {
        Guid id = ContainerOfKind(ContainerKind.Documents);
        List<TableInfo> page = Enumerable.Range(skip, Math.Min(take, total - skip)).Select(i => Table(id, $"t{i}")).ToList();
        _tables.ListAsync(id, skip, take, Arg.Any<CancellationToken>()).Returns(new TablePage(page, total));

        var listed = await List.RunAsync(Caller, id, skip, take);

        listed.Status.Should().Be(TableStatus.Success);
        listed.Result!.Items.Should().Equal(page);
        listed.Result.TotalCount.Should().Be(total);
        listed.Result.HasMore.Should().Be(hasMore);
    }

    [Fact]
    [Trait("Size", "Medium")]
    [RandomInputTestOf(typeof(ListTables))]
    public Task List_AnyCallerContainerAndPage_AnswersThatSliceOrTheFirstRuleItBreaks() =>
        Gen.Select(Gen.Bool, Gen.Enum<ContainerKind>().Nullable(), Gen.Int[-2, 6], Gen.Int[-1, 205], Gen.Int[0, 8])
            .SampleAsync(async t =>
            {
                var (reader, kind, skip, take, stored) = t;
                var containers = Substitute.For<IContainerStore>();
                var tables = Substitute.For<ITableStore>();
                var authorizer = new PolicyAuthorizer(policy => policy == Policies.Reader && reader);
                Guid id = Guid.NewGuid();
                containers.GetAsync(id, Arg.Any<CancellationToken>())
                    .Returns(kind is { } k ? new Container(id.ToString(), "c", null, Epoch, Epoch, Kind: k) : null);
                var all = Enumerable.Range(0, stored).Select(i => Table(id, $"t{i}")).ToList();
                tables.ListAsync(id, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(c => new TablePage(all.Skip(c.ArgAt<int>(1)).Take(c.ArgAt<int>(2)).ToList(), all.Count));

                var listed = await new ListTables(containers, tables, authorizer).RunAsync(Caller, id, skip, take);

                TableStatus expected = !reader ? TableStatus.Forbidden
                    : skip < 0 || take is < 1 or > 200 ? TableStatus.InvalidPage
                    : kind is null ? TableStatus.ContainerNotFound
                    : kind == ContainerKind.Memory ? TableStatus.MemoryContainer
                    : TableStatus.Success;
                listed.Status.Should().Be(expected);
                if (expected != TableStatus.Success)
                {
                    tables.ReceivedCalls().Should().BeEmpty();
                    return;
                }
                listed.Result!.Items.Should().Equal(all.Skip(skip).Take(take));
                listed.Result.TotalCount.Should().Be(stored);
                listed.Result.HasMore.Should().Be(stored - skip > take);
            }, iter: 2000);
}
