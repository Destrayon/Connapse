using Connapse.Web.Components.Providers;
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>The GitHub setup guide documents every field the GitHub forms render from the spec.</summary>
[Trait("Category", "Unit")]
public class GitHubFieldSpecDocsTests
{
    [Fact]
    public void SetupGuide_NamesEveryGitHubField()
    {
        string guide = File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "github-setup.md"));

        foreach (var field in GitHubFieldSpecs.All)
            guide.Should().Contain(field.Label, $"the guide must say where the {field.Id} value comes from");
    }

    [Fact]
    public void EverySecretField_IsASecretKind()
    {
        GitHubFieldSpecs.All.Where(f => f.Label.Contains("key", StringComparison.OrdinalIgnoreCase)
                                         || f.Label.Contains("secret", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(f => f.IsSecret, "a key or secret must render as SecretTextArea");
    }
}
