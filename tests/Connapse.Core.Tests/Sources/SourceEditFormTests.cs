using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Storage.Connectors;
using Connapse.Web.Components.Settings;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>The source edit page's rules: what it reads from a source, and the update it sends.</summary>
[Trait("Category", "Unit")]
public class SourceEditFormTests
{
    private static Source MakeSource(string scope, int? interval = null) => new(
        Id: Guid.NewGuid(), Name: "octocat/hello issues", Description: "Issues", ConnectionId: Guid.NewGuid(),
        ScopeJson: scope, CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, SyncIntervalSeconds: interval);

    private const string IssuesScope = """{"owner":"octocat","repo":"hello","kind":"IssuesAndPullRequests","includeComments":true}""";

    [Fact]
    public void From_GitHubIssuesSource_ReadsItsCommentSettings()
    {
        var source = MakeSource("""{"owner":"o","repo":"r","kind":"IssuesAndPullRequests","includeCommentAuthors":["coderabbitai[bot]"],"excludeCommentAuthors":["ci-deploy"]}""");

        var form = SourceEditForm.From(source, ConnectionProvider.GitHub);

        form.GitHubKind.Should().Be(GitHubContentKind.IssuesAndPullRequests);
        form.IncludedBots.Should().Equal("coderabbitai[bot]");
        form.ExcludedPeople.Should().Equal("ci-deploy");
    }

    [Fact]
    public void From_NonGitHubSource_HasNoProviderSection()
    {
        SourceEditForm.From(MakeSource("""{"bucketName":"b"}"""), ConnectionProvider.S3).GitHubKind.Should().BeNull();
    }

    [Fact]
    public void ToRequest_NothingChanged_IsNull()
    {
        var source = MakeSource(IssuesScope, interval: 900);

        SourceEditForm.From(source, ConnectionProvider.GitHub).ToRequest(source).Should().BeNull();
    }

    [Fact]
    public void ToRequest_RenameOnly_LeavesTheScopeAlone()
    {
        var source = MakeSource(IssuesScope);
        var form = SourceEditForm.From(source, ConnectionProvider.GitHub);
        form.Name = "Renamed";

        var request = form.ToRequest(source)!;

        request.Name.Should().Be("Renamed");
        request.ScopeJson.Should().BeNull("an unchanged scope is not sent, so a rename does not re-read every issue");
    }

    [Fact]
    public void ToRequest_AuthorsChosen_WritesOnlyThoseListsIntoTheScope()
    {
        var source = MakeSource(IssuesScope);
        var form = SourceEditForm.From(source, ConnectionProvider.GitHub);
        form.IncludedBots.Add("coderabbitai[bot]");
        form.ExcludedPeople.Add("ci-deploy");

        var scope = JsonNode.Parse(form.ToRequest(source)!.ScopeJson!)!.AsObject();

        scope["owner"]!.GetValue<string>().Should().Be("octocat");
        scope["includeComments"]!.GetValue<bool>().Should().BeTrue();
        scope[SourceEditForm.IncludeAuthorsKey]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("coderabbitai[bot]");
        scope[SourceEditForm.ExcludeAuthorsKey]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("ci-deploy");
        SourceEditForm.From(source with { ScopeJson = scope.ToJsonString() }, ConnectionProvider.GitHub)
            .ToRequest(source with { ScopeJson = scope.ToJsonString() }).Should().BeNull("reopening shows what was saved");
    }

    [Fact]
    public void ToRequest_BlankInterval_KeepsTheStoredOne()
    {
        var source = MakeSource(IssuesScope, interval: 900);
        var form = SourceEditForm.From(source, ConnectionProvider.GitHub);
        form.SyncIntervalSeconds = null;

        form.ToRequest(source).Should().BeNull("the store has no way back to the default, so blank means unchanged");
    }

    [Theory]
    [InlineData("", null, "A source name is required.")]
    [InlineData("ok", 30, "Sync interval must be at least 60 seconds.")]
    [InlineData("ok", 60, null)]
    public void Validate_NameAndInterval(string name, int? interval, string? expected)
    {
        var form = SourceEditForm.From(MakeSource(IssuesScope), ConnectionProvider.GitHub);
        form.Name = name;
        form.SyncIntervalSeconds = interval;

        form.Validate().Should().Be(expected);
    }

