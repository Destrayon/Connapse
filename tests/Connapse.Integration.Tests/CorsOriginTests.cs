using System.Net;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// What the default CORS policy does with the Origin header a request actually carries.
/// </summary>
/// <remarks>
/// The policy is a predicate in Program.cs, so it is only reachable through a real request. It used
/// to build a <see cref="Uri"/> from the header directly, which throws on a value that is not a URL
/// — and CORS runs ahead of the exception handler, so the throw surfaced as a 500 on any request
/// carrying one, from anyone who cared to send it. Clicking Connect on the integrations page was
/// how it was found.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CorsOriginTests(SharedWebAppFixture fixture)
{
    /// <summary>Requires authentication, so a working request answers 401 rather than 200.</summary>
    private const string Endpoint = "/api/v1/auth/cloud/aws/connect";

    [Theory]
    // The literal string, which is what a browser sends for an opaque origin — a cross-site POST
    // that has been through a redirect, which is the shape of a SAML sign-in coming back.
    [InlineData("null")]
    [InlineData("")]
    [InlineData("not a url")]
    // A scheme with no authority: parses as a relative reference, so Host is unavailable.
    [InlineData("/somewhere")]
    public async Task AnOriginThatIsNotAUrl_IsRefused_NotThrown(string origin)
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.TryAddWithoutValidation("Origin", origin);

        var response = await client.SendAsync(request);

        // Whatever the endpoint answers, it must be the endpoint answering. The header decides
        // whether CORS headers come back, and must never decide whether the request survives.
        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        response.Headers.Should().NotContain(h => h.Key == "Access-Control-Allow-Origin",
            "an origin that cannot be parsed cannot be matched, so it is not allowed");
    }

    [Fact]
    public async Task AWellFormedOrigin_IsStillAnswered()
    {
        using var client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5001");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
