using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

/// <summary>Which middle man addresses count as the same one, so a saved token is only ever sent to its own.</summary>
[Trait("Category", "Unit")]
public class CustomSourceEndpointTests
{
    [Theory]
    [InlineData("https://bridge.example/A", "https://bridge.example/A/")]
    [InlineData("https://Bridge.Example/A", "https://bridge.example/A")]
    [InlineData("HTTPS://bridge.example", "https://bridge.example")]
    [InlineData("https://bridge.example:443", "https://bridge.example")]
    [InlineData(" https://bridge.example ", "https://bridge.example")]
    public void SameAddress_SameServerAndPath_True(string a, string b) =>
        CustomSourceEndpoint.SameAddress(a, b).Should().BeTrue();

    [Theory]
    [InlineData("https://bridge.example/A", "https://bridge.example/a")]
    [InlineData("https://bridge.example", "https://other.example")]
    [InlineData("https://bridge.example", "http://bridge.example")]
    [InlineData("https://bridge.example:8443", "https://bridge.example")]
    [InlineData("https://bridge.example", null)]
    [InlineData(null, "https://bridge.example")]
    public void SameAddress_DifferentPathCaseOrServer_False(string? a, string? b) =>
        CustomSourceEndpoint.SameAddress(a, b).Should().BeFalse();
}
