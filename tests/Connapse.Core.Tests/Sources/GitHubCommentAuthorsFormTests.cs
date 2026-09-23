using System.Text.Json.Nodes;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Settings;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Sources;

/// <summary>The "Comments from" checklist on a GitHub issues source, and the scope it writes.</summary>
[Trait("Category", "Unit")]
public class GitHubCommentAuthorsFormTests
{
    private static readonly GitHubCommentAuthor[] Authors =
    [
        new("coderabbitai[bot]", IsBot: true, Comments: 435),
        new("Destrayon", IsBot: false, Comments: 156),
        new("ci-deploy", IsBot: false, Comments: 3),
    ];

    private const string Scope = """{"owner":"o","repo":"r","kind":"IssuesAndPullRequests"}""";

    [Fact]
    public void From_NothingChosenYet_TicksPeopleAndLeavesBotsUnticked()
    {
        var form = GitHubCommentAuthorsForm.From(Scope, Authors);

        form.Items.Select(i => (i.Author.Login, i.Included)).Should().Equal(
            ("coderabbitai[bot]", false), ("Destrayon", true), ("ci-deploy", true));
    }

    [Fact]
    public void ApplyTo_SavesOnlyTheExceptions_AndKeepsTheRestOfTheScope()
    {
        var form = GitHubCommentAuthorsForm.From(Scope, Authors);
        form.Items[0].Included = true;  // keep the review bot
        form.Items[2].Included = false; // drop the machine user

        var scope = JsonNode.Parse(form.ApplyTo(Scope))!.AsObject();

        scope["kind"]!.GetValue<string>().Should().Be("IssuesAndPullRequests");
        scope[GitHubCommentAuthorsForm.IncludeKey]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("coderabbitai[bot]");
        scope[GitHubCommentAuthorsForm.ExcludeKey]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("ci-deploy");
        GitHubCommentAuthorsForm.From(scope.ToJsonString(), Authors).Items.Select(i => i.Included)
            .Should().Equal([true, true, false], "saving and reopening shows the same choices");
    }

    [Fact]
    public void ApplyTo_DefaultChoices_WriteNoLists()
    {
        string saved = GitHubCommentAuthorsForm.From(Scope, Authors).ApplyTo(Scope);

        var scope = JsonNode.Parse(saved)!.AsObject();
        scope.ContainsKey(GitHubCommentAuthorsForm.IncludeKey).Should().BeFalse();
        scope.ContainsKey(GitHubCommentAuthorsForm.ExcludeKey).Should().BeFalse();
    }

    [Fact]
    public void ApplyTo_AuthorNamedEarlierButNotSeenNow_KeepsTheirEntry()
    {
        const string scope = """{"kind":"IssuesAndPullRequests","excludeCommentAuthors":["old-ci-user"]}""";

        string saved = GitHubCommentAuthorsForm.From(scope, Authors).ApplyTo(scope);

        JsonNode.Parse(saved)![GitHubCommentAuthorsForm.ExcludeKey]!.AsArray().Select(n => n!.GetValue<string>())
            .Should().Equal("old-ci-user");
    }
}
