using Connapse.Web.Services;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Web;

/// <summary>
/// The one-line scope description shown on the Sources tab.
/// <para>
/// It names <em>where</em> a source points — a bucket, container, or directory — and never what
/// is inside it. That line is the boundary this page is built around, so the tests below pin
/// both halves: the scope renders, and nothing resembling content does.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class SourceScopeSummaryTests
{
    [Fact]
    public void Describe_S3Scope_ShowsBucketAndPrefix()
    {
        SourceScopeSummary.Describe("""{"bucketName":"docs-bucket","prefix":"team/2026/"}""")
            .Should().Be("docs-bucket/team/2026");
    }

    [Fact]
    public void Describe_S3ScopeWithoutPrefix_ShowsJustTheBucket()
    {
        SourceScopeSummary.Describe("""{"bucketName":"docs-bucket"}""")
            .Should().Be("docs-bucket");
    }

    [Fact]
    public void Describe_AzureScope_ShowsContainerAndPrefix()
    {
        SourceScopeSummary.Describe("""{"containerName":"blobs","prefix":"reports/"}""")
            .Should().Be("blobs/reports");
    }

    [Fact]
    public void Describe_FilesystemScope_ShowsTheSubPath()
    {
        SourceScopeSummary.Describe("""{"subPath":"team/docs"}""")
            .Should().Be("team/docs");
    }

    [Fact]
    public void Describe_IncludesPatterns_BecauseTheyChangeWhatIsPickedUp()
    {
        // Two sources on the same directory with different patterns are different sources; the
        // operator needs to be able to tell them apart.
        SourceScopeSummary.Describe("""{"subPath":"team","includePatterns":["*.md","*.txt"]}""")
            .Should().Be("team (*.md, *.txt)");
    }

    [Fact]
    public void Describe_ShowsExclusionsSeparately()
    {
        SourceScopeSummary.Describe("""{"subPath":"team","excludePatterns":["*.tmp"]}""")
            .Should().Be("team (excluding *.tmp)");
    }

    // ── Never throws, never guesses ───────────────────────────────────

    [Fact]
    public void Describe_MalformedJson_ReturnsNull()
    {
        // A display helper that throws takes the whole page down over cosmetic detail.
        SourceScopeSummary.Describe("{not valid json").Should().BeNull();
    }

    [Fact]
    public void Describe_NonStringPatternEntries_AreSkipped()
    {
        SourceScopeSummary.Describe("""{"subPath":"team","includePatterns":["*.md",42,null]}""")
            .Should().Be("team (*.md)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void Describe_NothingMeaningful_ReturnsNull(string? scopeJson)
    {
        SourceScopeSummary.Describe(scopeJson).Should().BeNull();
    }

    [Fact]
    public void Describe_JsonArrayRatherThanObject_ReturnsNull()
    {
        SourceScopeSummary.Describe("""["not","an","object"]""").Should().BeNull();
    }

    // ── The boundary ──────────────────────────────────────────────────

    [Fact]
    public void Describe_NeverSurfacesDocumentDetail()
    {
        // A scope blob could carry anything. Only the keys that name the scope itself are read,
        // so a stray document list in stored config cannot leak onto the page — the Sources tab
        // showing file names is precisely the leak this epic exists to close.
        string scope = """
            {"bucketName":"docs","prefix":"team/","documents":["salary-review.pdf"],"lastFile":"secret.txt"}
            """;

        var described = SourceScopeSummary.Describe(scope);

        described.Should().Be("docs/team");
        described.Should().NotContain("salary-review");
        described.Should().NotContain("secret.txt");
    }
}
