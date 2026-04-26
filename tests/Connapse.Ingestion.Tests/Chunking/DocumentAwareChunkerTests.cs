using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class DocumentAwareChunkerTests
{
    private readonly DocumentAwareChunker _chunker;
    private readonly RecursiveChunker _recursive;

    public DocumentAwareChunkerTests()
    {
        TiktokenTokenCounter counter = new();
        _recursive = new RecursiveChunker(counter);
        _chunker = new DocumentAwareChunker(counter, _recursive);
    }

    [Fact]
    public void Name_IsDocumentAware()
    {
        _chunker.Name.Should().Be("DocumentAware");
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsEmpty()
    {
        var doc = new ParsedDocument("", new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_PlainProseNoMarkdown_FallsThroughToRecursive()
    {
        string content = "Just a paragraph of prose. No headers. No code fences. " +
                         string.Join(" ", Enumerable.Repeat("filler", 20));
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().NotBeEmpty();
        // Fallback path means HeaderPath metadata is not stamped.
        result.Should().AllSatisfy(c => c.Metadata.Should().NotContainKey("HeaderPath"));
    }

    [Fact]
    public async Task ChunkAsync_HeadersStamped_OnEveryChunk()
    {
        string content = "# Engineering\n\n## Deploy\n\nDeploy steps here.\n\n## Rollback\n\nRollback steps here.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 200,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = false  // keep raw bodies for offset round-trip below
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThanOrEqualTo(2);
        result.Where(c =>
            c.Metadata.TryGetValue("HeaderPath", out string? p) &&
            p == "Engineering > Deploy").Should().NotBeEmpty();
        result.Where(c =>
            c.Metadata.TryGetValue("HeaderPath", out string? p) &&
            p == "Engineering > Rollback").Should().NotBeEmpty();
        result.Should().AllSatisfy(c => c.Metadata.Should().ContainKey("H1"));
    }

    [Fact]
    public async Task ChunkAsync_PrependHeaderPath_AddsBreadcrumbToContent()
    {
        string content = "# Engineering\n\n## Deploy\n\nDeploy steps here.";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 200,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = true
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Where(c =>
            c.Content.StartsWith("Engineering > Deploy") &&
            c.Metadata.TryGetValue("OffsetEstimated", out string? est) && est == "true")
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_OversizeSection_DelegatesToRecursive()
    {
        // Section is far above MaxChunkSize → goes through recursive sub-splitting.
        string longBody = string.Join(" ", Enumerable.Repeat("filler", 500));
        string content = $"# Big\n\n{longBody}";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 50,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = false  // exercise raw-body path; prepend asserted in dedicated test
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThan(1);
        result.Should().AllSatisfy(c =>
        {
            c.Metadata.Should().ContainKey("HeaderPath");
            c.Metadata["HeaderPath"].Should().Be("Big");
            c.TokenCount.Should().BeLessThanOrEqualTo(settings.MaxChunkSize);
        });
    }

    [Fact]
    public async Task ChunkAsync_OversizeSection_PrependsHeaderPathToSubChunks()
    {
        // Regression: previously only short sections received the breadcrumb when
        // PrependHeaderPath = true; oversize sections delegated to the recursive
        // chunker emitted bare body slices. Now consistent across both paths.
        string longBody = string.Join(" ", Enumerable.Repeat("filler", 500));
        string content = $"# Engineering\n\n## Deploy\n\n{longBody}";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 50,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = true
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().HaveCountGreaterThan(1);
        result.Should().AllSatisfy(c =>
        {
            c.Content.Should().StartWith("Engineering > Deploy");
            c.Metadata.Should().ContainKey("OffsetEstimated");
            c.Metadata["OffsetEstimated"].Should().Be("true");
        });
    }

    [Fact]
    public async Task ChunkAsync_FencedCodeBlock_NeverSplitMidFence()
    {
        // A code fence inside a section MUST stay atomic even if the recursive
        // fallback fires for the section. Markdig's AST treats the fence as one
        // block; the section as a whole is sliced from source, preserving the fence.
        string content = "# Cmd\n\n```\nlong long long long long long long long long\n" +
                         "and more more more more more more more more more more more\n" +
                         "and yet more yet more yet more yet more yet more yet more\n```";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 1000, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        // The fence (open + body + close) must live entirely inside ONE chunk.
        // Concatenating chunk contents and checking for "```" anywhere would also
        // pass if the chunker split the fence across two chunks — exactly the
        // regression this test is meant to catch.
        result.Should().Contain(c =>
            c.Content.Contains("```\nlong long long") &&
            c.Content.TrimEnd().EndsWith("```"));
    }

    [Fact]
    public async Task ChunkAsync_OffsetsRoundTrip_WhenPrependHeaderPathFalse()
    {
        string content = "# A\n\nbody a\n\n# B\n\nbody b";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings
        {
            MaxChunkSize = 1000,
            MinChunkSize = 1,
            Overlap = 0,
            PrependHeaderPath = false
        };

        var result = await _chunker.ChunkAsync(doc, settings);

        foreach (ChunkInfo c in result)
        {
            c.StartOffset.Should().BeGreaterThanOrEqualTo(0);
            c.EndOffset.Should().BeLessThanOrEqualTo(content.Length);
            string slice = content.Substring(c.StartOffset, c.EndOffset - c.StartOffset);
            slice.Trim().Should().Be(c.Content.Trim());
        }
    }

    [Fact]
    public async Task ChunkAsync_HasChunkingStrategyMetadata()
    {
        string content = "# A\n\nbody";
        var doc = new ParsedDocument(content, new Dictionary<string, string>(), new List<string>());
        var settings = new ChunkingSettings { MaxChunkSize = 100, MinChunkSize = 1, Overlap = 0 };

        var result = await _chunker.ChunkAsync(doc, settings);

        result.Should().AllSatisfy(c =>
        {
            c.Metadata["ChunkingStrategy"].Should().Be("DocumentAware");
        });
    }
}
