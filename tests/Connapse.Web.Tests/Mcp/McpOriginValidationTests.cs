using Connapse.Web.Mcp;
using FluentAssertions;

namespace Connapse.Web.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpOriginValidationTests
{
    private static readonly IReadOnlySet<string> Configured = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https://app.example.com" };

    [Theory]
    [InlineData("http://localhost:5099")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://[::1]:3000")]
    [InlineData("https://app.example.com")]
    [InlineData("https://APP.example.com/")]
    public void IsAllowed_LoopbackOrConfiguredOrigin_IsAllowed(string origin) =>
        McpOriginValidation.IsAllowed(origin, Configured).Should().BeTrue();

    [Theory]
    [InlineData("http://evil.example.com")]
    [InlineData("https://app.example.com.evil.net")]
    [InlineData("http://localhost.evil.net")]
    [InlineData("null")]
    [InlineData("file://localhost")]
    public void IsAllowed_AnyOtherOrigin_IsRefused(string origin) =>
        McpOriginValidation.IsAllowed(origin, Configured).Should().BeFalse();
}
