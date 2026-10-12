using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Core;
using Connapse.Storage.ConnectionTesters;
using Connapse.Web.Components.Connections;
using Connapse.Web.Endpoints;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The Atlassian site form is driven by <see cref="AtlassianSiteFieldSpecs"/>; the setup guide must
/// name every label and have every section the form and its test results link to.
/// </summary>
[Trait("Category", "Unit")]
public class AtlassianSiteFieldSpecDocsTests
{
    private static string GuidePath => Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "atlassian-setup.md");

    [Fact]
    public void TheGuideMustName_TheseLabels()
    {
        AtlassianSiteFieldSpecs.GuideLabels.Should().Equal("Site URL", "Client ID", "Client secret", "Cloud ID");
        AtlassianSiteFieldSpecs.All.Select(f => f.Label).Distinct()
            .Should().BeSubsetOf(AtlassianSiteFieldSpecs.GuideLabels);
    }

    [Fact]
    public void TheGuideMustHave_TheseAnchors()
    {
        AtlassianSiteFieldSpecs.GuideAnchors.Should().Equal(
            "2-service-account", "3-site-connection",
            "site-not-found", "client-id-or-secret-rejected", "cant-read-confluence",
            "no-confluence-access-or-no-spaces", "not-confluence-administrator", "missing-scope",
            "site-has-no-pages-or-no-other-users-yet", "troubleshooting");
        AtlassianSiteFieldSpecs.All.Select(f => f.GuideUrl.Split('#')[1]).Should().BeSubsetOf(AtlassianSiteFieldSpecs.GuideAnchors);
    }

    /// <summary>Checked against the guide once it is in the tree; until then the lists above are the contract.</summary>
    [Fact]
    public void SetupGuide_WhenPresent_NamesEveryLabelAndHasEveryAnchor()
    {
        if (!File.Exists(GuidePath))
            return;

        string guide = File.ReadAllText(GuidePath);
        foreach (string label in AtlassianSiteFieldSpecs.GuideLabels)
            guide.Should().Contain(label, $"the guide must say where the {label} value comes from");

        var anchors = guide.Split('\n')
            .Where(line => line.StartsWith('#'))
            .Select(line => Regex.Replace(line.TrimStart('#').Trim().ToLowerInvariant(), @"[^a-z0-9 -]", "").Replace(' ', '-'))
            .ToHashSet();
        foreach (string anchor in AtlassianSiteFieldSpecs.GuideAnchors)
            anchors.Should().Contain(anchor, "the page links to this section");
    }

    [Fact]
    public void EverySecretField_IsASecretKind()
    {
        AtlassianSiteFieldSpecs.All.Where(f => f.Label.Contains("secret", StringComparison.OrdinalIgnoreCase))
            .Should().NotBeEmpty().And.OnlyContain(f => f.IsSecret);
    }

    [Fact]
    public void RestApi_UsesTheSameFieldNamesAsTheForm()
    {
        string create = JsonSerializer.Serialize(new CreateAtlassianSiteRequest("s", "i", "x"), JsonSerializerOptions.Web);
        string replace = JsonSerializer.Serialize(new ReplaceAtlassianCredentialsRequest("i", "x"), JsonSerializerOptions.Web);

        foreach (string label in new[] { "Site URL", "Client ID", "Client secret" })
            create.Should().Contain($"\"{AtlassianSiteFieldSpecs.ApiNames[label]}\"");
        foreach (string label in new[] { "Client ID", "Client secret" })
            replace.Should().Contain($"\"{AtlassianSiteFieldSpecs.ApiNames[label]}\"");
    }

    [Fact]
    public void EveryTroubleshootingAnchor_IsAGuideAnchor() =>
        AtlassianSiteTroubleshooting.All.Should().BeSubsetOf(AtlassianSiteFieldSpecs.GuideAnchors);
}

/// <summary>A site's health as the Connections list and the site view show it.</summary>
[Trait("Category", "Unit")]
public class AtlassianSiteStatusTests
{
    private static Connection Site(ConnectionHealth? health, bool hasSecret = true) => new(
        Guid.NewGuid(), "acme.atlassian.net", ConnectionProvider.Atlassian, "{}", null, DateTime.UtcNow, DateTime.UtcNow,
        HasSecret: hasSecret, Health: health);

    private static ConnectionHealth Check(bool ok, bool rejected = false, bool warning = false, string? step = null) =>
        new(DateTime.UtcNow, ok, step, "m", "test", rejected, warning);

