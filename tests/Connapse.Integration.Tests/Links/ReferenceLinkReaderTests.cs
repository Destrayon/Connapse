using Connapse.Core.Interfaces;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests.Links;

/// <summary>The in-memory reference reader keeps the permission-leak contract.</summary>
[Trait("Category", "Integration")]
public class ReferenceLinkReaderContractTests : LinkReaderContract
{
    protected override LinkReaderFactory Factory =>
        (world, access) => Task.FromResult<ILinkReader>(new ReferenceLinkReader(world, access));
}

/// <summary>
/// Canaries: a reader with one planted leak must fail the contract. If a leak passes, the contract has a
/// blind spot, and a real reader with the same bug would pass too.
/// </summary>
[Trait("Category", "Integration")]
public class LinkReaderContractCanaryTests
{
    [Theory]
    [InlineData(PlantedLeak.AllEvidence)]
    [InlineData(PlantedLeak.FirstName)]
    [InlineData(PlantedLeak.AllKinds)]
    [InlineData(PlantedLeak.WalkThroughHidden)]
    [InlineData(PlantedLeak.LinkWithoutEvidence)]
    [InlineData(PlantedLeak.CachedAccess)]
    [InlineData(PlantedLeak.FailOpen)]
    public async Task Contract_ReaderWithPlantedLeak_Fails(PlantedLeak leak)
    {
        LinkReaderFactory leaky = (world, access) => Task.FromResult<ILinkReader>(new ReferenceLinkReader(world, access, leak));

        List<string> violations = [
            .. await LinkContractCases.GeneratedAsync(leaky, worlds: 100),
            .. await LinkContractCases.RevocationAsync(leaky),
            .. await LinkContractCases.FailedCheckAsync(leaky),
            .. await LinkContractCases.NamedAsync(leaky),
        ];

        violations.Should().NotBeEmpty($"the contract must catch a reader that leaks by {leak}");
    }

    [Fact]
    public async Task ReferenceReader_ChecksAccessOncePerQuery()
    {
        (LinkWorld world, DocumentAccessStub access) = LinkWorld.Generate(42);
        ReferenceLinkReader reader = new(world, access);

        await reader.ExpandAsync(new Core.LinkExpandRequest(world.Users[0], [world.Nodes[0].Id], 2));
        await reader.ExpandAsync(new Core.LinkExpandRequest(world.Users[0], [world.Nodes[0].Id], 2));

        access.Calls.Should().Be(2);
    }
}
