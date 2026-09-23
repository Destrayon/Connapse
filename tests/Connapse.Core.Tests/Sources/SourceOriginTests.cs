using Connapse.Web.Components.Settings;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>The New source dialog's first choice: a connection, or a kind that needs none.</summary>
[Trait("Category", "Unit")]
public class SourceOriginTests
{
    [Fact]
    public void Parse_ConnectionValue_RoundTrips()
    {
        var id = Guid.NewGuid();
        var origin = SourceOrigin.Parse(SourceOrigin.ForConnection(id).Value);

        origin.ConnectionId.Should().Be(id);
        origin.Kind.Should().BeNull();
    }

    [Fact]
    public void Parse_ConnectionlessKindValue_RoundTrips()
    {
        var origin = SourceOrigin.Parse(SourceOrigin.ForKind(ConnectionlessSourceKinds.GitHub).Value);

        origin.Kind.Should().Be(ConnectionlessSourceKinds.GitHub);
        origin.ConnectionId.Should().BeNull();
        origin.Kind!.Provider.Should().Be(ConnectionProvider.GitHub);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("connection:not-a-guid")]
    [InlineData("public:ftp")]
    [InlineData("github")]
    public void Parse_AnythingElse_IsEmpty(string? value)
    {
        SourceOrigin.Parse(value).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ConnectionlessKinds_HaveUniqueKeys()
    {
        ConnectionlessSourceKinds.All.Select(k => k.Key).Should().OnlyHaveUniqueItems();
    }
}
