using System.Text.RegularExpressions;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// One name per operation on every Atlassian surface, matching the other providers: "Check and
/// save" stores only what Atlassian accepts (as AWS and GitHub on the Providers page), "Test
/// connection" asks again with what is stored (as every connection on the Connections page),
/// "Details" opens the provider's own answer, and "Pause syncing" stops syncing a site or a source.
/// </summary>
[Trait("Category", "Unit")]
public class AtlassianOperationNamesTests
{
    private static readonly string[] Surfaces =
    [
        "Connections/AtlassianConnectionForm.razor",
        "Connections/AtlassianTestResultView.razor",
        "Providers/AtlassianProviderCard.razor",
        "Sources/ConfluenceSpaceForm.razor",
        "Pages/Connections.razor",
        "Pages/Providers.razor",
        "Pages/Sources.razor",
        "Pages/SourceEdit.razor",
        "Pages/ProfileIntegrations.razor",
        "Shared/AtlassianIdentityCard.razor",
    ];

    private static string Read(string path) => Regex.Replace(
        File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", path)), @"\s+", " ");

    [Theory]
    [InlineData("Test and save")]
    [InlineData("Test and replace")]
    [InlineData("Show details")]
    [InlineData("Raw error")]
    [InlineData("\"Disable\"")]
    [InlineData("\"Enable\"")]
    public void NoAtlassianSurface_UsesARetiredName(string retired)
    {
        foreach (string surface in Surfaces)
            Read(surface).Should().NotContain(retired, $"{surface} names the operation as every other surface does");
        foreach (string doc in new[] { "atlassian-setup.md", "confluence-test-site.md" })
            File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", doc)).Should().NotContain($"**{retired}**");
    }

    [Fact]
    public void SiteAndLinkingApp_UseTheSameNamesForCheckAndSave_AndForTestConnection()
    {
        string site = Read("Connections/AtlassianConnectionForm.razor");
        string app = Read("Providers/AtlassianProviderCard.razor");

        site.Should().Contain("<span>Check and save</span>").And.Contain("<span>Check and replace</span>").And.Contain("<span>Test connection</span>");
        app.Should().Contain("<span>Check and save</span>").And.Contain(">Check and replace</button>").And.Contain("<span>Test connection</span>");
        app.Should().NotContain("<span>Recheck</span>");
    }

    [Fact]
    public void SitesAndSources_BothPauseAndResumeSyncing()
    {
        Read("Pages/Connections.razor").Should().Contain("\"Resume syncing\" : \"Pause syncing\"");
        Read("Pages/Sources.razor").Should().Contain("\"Pause syncing\" : \"Resume syncing\"");
    }
}
