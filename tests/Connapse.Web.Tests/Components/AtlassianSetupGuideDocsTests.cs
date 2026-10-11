using System.Text.RegularExpressions;
using Connapse.Web.Components.Sources;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The Atlassian setup guide has every section the app links to, and names every field the
/// Confluence source form shows.
/// </summary>
[Trait("Category", "Unit")]
public class AtlassianSetupGuideDocsTests
{
    private static readonly string[] Headings =
    [
        "## Before you start",
        "## 1. Linking app",
        "## 2. Service account",
        "## 3. Site connection",
        "## 4. Confluence sources",
        "## Troubleshooting",
        "### Site not found",
        "### Client ID or secret rejected",
        "### Can't read Confluence",
        "### No Confluence access or no spaces",
        "### Not Confluence Administrator",
        "### Missing scope",
        "### Site has no pages or no other users yet",
        "### Linking app rejected",
        "### Sign-in failed",
    ];

    private static string Guide() =>
        File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "atlassian-setup.md"));

    [Fact]
    public void SetupGuide_HasEverySectionTheAppLinksTo()
    {
        var lines = Guide().Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet();

        foreach (string heading in Headings)
            lines.Should().Contain(heading, "in-app links point at this section");
    }

    [Fact]
    public void SetupGuide_NamesEveryConfluenceSourceField()
    {
        string guide = Guide();

        foreach (string label in ConfluenceSpaceSelection.FieldLabels)
            guide.Should().Contain(label, $"the guide must explain the \"{label}\" field");
    }

    [Fact]
    public void SourceFormGuideLink_PointsAtASectionOfTheGuide()
    {
        var anchors = Guide().Split('\n')
            .Where(line => line.StartsWith('#'))
            .Select(line => Regex.Replace(line.TrimStart('#').Trim().ToLowerInvariant(), @"[^a-z0-9 -]", "").Replace(' ', '-'))
            .ToHashSet();

        ConfluenceSpaceSelection.GuideUrl.Should().StartWith("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#");
        anchors.Should().Contain(ConfluenceSpaceSelection.GuideUrl.Split('#')[1]);
    }
}
