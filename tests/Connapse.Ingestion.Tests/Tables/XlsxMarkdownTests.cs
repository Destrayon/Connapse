using Connapse.Ingestion.Parsers;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Tables;

[Trait("Category", "Unit")]
public class XlsxMarkdownTests
{
    [Theory]
    [InlineData("yyyy-mm-dd", true)]
    [InlineData("h:mm AM/PM", true)]
    [InlineData("0.00", false)]
    [InlineData("\"days\" 0", false)]
    [InlineData("[Red]0.00", false)]
    [InlineData("#,##0\\d", false)]
    public void IsDateCode_ReadsOnlyUnquotedDateLetters(string code, bool expected)
    {
        XlsxMarkdown.IsDateCode(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("ab3", 27)]
    [InlineData("12", -1)]
    [InlineData("ABCD1", -1)]
    public void ColumnIndex_ReadsTheLetters(string reference, int expected)
    {
        XlsxMarkdown.ColumnIndex(reference).Should().Be(expected);
    }
}
