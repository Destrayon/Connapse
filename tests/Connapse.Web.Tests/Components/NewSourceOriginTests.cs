using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The New source dialog offers sources that need no connection alongside the connections, and
/// stays usable when there are no connections at all. Read from source, like the other page tests.
/// </summary>
[Trait("Category", "Unit")]
public class NewSourceOriginTests
{
    private static readonly string Markup = File.ReadAllText(Path.Combine(
        PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "Sources.razor"));

    [Fact]
    public void Dialog_ListsConnectionlessKindsInTheirOwnGroup()
    {
        Markup.Should().Contain("<optgroup label=\"Your connections\">")
            .And.Contain("<optgroup label=\"No connection needed\">")
            .And.Contain("ConnectionlessSourceKinds.All");
    }

    [Fact]
    public void NewSourceButton_DoesNotRequireAConnection()
    {
        Markup.Should().Contain("<button class=\"btn btn-primary\" @onclick=\"BeginCreate\">")
            .And.NotContain("@onclick=\"BeginCreate\" disabled=");
    }

    [Fact]
    public void ProvidersPage_NoLongerHostsGitHub()
    {
        string providers = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "Providers.razor"));

        providers.Should().NotContain("Key == \"github\"",
            "Providers is for identity and permission setup; a public repository is a source");
    }
}
