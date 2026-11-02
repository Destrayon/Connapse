using Connapse.Identity.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Connapse.Identity.Tests;

/// <summary>
/// CON-166: which handler a request's credentials go to. A JWT goes to JwtBearer, the token Identity's /login
/// issues goes to Identity.Bearer, and /mcp takes only JWTs.
/// </summary>
[Trait("Category", "Unit")]
public class MultiSchemeSelectionTests
{
    private static HttpRequest Request(string path, string? authorization = null, string? apiKey = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        if (apiKey is not null)
            context.Request.Headers[ApiKeyAuthenticationOptions.HeaderName] = apiKey;
        return context.Request;
    }

    // IdentityConstants holds these as static fields, which an attribute argument can't name.
    private const string IdentityBearer = "Identity.Bearer";
    private const string Cookie = "Identity.Application";

    [Fact]
    public void SchemeNames_MatchIdentityConstants()
    {
        IdentityBearer.Should().Be(IdentityConstants.BearerScheme);
        Cookie.Should().Be(IdentityConstants.ApplicationScheme);
    }

    [Theory]
    [InlineData("/api/containers", "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/api/containers", "bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/api/containers", "Bearer CfDJ8Abc-_def", IdentityBearer)]
    [InlineData("/api/containers", "BEARER CfDJ8Abc-_def", IdentityBearer)]
    [InlineData("/mcp", "Bearer CfDJ8Abc-_def", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/mcp/sse", "Bearer CfDJ8Abc-_def", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/mcpx", "Bearer CfDJ8Abc-_def", IdentityBearer)]
    [InlineData("/mcp", "Bearer a.b.c", JwtBearerDefaults.AuthenticationScheme)]
    [InlineData("/api/containers", "Basic dXNlcjpwYXNz", Cookie)]
    [InlineData("/api/containers", "Bearer", Cookie)]
    [InlineData("/api/containers", null, Cookie)]
    public void Authorization_GoesToItsScheme(string path, string? authorization, string expected)
    {
        IdentityServiceExtensions.SelectScheme(Request(path, authorization)).Should().Be(expected);
    }

    [Theory]
    [InlineData("cnp_key")]
    [InlineData("")]
    public void ApiKeyHeader_WinsOverABearerToken(string apiKey)
    {
        IdentityServiceExtensions.SelectScheme(Request("/api/containers", "Bearer CfDJ8Abc", apiKey))
            .Should().Be(ApiKeyAuthenticationOptions.SchemeName);
    }
}
