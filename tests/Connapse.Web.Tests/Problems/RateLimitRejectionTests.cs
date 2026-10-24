using System.Text.Json;
using Connapse.Web;
using Connapse.Web.Problems;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Web.Tests.Problems;

[Trait("Category", "Unit")]
public class RateLimitRejectionTests
{
    private static async Task<(string? ContentType, JsonElement Body)> RejectAsync(string path)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddConnapseProblemDetails().BuildServiceProvider(),
        };
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();

        await RateLimitingExtensions.WriteRejectionAsync(http, CancellationToken.None);

        http.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        http.Response.Body.Position = 0;
        return (http.Response.ContentType, await JsonSerializer.DeserializeAsync<JsonElement>(http.Response.Body));
    }

    [Fact]
    public async Task WriteRejectionAsync_ApiPath_WritesProblem()
    {
        var (type, body) = await RejectAsync("/api/v1/agents");

        type.Should().StartWith("application/problem+json");
        body.GetProperty("status").GetInt32().Should().Be(429);
        body.GetProperty("error").GetString().Should().Be("Rate limit exceeded. Try again later.");
    }

    [Theory]
    [InlineData("/oauth/token")]
    [InlineData("/mcp")]
    public async Task WriteRejectionAsync_ProtocolPath_KeepsItsJsonError(string path)
    {
        var (type, body) = await RejectAsync(path);

        type.Should().StartWith("application/json");
        body.GetProperty("error").GetString().Should().Be("Rate limit exceeded. Try again later.");
        body.TryGetProperty("status", out _).Should().BeFalse();
    }
}
