using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The New source dialog on a GitHub App connection. Read from source, like the other page tests:
/// the page has no render harness, and the property that matters — no private repository is
/// indexed before search can filter it per user — must not be lost in an edit.
/// </summary>
[Trait("Category", "Unit")]
public class NewSourceGitHubTests
{
    private static readonly string Markup = File.ReadAllText(Path.Combine(
        PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "Sources.razor"));

    [Fact]
    public void GitHubConnection_ShowsRepositoryFieldsAndCreatesThroughTheGitHubPath()
    {
        Markup.Should().Contain("selectedProvider is ConnectionProvider.GitHub")
            .And.Contain("connection.Provider == ConnectionProvider.GitHub")
            .And.Contain("CreateGitHubRepositoryAsync(connection)");
    }

    [Fact]
    public void GitHubRepository_IsLookedUpAndAddedAsPrivateUnlessPublic()
    {
        // Private repositories are indexed as private (#526): marked, with their id, so their
        // documents are filtered per user rather than refused.
        Markup.Should().Contain("GitHubRepositories.FindAsync(")
            .And.Contain("bool isPrivate = !found.IsPublic;")
            .And.Contain("githubForm.ToRequests(connection.Id, found.Id, isPrivate)");
    }
}
