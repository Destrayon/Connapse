using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Application.Sources;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

/// <summary>CON-227: container_create takes a kind, and container_list tells an agent which containers hold memory.</summary>
[Trait("Category", "Unit")]
public class McpToolsContainerKindTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IContainerStore _containers = ContainerStoreFakes.Create();

    private CreateContainer Create() => new(_containers, Substitute.For<IAuditLogger>(), McpCaller.Allowed);

    [Theory]
    [InlineData(null, ContainerKind.Documents)]
    [InlineData("", ContainerKind.Documents)]
    [InlineData("documents", ContainerKind.Documents)]
    [InlineData(" Memory ", ContainerKind.Memory)]
    [InlineData("memory", ContainerKind.Memory)]
    [InlineData("tables", ContainerKind.Tables)]
    [InlineData(" Tables ", ContainerKind.Tables)]
    [InlineData("spreadsheets", null)]
    [InlineData("1", null)]
    public void ParseKind_AnyText_IsAKnownKindOrUnknown(string? text, ContainerKind? expected) =>
        McpTools.ParseKind(text).Should().Be(expected);

    [Fact]
    public async Task ContainerCreate_MemoryKind_CreatesAMemoryContainerAndNamesTheMemoryTools()
    {
        _containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => new Container("7b1c9e2a-0000-4000-8000-0000000000aa", c.Arg<CreateContainerRequest>().Name, null, Epoch, Epoch,
                Kind: c.Arg<CreateContainerRequest>().Kind));

        string result = await McpTools.ContainerCreate(Create(), McpCaller.Http, "agent-notes", kind: "memory");

        result.Should().Be("Agent memory container 'agent-notes' created. Save facts to it with `memory_remember` and read them with `memory_recall`.\n\nID: 7b1c9e2a-0000-4000-8000-0000000000aa");
        await _containers.Received(1).CreateAsync(new CreateContainerRequest("agent-notes", null, ContainerKind.Memory), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContainerCreate_NoKind_CreatesADocumentsContainer()
    {
        _containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => new Container("7b1c9e2a-0000-4000-8000-0000000000aa", c.Arg<CreateContainerRequest>().Name, null, Epoch, Epoch));

        string result = await McpTools.ContainerCreate(Create(), McpCaller.Http, "docs");

        result.Should().StartWith("Container 'docs' created.");
        await _containers.Received(1).CreateAsync(new CreateContainerRequest("docs", null, ContainerKind.Documents), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContainerCreate_UnknownKind_IsRefusedAndCreatesNothing()
    {
        string result = await McpTools.ContainerCreate(Create(), McpCaller.Http, "docs", kind: "spreadsheets");

        result.Should().Be("Error: kind must be \"documents\", \"tables\" or \"memory\".");
        _containers.ReceivedCalls().Should().BeEmpty();
    }

    /// <summary>CON-300: an agent can make a tables container, and is told text search won't search it.</summary>
    [Fact]
    public async Task ContainerCreate_TablesKind_CreatesATablesContainerAndSaysSearchSkipsIt()
    {
        _containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>())
            .Returns(c => new Container("7b1c9e2a-0000-4000-8000-0000000000aa", c.Arg<CreateContainerRequest>().Name, null, Epoch, Epoch,
                Kind: c.Arg<CreateContainerRequest>().Kind));

        string result = await McpTools.ContainerCreate(Create(), McpCaller.Http, "sales", kind: "tables");

        result.Should().Be("Tables container 'sales' created. It holds tables, not documents, so `search_knowledge` won't search it.\n\nID: 7b1c9e2a-0000-4000-8000-0000000000aa");
        await _containers.Received(1).CreateAsync(new CreateContainerRequest("sales", null, ContainerKind.Tables), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContainerList_MemorySpace_IsNotListed()
    {
        _containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new Container(Guid.NewGuid().ToString(), "docs", null, Epoch, Epoch, 3),
            new Container(Guid.NewGuid().ToString(), "agent-notes", "General facts", Epoch, Epoch, Kind: ContainerKind.Memory),
        ]);
        var sources = Substitute.For<ISourceStore>();
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Source>());

        string result = await McpTools.ContainerList(new ListContainers(_containers, McpCaller.Allowed),
            new ListSources(sources, McpCaller.SeesEverySource, McpCaller.Allowed), McpCaller.Http);

        result.Should().Contain("- docs [managed] (3 files)");
        result.Should().NotContain("agent-notes", "memory spaces are listed by memory_spaces, not among containers (CON-291)");
    }

    [Fact]
    public async Task ContainerList_TablesContainer_IsListedAsTables()
    {
        _containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(
        [
            new Container(Guid.NewGuid().ToString(), "docs", null, Epoch, Epoch, 3),
            new Container(Guid.NewGuid().ToString(), "sales", null, Epoch, Epoch, Kind: ContainerKind.Tables),
        ]);
        var sources = Substitute.For<ISourceStore>();
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<Source>());

        string result = await McpTools.ContainerList(new ListContainers(_containers, McpCaller.Allowed),
            new ListSources(sources, McpCaller.SeesEverySource, McpCaller.Allowed), McpCaller.Http);

        result.Should().Contain("- docs [managed] (3 files)");
        result.Should().Contain("- sales [tables]");
    }
}
