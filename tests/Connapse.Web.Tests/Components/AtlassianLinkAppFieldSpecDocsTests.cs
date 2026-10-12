using Connapse.Web.Components.Providers;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>The Atlassian setup guide documents every field the linking-app form renders from the spec.</summary>
[Trait("Category", "Unit")]
public class AtlassianLinkAppFieldSpecDocsTests
{
    private static string? Guide()
    {
        string path = Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "atlassian-setup.md");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Fact]
    public void Fields_AreTheTwoValuesTheDeveloperConsoleShows_InThatOrder()
    {
        // The guide must name each of these labels; the checks below read it once it exists.
        AtlassianLinkAppFieldSpecs.All.Select(f => f.Label).Should().Equal("Client ID", "Client secret");
        // Hand-entered provider values sit under the same heading on every provider page.
        AtlassianLinkAppFieldSpecs.All.Select(f => f.Group).Distinct().Should().Equal([GitHubFieldSpecs.AppId.Group]);
    }

    [Fact]
    public void SetupGuide_NamesEveryLinkingAppField()
    {
        // The guide is written separately; until it is in the repository there is nothing to compare.
        if (Guide() is not { } guide) return;

        foreach (var field in AtlassianLinkAppFieldSpecs.All)
            guide.Should().Contain(field.Label, $"the guide must say where the {field.Id} value comes from");
    }

    [Fact]
    public void EveryGuideLink_PointsAtASectionOfTheGuide()
    {
        if (Guide() is not { } guide) return;

        var anchors = guide.Split('\n')
            .Where(line => line.StartsWith('#'))
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line.TrimStart('#').Trim().ToLowerInvariant(), @"[^a-z0-9 -]", "").Replace(' ', '-'))
            .ToHashSet();

        foreach (var url in AtlassianLinkAppFieldSpecs.All.Select(f => f.GuideUrl)
                     .Append(AtlassianLinkAppFieldSpecs.GuideSection)
                     .Append(AtlassianLinkAppFieldSpecs.TroubleshootingRejected))
            anchors.Should().Contain(url.Split('#')[1], $"{url} links to a section the guide must have");
    }

    [Fact]
    public void EverySecretField_IsASecretKind()
    {
        AtlassianLinkAppFieldSpecs.All.Where(f => f.Label.Contains("secret", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(f => f.IsSecret, "a secret must render as SecretTextArea");
    }
}
