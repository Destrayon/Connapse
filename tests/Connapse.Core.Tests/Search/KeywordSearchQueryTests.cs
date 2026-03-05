using Connapse.Search.Keyword;
using FluentAssertions;

namespace Connapse.Core.Tests.Search;

/// <summary>
/// Tests for KeywordSearchService query building logic.
/// Validates that multi-term queries use OR joining and terms are sanitized correctly.
/// </summary>
public class KeywordSearchQueryTests
{
    [Fact]
    public void BuildOrQuery_SingleTerm_ReturnsTerm()
    {
        var result = KeywordSearchService.BuildOrQuery("Jellyfin");

        result.Should().Be("Jellyfin");
    }

    [Fact]
    public void BuildOrQuery_MultipleTerms_JoinsWithOr()
    {
        var result = KeywordSearchService.BuildOrQuery("Jellyfin Bitwarden");

        result.Should().Be("Jellyfin | Bitwarden");
    }

    [Fact]
    public void BuildOrQuery_ThreeTerms_JoinsAllWithOr()
    {
        var result = KeywordSearchService.BuildOrQuery("Jellyfin Bitwarden NET");

        result.Should().Be("Jellyfin | Bitwarden | NET");
    }

    [Fact]
    public void BuildOrQuery_DotNetTerm_StripsLeadingDot()
    {
        // ".NET" should become "NET" (leading dot stripped)
        var result = KeywordSearchService.BuildOrQuery(".NET framework");

        result.Should().Be("NET | framework");
    }

    [Fact]
    public void BuildOrQuery_EmptyQuery_ReturnsEmpty()
    {
        var result = KeywordSearchService.BuildOrQuery("   ");

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildOrQuery_SpecialCharactersOnly_ReturnsEmpty()
    {
        var result = KeywordSearchService.BuildOrQuery("!@#$%^&*()");

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildOrQuery_DuplicateTerms_Deduplicates()
    {
        var result = KeywordSearchService.BuildOrQuery("test Test TEST");

        // Should deduplicate case-insensitively
        result.Should().Be("test");
    }

    [Fact]
    public void BuildOrQuery_ExtraWhitespace_HandledCorrectly()
    {
        var result = KeywordSearchService.BuildOrQuery("  hello   world  ");

        result.Should().Be("hello | world");
    }

    [Fact]
    public void BuildOrQuery_HyphenatedTerm_PreservesHyphen()
    {
        var result = KeywordSearchService.BuildOrQuery("real-time updates");

        result.Should().Be("real-time | updates");
    }

    [Fact]
    public void BuildOrQuery_InternalDots_Preserved()
    {
        // "node.js" has internal dot which may form a compound lexeme
        var result = KeywordSearchService.BuildOrQuery("node.js setup");

        result.Should().Be("node.js | setup");
    }

    [Fact]
    public void SanitizeTerm_LeadingTrailingDots_Stripped()
    {
        var result = KeywordSearchService.SanitizeTerm("..NET..");

        result.Should().Be("NET");
    }

    [Fact]
    public void SanitizeTerm_SpecialCharsRemoved()
    {
        var result = KeywordSearchService.SanitizeTerm("C#");

        result.Should().Be("C");
    }

    [Fact]
    public void SanitizeTerm_SingleQuotesRemoved()
    {
        var result = KeywordSearchService.SanitizeTerm("it's");

        result.Should().Be("its");
    }

    [Fact]
    public void SanitizeTerm_EmptyInput_ReturnsEmpty()
    {
        var result = KeywordSearchService.SanitizeTerm("!!!");

        result.Should().BeEmpty();
    }

    [Fact]
    public void SanitizeTerm_Underscore_Preserved()
    {
        var result = KeywordSearchService.SanitizeTerm("my_variable");

        result.Should().Be("my_variable");
    }
}
