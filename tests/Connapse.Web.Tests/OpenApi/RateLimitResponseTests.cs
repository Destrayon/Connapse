using System.Text.Json;
using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// Every route under /api is rate limited, and the limiter answers 429 before the handler runs. The integration
/// tests turn the limits off, so spec conformance never sees one; the spec still has to declare it.
/// </summary>
[Trait("Category", "Unit")]
public class RateLimitResponseTests
{
    [Fact]
    public void ApiOperations_Declare429AsAProblem()
    {
        string path = Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json");
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllText(path));

        var missing = new List<string>();
        foreach (JsonProperty route in spec.RootElement.GetProperty("paths").EnumerateObject().Where(p => p.Name.StartsWith("/api/", StringComparison.Ordinal)))
        {
            foreach (JsonProperty operation in route.Value.EnumerateObject())
            {
                bool declared = operation.Value.GetProperty("responses").TryGetProperty("429", out JsonElement limited)
                    && limited.TryGetProperty("content", out JsonElement content)
                    && content.TryGetProperty("application/problem+json", out _);
                if (!declared)
                    missing.Add($"{operation.Name.ToUpperInvariant()} {route.Name}");
            }
        }

        missing.Should().BeEmpty("the rate limiter can answer any /api request with a 429 problem");
    }
}