    [Fact]
    public void Validate_BothFieldsWrong_GivesEachFieldItsOwnError()
    {
        var form = SourceEditForm.From(MakeSource(IssuesScope), ConnectionProvider.GitHub);
        form.Name = " ";
        form.SyncIntervalSeconds = 10;

        form.Validate().Should().Be(SourceEditForm.NameRequired);
        form.NameError.Should().Be(SourceEditForm.NameRequired);
        form.SyncIntervalError.Should().Be(SourceEditForm.IntervalTooShort, "the page shows both at once, beside their fields");

        form.Name = "fixed";
        form.ValidateName().Should().BeTrue();
        form.NameError.Should().BeNull();
        form.SyncIntervalError.Should().NotBeNull("checking one field leaves the other's error alone");
    }

    private const string ConfluenceScope =
        """{"kind":"confluence-space","spaceId":"100","spaceKey":"ENG","spaceName":"Engineering","includeAttachments":true,"maxAttachmentMb":25}""";

    [Fact]
    public void From_ConfluenceSource_ReadsItsAttachmentSettings()
    {
        var source = MakeSource("""{"kind":"confluence-space","spaceId":"100","spaceKey":"ENG","includeAttachments":false,"maxAttachmentMb":40}""");

        var form = SourceEditForm.From(source, ConnectionProvider.Atlassian);

        form.Confluence.Should().NotBeNull();
        form.Confluence!.IncludeAttachments.Should().BeFalse();
        form.Confluence.MaxAttachmentMb.Should().Be(40);
        form.GitHubKind.Should().BeNull();
    }

    [Fact]
    public void From_ConfluenceSourceWithoutAttachmentKeys_ShowsTheConnectorsDefaultsAndChangesNothing()
    {
        var source = MakeSource("""{"kind":"confluence-space","spaceId":"100","spaceKey":"ENG"}""");

        var form = SourceEditForm.From(source, ConnectionProvider.Atlassian);

        form.Confluence!.IncludeAttachments.Should().BeTrue();
        form.Confluence.MaxAttachmentMb.Should().Be(25);
        form.ToRequest(source).Should().BeNull("showing the defaults must not rewrite the scope and force a re-read");
    }

    [Fact]
    public void ToRequest_ConfluenceAttachmentsChanged_KeepsTheSpaceAndWritesTheNewSettings()
    {
        var source = MakeSource(ConfluenceScope);
        var form = SourceEditForm.From(source, ConnectionProvider.Atlassian);
        form.Confluence!.MaxAttachmentMb = 60;

        var scope = JsonNode.Parse(form.ToRequest(source)!.ScopeJson!)!.AsObject();

        scope["spaceId"]!.GetValue<string>().Should().Be("100");
        scope["spaceKey"]!.GetValue<string>().Should().Be("ENG");
        scope["includeAttachments"]!.GetValue<bool>().Should().BeTrue();
        scope["maxAttachmentMb"]!.GetValue<int>().Should().Be(60);
    }

    [Fact]
    public void ToRequest_ConfluenceAttachmentsTurnedOffWithABadSize_StoresOffAndKeepsTheStoredSize()
    {
        var source = MakeSource(ConfluenceScope);
        var form = SourceEditForm.From(source, ConnectionProvider.Atlassian);
        form.Confluence!.MaxAttachmentMb = 0;
        form.Confluence.IncludeAttachments = false;

        form.Validate().Should().BeNull("the size is not checked with attachments off, as on the New source dialog");
        var scope = JsonNode.Parse(form.ToRequest(source)!.ScopeJson!)!.AsObject();

        scope["includeAttachments"]!.GetValue<bool>().Should().BeFalse();
        scope["maxAttachmentMb"]!.GetValue<int>().Should().Be(25);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_ConfluenceSizeOutOfRange_GivesTheDialogsError(int size)
    {
        var form = SourceEditForm.From(MakeSource(ConfluenceScope), ConnectionProvider.Atlassian);
        form.Confluence!.MaxAttachmentMb = size;

        form.Validate().Should().Be(Connapse.Web.Components.Sources.ConfluenceSpaceSelection.AttachmentRangeError);
        form.Confluence.MaxAttachmentMbError.Should().NotBeNull();
    }
}
