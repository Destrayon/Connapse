using Connapse.Core.Interfaces;
using Connapse.Ingestion.Validation;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Validation;

[Trait("Category", "Unit")]
public class FileTypeValidatorTests
{
    private readonly IFileTypeValidator _validator;

    public FileTypeValidatorTests()
    {
        var textParser = Substitute.For<IDocumentParser>();
        textParser.SupportedExtensions.Returns(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".csv" });

        var pdfParser = Substitute.For<IDocumentParser>();
        pdfParser.SupportedExtensions.Returns(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf" });

        _validator = new FileTypeValidator(new[] { textParser, pdfParser });
    }

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("notes.txt")]
    [InlineData("data.csv")]
    [InlineData("readme.md")]
    public void IsSupported_SupportedExtension_ReturnsTrue(string fileName)
    {
        _validator.IsSupported(fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("report.PDF")]
    [InlineData("NOTES.TXT")]
    [InlineData("data.Csv")]
    public void IsSupported_CaseInsensitive_ReturnsTrue(string fileName)
    {
        _validator.IsSupported(fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("script.bat")]
    [InlineData("archive.zip")]
    [InlineData("image.png")]
    public void IsSupported_UnsupportedExtension_ReturnsFalse(string fileName)
    {
        _validator.IsSupported(fileName).Should().BeFalse();
    }

    [Theory]
    [InlineData("README")]
    [InlineData("Makefile")]
    public void IsSupported_NoExtension_ReturnsFalse(string fileName)
    {
        _validator.IsSupported(fileName).Should().BeFalse();
    }

    [Theory]
    [InlineData("report.v2.pdf")]
    [InlineData("my.report.final.txt")]
    public void IsSupported_MultiDotSupported_ReturnsTrue(string fileName)
    {
        _validator.IsSupported(fileName).Should().BeTrue();
    }

    [Theory]
    [InlineData("report.pdf.exe")]
    [InlineData("test.txt.zip")]
    public void IsSupported_MultiDotUnsupported_ReturnsFalse(string fileName)
    {
        _validator.IsSupported(fileName).Should().BeFalse();
    }

    [Fact]
    public void SupportedExtensions_ContainsAllParserExtensions()
    {
        _validator.SupportedExtensions.Should().BeEquivalentTo(
            new[] { ".txt", ".md", ".csv", ".pdf" });
    }
}
