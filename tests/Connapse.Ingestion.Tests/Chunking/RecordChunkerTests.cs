using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class RecordChunkerTests
{
    private const string Header = "# 12: Crash on start\nType: Issue · Author: octo · State: open · Created: 2026-01-01";

    private readonly RecordChunker _chunker = new(
        new TiktokenTokenCounter(), new RecursiveChunker(new TiktokenTokenCounter()));

    private static ParsedDocument Doc(string content) =>
        new(content, new Dictionary<string, string> { ["FileType"] = "Markdown" }, []);

    private static string Words(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));

    private static string Record(string body, params string[] comments)
    {
        var sb = new StringBuilder(Header).Append("\n\n").Append(body).Append('\n');
        for (int i = 0; i < comments.Length; i++)
            sb.Append($"\n--- Comment by user{i} (2026-01-0{i + 2}) ---\n").Append(comments[i]).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Name_ReturnsRecord()
    {
        _chunker.Name.Should().Be("Record");
        _chunker.Name.Should().Be(nameof(ChunkingStrategy.Record));
    }

    [Fact]
    public async Task ChunkAsync_EmptyContent_ReturnsNothing()
    {
        (await _chunker.ChunkAsync(Doc("  "), new ChunkingSettings())).Should().BeEmpty();
    }

    [Fact]
    public async Task ChunkAsync_RecordWithinBudget_IsOneChunkHoldingTheWholeRecord()
    {
        string content = Record("It crashes.", "Me too", "Fixed in #13");

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 512 });

        result.Should().ContainSingle();
        result[0].Content.Should().Be(content.Trim());
        result[0].Metadata["ChunkingStrategy"].Should().Be("Record");
        result[0].Metadata["FileType"].Should().Be("Markdown");
        result[0].Metadata.Should().NotContainKey("OffsetEstimated");
    }

    [Fact]
    public async Task ChunkAsync_OversizedRecord_SplitsAtCommentsWithTheHeaderOnEveryChunk()
    {
        string content = Record(Words("body", 40), Words("alpha", 40), Words("beta", 40), Words("gamma", 40));

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 100, Overlap = 0 });

        result.Should().HaveCount(4);
        result.Should().OnlyContain(c => c.Content.StartsWith(Header + "\n\n"));
        result.Should().OnlyContain(c => c.TokenCount <= 100);
        result[1].Content.Should().Contain("--- Comment by user0 (2026-01-02) ---").And.Contain("alpha");
        result[3].Content.Should().Contain("gamma").And.NotContain("beta");
        result.Select(c => c.ChunkIndex).Should().Equal(0, 1, 2, 3);
        result.Should().OnlyContain(c => c.Metadata["ChunkingStrategy"] == "Record");
    }

    [Fact]
    public async Task ChunkAsync_SmallAdjacentComments_ArePackedTogether()
    {
        string content = Record(Words("body", 50), "short one", "short two", "short three");

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 100, Overlap = 0 });

        result.Should().HaveCount(2, "the body fills one chunk and the three short comments share the next");
        result[1].Content.Should().Contain("short one").And.Contain("short three");
    }

    [Fact]
    public async Task ChunkAsync_OneCommentOverBudget_IsSplitFurtherAndStillCarriesTheHeader()
    {
        string content = Record("short body", Words("huge", 400));

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 100, Overlap = 0, MinChunkSize = 10 });

        result.Count.Should().BeGreaterThan(3);
        result.Should().OnlyContain(c => c.Content.StartsWith(Header));
        result.Skip(1).Should().OnlyContain(c => c.Metadata["OffsetEstimated"] == "true");
    }

    [Fact]
    public async Task ChunkAsync_OffsetsPointAtTheSpanEachChunkCovers()
    {
        string content = Record(Words("body", 40), Words("alpha", 40));

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 100, Overlap = 0 });

        result.Should().HaveCount(2);
        content[result[1].StartOffset..result[1].EndOffset].Should().StartWith("--- Comment by user0");
        result[1].EndOffset.Should().Be(content.Length);
    }

    [Fact]
    public async Task ChunkAsync_HeaderLongerThanHalfTheBudget_IsShortenedSoNoChunkExceedsTheLimit()
    {
        string references = "References: " + string.Join(", ", Enumerable.Range(100, 300).Select(n => "#" + n));
        string content = Header + "\n" + references + "\n\n" + Words("body", 60)
            + "\n\n--- Comment by user0 (2026-01-02) ---\n" + Words("alpha", 60) + "\n";

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 100, Overlap = 0, MinChunkSize = 10 });

        result.Should().NotBeEmpty();
        result.Should().OnlyContain(c => c.TokenCount <= 100);
        result.Should().OnlyContain(c => c.Content.StartsWith("# 12: Crash on start"),
            "the title line survives; the edge list is what gives way");
    }

    [Fact]
    public async Task ChunkAsync_EscapedDelimiterInUserText_IsNotABoundary()
    {
        // What the renderer writes when a comment body contains a delimiter-shaped line.
        string forged = Words("alpha", 12) + "\n\\--- Comment by mallory (2026-01-09) ---\n" + Words("beta", 12);
        string content = Record(Words("body", 60), forged);

        var result = await _chunker.ChunkAsync(Doc(content), new ChunkingSettings { MaxChunkSize = 100, Overlap = 0 });

        result.Should().HaveCount(2, "the body is one chunk and the single real comment the other");
        result[1].Content.Should().Contain("alpha").And.Contain("beta");
    }
}
