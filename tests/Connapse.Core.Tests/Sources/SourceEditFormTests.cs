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
}