    [Fact]
    public void Status_ReadsEachHealth()
    {
        AtlassianSiteStatus.Of(Site(null)).Text.Should().StartWith("Not checked yet");
        AtlassianSiteStatus.Of(Site(Check(ok: true))).Text.Should().Be("Working");
        AtlassianSiteStatus.Of(Site(Check(ok: false, warning: true, step: "admin"))).Text.Should().Contain("not confirmed");
        AtlassianSiteStatus.Of(Site(Check(ok: false, step: "spaces"))).Text.Should().Be("Last check failed: Listing spaces");
    }

    [Fact]
    public void Status_RejectedOrMissingSecret_NeedsNewCredentials()
    {
        AtlassianSiteStatus.Of(Site(Check(ok: false, rejected: true, step: "token"))).NeedsNewCredentials.Should().BeTrue();
        AtlassianSiteStatus.Of(Site(null, hasSecret: false)).NeedsNewCredentials.Should().BeTrue();
        AtlassianSiteStatus.Of(Site(Check(ok: false, step: "admin"))).NeedsNewCredentials.Should().BeFalse();
    }
}

/// <summary>
/// The Connections page's delete and list markup, read from the source as the page's other tests
/// do: the page itself needs too many services to render here.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionsPageLifecycleTests
{
    private static readonly string Markup = File.ReadAllText(Path.Combine(
        PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "Connections.razor"));

    [Fact]
    public void Delete_RequiresTypingTheConnectionName()
    {
        Markup.Should().Contain("id=\"delete-connection-confirm\"")
            .And.Contain("disabled=\"@(deleteConfirmText?.Trim() != pendingDelete.Name)\"")
            .And.Contain("deleteConfirmText?.Trim() != connection.Name) return;");
    }

    [Fact]
    public void Delete_IsSetApartFromPauseSyncing()
    {
        int pause = Markup.IndexOf("Pause syncing", StringComparison.Ordinal);
        int delete = Markup.IndexOf("@onclick=\"() => ConfirmDelete(c)\"", StringComparison.Ordinal);
        Markup[pause..delete].Should().Contain("border-start ms-2 ps-2");
    }

    [Fact]
    public void AtlassianRows_ShowTheSiteStatus()
    {
        Markup.Should().Contain("AtlassianSiteStatus.Of(c)").And.Contain("Replace credentials");
    }

    [Fact]
    public void NewAtlassianSite_KeepsTheNameFieldInPlace_DisabledAndSayingWhy()
    {
        Markup.Should().Contain("disabled=\"@NewAtlassianSite\"")
            .And.Contain("Atlassian sites are named after their host (for example acme.atlassian.net) when they are saved. You can rename it after saving.");
        int name = Markup.IndexOf("for=\"connection-name\"", StringComparison.Ordinal);
        Markup.LastIndexOf("@if (!NewAtlassianSite)", name, StringComparison.Ordinal).Should().Be(-1,
            "nothing above the Name field may hide it for a new Atlassian site");
    }

    [Fact]
    public void SaveName_StepsDownToSecondaryWhileCredentialsAreBeingReplaced()
    {
        Markup.Should().Contain("ReplacingChanged=\"@((bool open) => atlassianReplacing = open)\"")
            .And.Contain("class=\"btn @(atlassianReplacing ? \"btn-outline-secondary\" : \"btn-primary\") me-2\" @onclick=\"Save\"");
    }

    [Fact]
    public void TestConnection_IsASecondaryButton_SoSaveIsTheOnlyFilledPrimary()
    {
        Markup.Should().Contain("<button id=\"connection-test\" class=\"btn btn-outline-secondary\" @onclick=\"Test\"")
            .And.NotContain("btn-info", "a filled Test button competes with the one primary action");
    }

    [Fact]
    public void RenameDeleteAndPause_GoThroughTheSharedServiceTheRestApiUses()
    {
        Markup.Should().Contain("await Admin.SaveAsync(editing!.Id,")
            .And.Contain("await Admin.DeleteAsync(connection.Id, connection.Name)")
            .And.Contain("await Admin.SetSyncingAsync(connection, enabled, only)")
            .And.NotContain("ConnectionStore.DeleteAsync(")
            .And.NotContain("ConnectionStore.UpdateAsync(");
    }

    [Fact]
    public void RestComment_NamesTheAtlassianException()
    {
        Markup.Should().Contain("The one exception is an Atlassian site");
    }
}
