using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsContainerListTests
{
    private readonly IContainerStore _containerStore;
    private readonly ISourceStore _sourceStore;
    private readonly IServiceProvider _services;

    public McpToolsContainerListTests()
    {
        _containerStore = Substitute.For<IContainerStore>();

        // container_list now lists sources alongside containers. Configured to return none
        // so these tests keep covering container rendering; source rendering is covered in
        // McpSourceKindTests against a real database.
        _sourceStore = Substitute.For<ISourceStore>();
        _sourceStore.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Source>());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(ISourceStore)).Returns(_sourceStore);
        _services = services;
    }

    [Fact]
    public async Task ContainerList_EmptyReturnsPlainMessageWithoutTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>());

        var result = await McpTools.ContainerList(_services);

        result.Should().Be("No containers found.");
    }

    [Fact]
    public async Task ContainerList_SingleContainerReturnsNoTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("only-one", "The only container", 7)
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().NotStartWith("TIP:");
        result.Should().StartWith("Found 1 knowledge scope(s):");
        result.Should().Contain("- only-one [managed] (7 files) — The only container");
    }

    [Fact]
    public async Task ContainerList_MultipleContainersBeginWithTrimmedTip()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("docs", "Product documentation", 12),
                MakeContainer("research", "Customer research", 5)
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().StartWith("TIP:");
        result.Should().Contain("search_knowledge");
        result.Should().Contain("Pick the entry whose description best matches the topic");

        // Trimmed TIP should NOT contain the v1 "Do NOT enumerate files" phrasing —
        // ServerInstructions Rule 3 covers that now.
        result.Should().NotContain("Do NOT enumerate files");

        result.Should().Contain("- docs [managed] (12 files) — Product documentation");
        result.Should().Contain("- research [managed] (5 files) — Customer research");
    }

    [Fact]
    public async Task ContainerList_IncludesSummaryFirstSentence_WhenSummaryPresent()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("apple", "Apple earnings", 8,
                    summary: "Use this container for Apple earnings. Covers iPhone unit sales and China revenue.")
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().Contain("Summary: Use this container for Apple earnings.");
        result.Should().NotContain("iPhone unit sales");
        result.Should().NotContain("China revenue");
    }

    [Fact]
    public async Task ContainerList_OmitsSummaryLine_WhenSummaryNull()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("empty", "Empty container", 0, summary: null)
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().NotContain("Summary:");
        result.Should().Contain("- empty [managed] (0 files) — Empty container");
        result.Should().Contain("  ID:");
    }

    [Fact]
    public async Task ContainerList_TruncatesLongFirstSentence()
    {
        string longSentence = new string('x', 200); // 200 chars, no period
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("long", "Long summary", 3, summary: longSentence)
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().Contain("Summary:");
        // Should be truncated at 120 chars + "…"
        var summaryLine = result.Split('\n').FirstOrDefault(l => l.Contains("Summary:"));
        summaryLine.Should().NotBeNull();
        summaryLine!.Length.Should().BeLessThanOrEqualTo(11 + 120 + 1); // "  Summary: " (11) + 120 chars + 1 for ellipsis
        summaryLine.Should().EndWith("…");
    }

    [Fact]
    public async Task ContainerList_SingleSentenceWithoutPeriodUsesWholeSentence()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("test", "Test", 1, summary: "No period here")
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().Contain("Summary: No period here");
    }

    [Fact]
    public async Task ContainerList_MultipleContainersWithMixedSummaries()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("docs", "Product docs", 10, summary: "Complete product documentation. Updated monthly."),
                MakeContainer("research", "Research papers", 5, summary: null),
                MakeContainer("logs", "System logs", 100, summary: "Raw system logs. No processing.")
            });

        var result = await McpTools.ContainerList(_services);

        result.Should().Contain("- docs [managed] (10 files) — Product docs");
        result.Should().Contain("  Summary: Complete product documentation.");
        result.Should().Contain("- research [managed] (5 files) — Research papers");
        result.Should().NotContain("- research [managed] (5 files) — Research papers\n  Summary:");
        result.Should().Contain("- logs [managed] (100 files) — System logs");
        result.Should().Contain("  Summary: Raw system logs.");
    }

    [Fact]
    public async Task ContainerList_HandlesNumberedListPrefix_CorrectlyExtractsFirstSentence()
    {
        _containerStore
            .ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Container>
            {
                MakeContainer("test", "Test container", 3,
                    summary: "1. Use this container for Apple queries. Covers iPhone units.")
            });

        var result = await McpTools.ContainerList(_services);

        // Should capture the full first sentence, not just "1."
        result.Should().Contain("Summary: 1. Use this container for Apple queries.");
        result.Should().NotContain("Covers iPhone units.");
    }

    private static Container MakeContainer(
        string name,
        string description,
        int docCount,
        string? summary = null) => new(
        Id: Guid.NewGuid().ToString(),
        Name: name,
        Description: description,
        ConnectorType: ConnectorType.ManagedStorage,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        DocumentCount: docCount,
        Summary: summary);
}
