using Connapse.Web.Endpoints;
using FluentAssertions;

namespace Connapse.Web.Tests.Endpoints;

/// <summary>
/// GET /api/containers/{containerId}/files/{fileId}/content answers JSON or plain text. The spec declares
/// both, so a generated client that sends both types must get the one it prefers.
/// </summary>
[Trait("Category", "Unit")]
public class FileContentNegotiationTests
{
    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("text/*")]
    [InlineData("text/plain, */*")]
    [InlineData("application/json;q=0.5, text/plain")]
    public void PrefersPlainText_PlainTextPreferred_IsTrue(string accept) =>
        DocumentsEndpoints.PrefersPlainText(accept).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*/*")]
    [InlineData("application/json")]
    [InlineData("application/json, text/plain")]
    [InlineData("application/json, text/plain, */*")]
    [InlineData("text/plain;q=0.5, application/json")]
    [InlineData("text/plain;q=0, application/json")]
    [InlineData("text/plain;q=0")]
    public void PrefersPlainText_JsonPreferredOrEqual_IsFalse(string? accept) =>
        DocumentsEndpoints.PrefersPlainText(accept).Should().BeFalse();
}
