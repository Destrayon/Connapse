using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests;

[Trait("Category", "Unit")]
public class IngestionStateTests
{
    [Fact]
    public void DefaultValue_IsPending()
    {
        IngestionState defaultState = default;
        defaultState.Should().Be(IngestionState.Pending);
    }

    [Fact]
    public void EnumHasFourMembers()
    {
        Enum.GetNames(typeof(IngestionState)).Should().BeEquivalentTo(
            new[] { "Pending", "Indexed", "SummaryIndexed", "Failed" });
    }
}
