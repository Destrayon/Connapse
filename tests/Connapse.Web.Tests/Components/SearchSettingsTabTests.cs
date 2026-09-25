using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// Switching the reranker provider away from TEI and back must not replace the deployment's TEI
/// address with localhost. A saved setting outranks the Compose environment, so a saved
/// <c>http://localhost:8080</c> points the web container at itself and every rerank silently falls
/// back. Source-scanned for the same reason as <see cref="SamlSignInSettingsTabTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class SearchSettingsTabTests
{
    private static string FormSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test walks up to the solution file to find the component");
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Connapse.Web", "Components", "Settings", "SearchSettingsTab.razor"));
    }

    [Fact]
    public void ProviderSwitchToTei_RestoresTheResolvedAddress_LocalhostOnlyAsLastResort()
    {
        string source = FormSource();

        source.Should().Contain("teiBaseUrl ?? \"http://localhost:8080\"");
        source.Split('\n')
            .Where(line => line.Contains("\"http://localhost:8080\""))
            .Should().OnlyContain(
                line => line.Contains("placeholder=") || line.Contains("teiBaseUrl ??"),
                "localhost is only a hint or the fallback when no TEI address was ever resolved");
    }
}
