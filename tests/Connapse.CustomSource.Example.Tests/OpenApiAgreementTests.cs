using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.CustomSource.Example.Tests;

[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class OpenApiAgreementTests
{
    [Fact]
    public void ServerRoutes_MatchTheOpenApiPaths()
    {
        using ExampleServer server = new();
        server.Client();

        HashSet<string> served = server.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.Trim('/'))
            .Where(p => !p.StartsWith("/link", StringComparison.Ordinal)) // link_url is chosen by the middle man
            .Select(Normalise)
            .ToHashSet();

        using JsonDocument openApi = JsonDocument.Parse(File.ReadAllText(OpenApiPath()));
        HashSet<string> documented = openApi.RootElement.GetProperty("paths").EnumerateObject()
            .Select(p => Normalise(p.Name))
            .ToHashSet();

        served.Should().BeEquivalentTo(documented);
    }

    // Parameter names differ in case between C# and the document; compare shapes only.
    private static string Normalise(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, "\\{[^}]+\\}", "{}");

    private static string OpenApiPath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the repository");
        return Path.Combine(dir!.FullName, "docs", "protocols", "custom-source-v1.openapi.json");
    }
}
