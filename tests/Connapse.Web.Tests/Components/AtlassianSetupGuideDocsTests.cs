using System.Text.RegularExpressions;
using Connapse.Storage.Connectors.Atlassian;
using Connapse.Web.Components.Pages;
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
        "### Space can no longer be read",
        "### Attachments skipped",
        "### Couldn't reach Atlassian",
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
        ConfluenceSpaceSelection.MissingScopeGuideUrl.Should().StartWith("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#");
        anchors.Should().Contain(ConfluenceSpaceSelection.MissingScopeGuideUrl.Split('#')[1]);
    }

    private static HashSet<string> Anchors() => Guide().Split('\n')
        .Where(line => line.StartsWith('#'))
        .Select(line => Regex.Replace(line.TrimStart('#').Trim().ToLowerInvariant(), @"[^a-z0-9 -]", "").Replace(' ', '-'))
        .ToHashSet();

    [Fact]
    public void EveryLinkOutsideTheSiteTest_PointsAtASectionOfTheGuide()
    {
        var anchors = Anchors();
        var urls = new List<string>
        {
            ProfileIntegrations.AtlassianLinkAppRejectedHelp,
            ProfileIntegrations.AtlassianSignInFailedHelp,
        };
        urls.AddRange(Connapse.Storage.ConnectionTesters.AtlassianSiteTroubleshooting.Elsewhere
            .Select(Connapse.Storage.ConnectionTesters.AtlassianSiteTroubleshooting.UrlOf));

        foreach (string url in urls)
        {
            url.Should().StartWith("https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#");
            anchors.Should().Contain(url.Split('#')[1], "the page links to this section");
        }
    }

    [Theory]
    [InlineData("atlassian_app_rejected", "#linking-app-rejected")]
    [InlineData("atlassian_not_configured", "#sign-in-failed")]
    [InlineData("atlassian_link_expired", "#sign-in-failed")]
    [InlineData("atlassian_sign_in_off", "#sign-in-failed")]
    [InlineData("atlassian_link_failed", "#sign-in-failed")]
    public void ProfileSignInError_LinksTheGuideEntryThatExplainsIt(string error, string anchor)
    {
        ProfileIntegrations.AtlassianHelpFor(error).Should().EndWith("atlassian-setup.md" + anchor);

        string page = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "ProfileIntegrations.razor"));
        page.Should().Contain(">What this means<", "the link reads as help to the person who hit the error");
    }

    [Fact]
    public void SetupGuide_QuotesEverySyncErrorASourceCanShow()
    {
        string guide = Flat(Guide());

        // The guide quotes each message's first sentence, which is what an administrator sees first.
        foreach (string message in new[]
                 {
                     AtlassianSyncErrors.CredentialRefused,
                     AtlassianSyncErrors.FreshTokenRefused,
                     AtlassianSyncErrors.Unavailable,
                     AtlassianSyncErrors.SpaceUnreadable("ENG", 403)["Confluence space ENG ".Length..],
                     AtlassianSyncErrors.SpaceUnreadable("ENG", 404)["Confluence space ENG can no longer be read: ".Length..],
                 })
        {
            string firstSentence = message[..(message.IndexOf(". ", StringComparison.Ordinal) + 1)];
            guide.Should().Contain(firstSentence);
        }
    }

    /// <summary>Markdown wraps lines; a message quoted in the guide is compared with whitespace collapsed.</summary>
    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    [Fact]
    public void SetupGuide_QuotesEveryAtlassianSignInErrorTheProfilePageShows()
    {
        string page = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "ProfileIntegrations.razor"));
        var messages = Regex.Matches(page, @"(?:""atlassian_[a-z_]+""|_) => ""(?<message>[^""]+)""")
            .Select(m => m.Groups["message"].Value)
            .Where(m => m.Contains("Atlassian", StringComparison.Ordinal))
            .ToList();
        string guide = Flat(Guide());

        messages.Should().HaveCountGreaterThanOrEqualTo(8, "every error code, and the fallback, has its own message");
        foreach (string message in messages)
            guide.Should().Contain(message, "the Sign-in failed table quotes each message exactly");
    }

    [Fact]
    public void SetupGuide_QuotesTheAttachmentScopeWarningAndTheFixedSpaceNote()
    {
        string guide = Flat(Guide());

        guide.Should().Contain(Connapse.Storage.Connectors.Atlassian.ConfluenceSpaceConnector.AttachmentScopeNotice);
        guide.Should().Contain(ConfluenceSpaceSelection.SpaceFixedNote);
        guide.Should().Contain("**Check and save**").And.NotContain("choose **Save**:");
    }
}
