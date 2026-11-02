using System.Text.RegularExpressions;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// One name per operation on every Atlassian surface, matching the other providers on the same page:
/// "Check and save" stores only what Atlassian accepts (as AWS and GitHub on the Providers page);
/// asking again with what is stored is "Recheck" on the Providers page (as AWS, Azure and GitHub)
/// and "Test connection" on the Connections page (as every connection); "Details" opens the
/// provider's own answer, and "Pause syncing" stops syncing a site or a source.
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
        foreach (string doc in new[] { Path.Combine("sources", "atlassian-setup.md"), Path.Combine("contributing", "confluence-test-site.md") })
            File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", doc)).Should().NotContain($"**{retired}**");
    }

    [Fact]
    public void SiteAndLinkingApp_UseTheSameNamesForCheckAndSave()
    {
        Read("Connections/AtlassianConnectionForm.razor").Should().Contain("<span>Check and save</span>").And.Contain("<span>Check and replace</span>");
        Read("Providers/AtlassianProviderCard.razor").Should().Contain("<span>Check and save</span>").And.Contain(">Check and replace</button>");
    }

    [Fact]
    public void ProvidersPage_NamesTheStoredCheckRecheck_ForEveryProvider()
    {
        string app = Read("Providers/AtlassianProviderCard.razor");
        string providers = Read("Pages/Providers.razor");

        app.Should().Contain("<span>Recheck</span>").And.NotContain("Test connection");
        Regex.Matches(providers, @">Recheck</button>").Should().HaveCount(3, "AWS, Azure and GitHub each have one");
        providers.Should().NotContain(">Test connection<");
    }

    [Fact]
    public void ConnectionsPage_NamesTheStoredCheckTestConnection_ForEveryConnection()
    {
        Read("Connections/AtlassianConnectionForm.razor").Should().Contain("<span>Test connection</span>").And.NotContain("Recheck");
        Read("Pages/Connections.razor").Should().Contain("<span>Test connection</span>").And.NotContain(">Recheck<");
    }

    [Fact]
    public void NewAndEditSource_LabelTheSyncIntervalTheSameWay()
    {
        foreach (string page in new[] { "Pages/Sources.razor", "Pages/SourceEdit.razor" })
            Read(page).Should().Contain(">Sync interval in seconds <span class=\"text-muted\">(optional)</span></label>", page);
    }

    [Fact]
    public void SitesAndSources_BothPauseAndResumeSyncing()
    {
        Read("Pages/Connections.razor").Should().Contain("\"Resume syncing\" : \"Pause syncing\"");
        Read("Pages/Sources.razor").Should().Contain("\"Pause syncing\" : \"Resume syncing\"");
    }
}
