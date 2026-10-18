using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

[Trait("Category", "Unit")]
public class CustomSourceModelTests
{
    private static readonly string[] NoHttpHosts = [];

    [Fact]
    public void Custom_HasTheSameValueInBothEnums()
    {
        ((int)ConnectionProvider.Custom).Should().Be((int)ConnectorType.Custom);
    }

    [Fact]
    public void Scope_RoundTripsThroughJson()
    {
        CustomSourceScope scope = new("https://bridge.acme.test/api", "eng");

        CustomSourceScope.TryParse(scope.ToJson()).Should().Be(scope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "scopeId": "eng" }""")]
    [InlineData("""{ "baseUrl": "https://bridge.acme.test" }""")]
    [InlineData("""{ "baseUrl": "", "scopeId": "eng" }""")]
    public void Scope_MissingOrMalformed_ParsesToNull(string? json)
    {
        CustomSourceScope.TryParse(json).Should().BeNull();
    }

    [Theory]
    [InlineData("https://bridge.acme.test", "https://bridge.acme.test")]
    [InlineData("https://bridge.acme.test/", "https://bridge.acme.test")]
    [InlineData("https://bridge.acme.test/api/", "https://bridge.acme.test/api")]
    [InlineData("  https://Bridge.Acme.Test:8443/api  ", "https://bridge.acme.test:8443/api")]
    public void Endpoint_Https_IsAcceptedAndNormalised(string input, string expected)
    {
        CustomSourceEndpoint.TryNormalize(input, NoHttpHosts, out string normalized, out string? error)
            .Should().BeTrue(error);

        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("http://bridge.acme.test")]
    [InlineData("ftp://bridge.acme.test")]
    [InlineData("https://user:pass@bridge.acme.test")]
    [InlineData("https://bridge.acme.test/?x=1")]
    [InlineData("https://bridge.acme.test/#frag")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void Endpoint_UnsafeOrMalformed_IsRefused(string? input)
    {
        CustomSourceEndpoint.TryNormalize(input, NoHttpHosts, out _, out string? error).Should().BeFalse();

        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Endpoint_Http_IsAcceptedOnlyForAnAllowedHost()
    {
        string[] allowed = ["host.docker.internal", "LOCALHOST"];

        CustomSourceEndpoint.TryNormalize("http://host.docker.internal:8080", allowed, out string normalized, out _)
            .Should().BeTrue();
        normalized.Should().Be("http://host.docker.internal:8080");
        CustomSourceEndpoint.TryNormalize("http://localhost:8080", allowed, out _, out _).Should().BeTrue();
        CustomSourceEndpoint.TryNormalize("http://bridge.acme.test", allowed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void CreateSourceRequest_ToString_NeverPrintsTheSecret()
    {
        CreateSourceRequest request = new("s", null, "{}", Provider: ConnectionProvider.Custom, Secret: "hunter2-token");

        request.ToString().Should().NotContain("hunter2-token").And.Contain("***");
    }

    [Fact]
    public void SyncDelta_DeletedPrefixes_DefaultsToNone()
    {
        SyncDelta delta = new([], [], "c", RequiresFullResync: false);

        delta.DeletedPrefixes.Should().BeNull();
    }
}
