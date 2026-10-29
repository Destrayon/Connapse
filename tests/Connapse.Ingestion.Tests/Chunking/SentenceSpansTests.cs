using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Utilities;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Chunking;

[Trait("Category", "Unit")]
public class SentenceSpansTests
{
    private static string[] Slices(string content, IReadOnlyList<(int Start, int End)> spans) =>
        spans.Select(s => content[s.Start..s.End]).ToArray();

    [Fact]
    public void Locate_EmptyContent_ReturnsNoSpans()
    {
        SentenceSpans.Locate("", ["anything"]).Should().BeEmpty();
    }

    [Fact]
    public void Locate_NoSentences_ReturnsWholeContent()
    {
        SentenceSpans.Locate("abc def", []).Should().Equal((0, 7));
    }

    [Fact]
    public void Locate_ProseSentences_TileTheContentWithTrailingWhitespace()
    {
        string content = "  One two. Three four.\nFive six.";

        IReadOnlyList<(int Start, int End)> spans =
            SentenceSpans.Locate(content, ["One two.", "Three four.", "Five six."]);

        Slices(content, spans).Should().Equal("  One two. ", "Three four.\n", "Five six.");
    }

    [Fact]
    public void Locate_SplitWithNoWhitespaceAfterIt_JoinsTheSentences()
    {
        string content = "var x = searchSettings.CurrentValue;\nreturn x;";

        IReadOnlyList<(int Start, int End)> spans =
            SentenceSpans.Locate(content, ["var x = searchSettings.", "CurrentValue;", "return x;"]);

        Slices(content, spans).Should().Equal("var x = searchSettings.CurrentValue;\n", "return x;");
    }

    [Fact]
    public void Locate_TextTheSegmenterDeleted_StaysInTheSpans()
    {
        string content = "List<Group> groups = a;\n/// <summary>Reads it.</summary>\nNext();";

        IReadOnlyList<(int Start, int End)> spans =
            SentenceSpans.Locate(content, ["List groups = a;", "Reads it.", "Next();"]);

        // "Reads it." is followed by the deleted "</summary>", not whitespace, so it joins with "Next();".
        Slices(content, spans).Should().Equal("List<Group> groups = a;\n", "/// <summary>Reads it.</summary>\nNext();");
    }

    [Fact]
    public void Locate_CharacterTheSegmenterAdded_IsSkipped()
    {
        string content = "Alpha beta. Gamma delta. Omega.";

        IReadOnlyList<(int Start, int End)> spans =
            SentenceSpans.Locate(content, ["Alpha ~beta.", "Gamma delta.", "Omega."]);

        Slices(content, spans).Should().Equal("Alpha beta. ", "Gamma delta. ", "Omega.");
    }

    [Fact]
    public void Locate_SentenceWithNothingInTheSource_AddsNoBoundary()
    {
        string content = "Alpha beta. Gamma delta.";

        IReadOnlyList<(int Start, int End)> spans =
            SentenceSpans.Locate(content, ["Alpha beta.", "~~~", "Gamma delta."]);

        Slices(content, spans).Should().Equal("Alpha beta. ", "Gamma delta.");
    }

    [Fact]
    public void Locate_TrailingWhitespaceAfterLastSentence_StaysInTheLastSpan()
    {
        string content = "One. Two.  \n";

        IReadOnlyList<(int Start, int End)> spans = SentenceSpans.Locate(content, ["One.", "Two."]);

        Slices(content, spans).Should().Equal("One. ", "Two.  \n");
    }

    [Fact]
    public void Locate_CharacterBeyondTheSearchWindow_IsNotMatched()
    {
        string gap = new('x', SentenceSpans.SearchWindow);
        string content = "Start. " + gap + "Q end. Tail.";

        IReadOnlyList<(int Start, int End)> spans =
            SentenceSpans.Locate(content, ["Start.", "Q end.", "Tail."]);

        // "Q end." and "Tail." are too far past "Start." to be matched, so no boundary is placed for them.
        Slices(content, spans).Should().Equal("Start. ", gap + "Q end. Tail.");
    }

    [Fact]
    public void Locate_CodeThroughTheRealSegmenter_KeepsEveryLineInOneSpan()
    {
        string content = """
            namespace Connapse.Application;

            /// <summary>Reads an agent. Administrators only.</summary>
            public static List<Group> groups = searchSettings.CurrentValue;
                return Attempt<IReadOnlyList<AgentDto>, AgentStatus>.Fail(x);
            """;
        IReadOnlyList<string> sentences = new PragmaticSentenceSegmenter().Split(content);

        string[] slices = Slices(content, SentenceSpans.Locate(content, sentences));

        string.Concat(slices).Should().Be(content);
        foreach (string line in content.Split('\n').Select(l => l.Trim()).Where(l => !l.Contains("Reads an agent")))
            slices.Should().Contain(s => s.Contains(line));
    }

    [Fact]
    public void Locate_SegmenterJoinedTwoLinesWithASpace_StillFindsTheBoundary()
    {
        string content = "One\ntwo. Three four.";

        IReadOnlyList<(int Start, int End)> spans = SentenceSpans.Locate(content, ["One two.", "Three four."]);

        Slices(content, spans).Should().Equal("One\ntwo. ", "Three four.");
    }

    [Fact]
    public void Locate_OneCharacterSentenceAtTheStart_IsMatched()
    {
        SentenceSpans.Locate("A B", ["A", "B"]).Should().Equal((0, 2), (2, 3));
    }

    [Fact]
    public void Locate_UnmatchedSentenceBeforeLeadingWhitespace_AddsNoBoundary()
    {
        SentenceSpans.Locate(" I am.", ["~", "I am."]).Should().Equal((0, 6));
    }

    [Theory]
    [InlineData("  ab c \n", 0, 8, 2, 6)]
    [InlineData("ab", 0, 2, 0, 2)]
    [InlineData("x   y", 1, 4, 1, 1)]
    public void Trim_RemovesSurroundingWhitespace(string content, int start, int end, int trimmedStart, int trimmedEnd)
    {
        SentenceSpans.Trim(content, (start, end)).Should().Be((trimmedStart, trimmedEnd));
    }
}
