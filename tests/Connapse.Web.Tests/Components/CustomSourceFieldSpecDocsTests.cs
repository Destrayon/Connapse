using Connapse.Web.Components.Providers;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>The custom source setup guide documents every field the custom provider and source pages show.</summary>
[Trait("Category", "Unit")]
public class CustomSourceFieldSpecDocsTests
{
    private static string Guide() => File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "sources", "custom-source-setup.md"));

    [Fact]
    public void SetupGuide_NamesEveryCustomField()
    {
        string guide = Guide();

        foreach (var field in CustomSourceFieldSpecs.All)
            guide.Should().Contain(field.Label, $"the guide must say where the {field.Id} value comes from");
    }

    [Fact]
    public void EveryGuideLink_PointsAtASectionOfTheGuide()
    {
        var anchors = Guide().Split('\n')
            .Where(line => line.StartsWith('#'))
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line.TrimStart('#').Trim().ToLowerInvariant(), @"[^a-z0-9 -]", "").Replace(' ', '-'))
            .ToHashSet();

        foreach (string url in CustomSourceFieldSpecs.All.Select(f => f.GuideUrl)
                     .Append(CustomSourceFieldSpecs.Prerequisites)
                     .Append(CustomSourceFieldSpecs.Troubleshooting))
            anchors.Should().Contain(url.Split('#')[1], $"{url} links to a section the guide must have");
    }

    [Fact]
    public void EveryTokenField_IsASecretKind()
    {
        CustomSourceFieldSpecs.All.Where(f => f.Label.Contains("token", StringComparison.OrdinalIgnoreCase))
            .Should().NotBeEmpty().And.OnlyContain(f => f.IsSecret, "a token must render as SecretTextArea");
    }
}
