using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

public class PathUtilitiesTests
{
    // ── IsValidContainerName ──────────────────────────────────────────

    [Theory]
    [InlineData("my-container")]
    [InlineData("ab")]
    [InlineData("test123")]
    [InlineData("a1")]
    [InlineData("project-data-2024")]
    public void IsValidContainerName_ValidNames_ReturnsTrue(string name)
    {
        PathUtilities.IsValidContainerName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("a")]                       // Too short (1 char)
    [InlineData("")]                        // Empty
    [InlineData("   ")]                     // Whitespace
    [InlineData("-my-container")]           // Starts with hyphen
    [InlineData("my-container-")]           // Ends with hyphen
    [InlineData("My-Container")]            // Uppercase letters
    [InlineData("my_container")]            // Underscores
    [InlineData("my container")]            // Spaces
    [InlineData("my.container")]            // Dots
    [InlineData("my@container")]            // Special chars
    public void IsValidContainerName_InvalidNames_ReturnsFalse(string name)
    {
        PathUtilities.IsValidContainerName(name).Should().BeFalse();
    }

    [Fact]
    public void IsValidContainerName_NullName_ReturnsFalse()
    {
        PathUtilities.IsValidContainerName(null!).Should().BeFalse();
    }

    [Fact]
    public void IsValidContainerName_MaxLength_ReturnsTrue()
    {
        // 128 chars: starts and ends with alphanumeric
        var name = "a" + new string('-', 126) + "b";
        name.Length.Should().Be(128);
        PathUtilities.IsValidContainerName(name).Should().BeTrue();
    }

    [Fact]
    public void IsValidContainerName_TooLong_ReturnsFalse()
    {
        // 129 chars
        var name = "a" + new string('b', 128);
        name.Length.Should().Be(129);
        PathUtilities.IsValidContainerName(name).Should().BeFalse();
    }

    // ── NormalizePath ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/folder/file.txt", "/folder/file.txt")]
    [InlineData("folder/file.txt", "/folder/file.txt")]
    [InlineData("/folder/file.txt/", "/folder/file.txt")]
    [InlineData("folder\\file.txt", "/folder/file.txt")]
    [InlineData("/a/b/c", "/a/b/c")]
    [InlineData("/", "/")]
    [InlineData("/file.txt", "/file.txt")]
    public void NormalizePath_VariousInputs_ReturnsExpected(string input, string expected)
    {
        PathUtilities.NormalizePath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizePath_EmptyOrNull_ReturnsRoot(string? input)
    {
        PathUtilities.NormalizePath(input!).Should().Be("/");
    }

    // ── NormalizeFolderPath ───────────────────────────────────────────

    [Theory]
    [InlineData("/folder/", "/folder/")]
    [InlineData("/folder", "/folder/")]
    [InlineData("folder", "/folder/")]
    [InlineData("folder/", "/folder/")]
    [InlineData("/a/b/c", "/a/b/c/")]
    [InlineData("/a/b/c/", "/a/b/c/")]
    [InlineData("a\\b\\c", "/a/b/c/")]
    [InlineData("/", "/")]
    public void NormalizeFolderPath_VariousInputs_ReturnsExpected(string input, string expected)
    {
        PathUtilities.NormalizeFolderPath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeFolderPath_EmptyOrNull_ReturnsRoot(string? input)
    {
        PathUtilities.NormalizeFolderPath(input!).Should().Be("/");
    }

    // ── GetParentPath ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/a/b/c", "/a/b/")]
    [InlineData("/a/b", "/a/")]
    [InlineData("/a", "/")]
    [InlineData("/file.txt", "/")]
    [InlineData("/folder/subfolder/file.pdf", "/folder/subfolder/")]
    public void GetParentPath_VariousInputs_ReturnsExpected(string input, string expected)
    {
        PathUtilities.GetParentPath(input).Should().Be(expected);
    }

    [Fact]
    public void GetParentPath_RootPath_ReturnsRoot()
    {
        PathUtilities.GetParentPath("/").Should().Be("/");
    }

    // ── GenerateDuplicateName ─────────────────────────────────────────

    [Theory]
    [InlineData("file.pdf", 1, "file (1).pdf")]
    [InlineData("file.pdf", 2, "file (2).pdf")]
    [InlineData("document.txt", 3, "document (3).txt")]
    [InlineData("archive.tar.gz", 1, "archive.tar (1).gz")]
    public void GenerateDuplicateName_WithIndex_ReturnsExpected(string fileName, int index, string expected)
    {
        PathUtilities.GenerateDuplicateName(fileName, index).Should().Be(expected);
    }

    [Theory]
    [InlineData("file.pdf", 0)]
    [InlineData("file.pdf", -1)]
    public void GenerateDuplicateName_ZeroOrNegativeIndex_ReturnsOriginal(string fileName, int index)
    {
        PathUtilities.GenerateDuplicateName(fileName, index).Should().Be(fileName);
    }

    [Fact]
    public void GenerateDuplicateName_NoExtension_AppendsIndex()
    {
        PathUtilities.GenerateDuplicateName("README", 1).Should().Be("README (1)");
    }

    // ── GetFileName ───────────────────────────────────────────────────

    [Theory]
    [InlineData("/folder/file.txt", "file.txt")]
    [InlineData("/a/b/c/document.pdf", "document.pdf")]
    [InlineData("/file.txt", "file.txt")]
    [InlineData("file.txt", "file.txt")]
    [InlineData("/folder/subfolder/readme.md", "readme.md")]
    public void GetFileName_VariousInputs_ReturnsExpected(string input, string expected)
    {
        PathUtilities.GetFileName(input).Should().Be(expected);
    }
}
