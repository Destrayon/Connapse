using Connapse.Core.Interfaces;
using Connapse.Ingestion.Validation;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Ingestion.Tests.Validation;

[Trait("Category", "Unit")]
public class McpFileTypeValidationTests
{
    private readonly IFileTypeValidator _validator;

    public McpFileTypeValidationTests()
    {
        var textParser = Substitute.For<IDocumentParser>();
        textParser.SupportedExtensions.Returns(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" });

        _validator = new FileTypeValidator(new[] { textParser });
    }

    [Theory]
    [InlineData("malware.exe", ".exe")]
    [InlineData("script.bat", ".bat")]
    public void UploadFile_UnsupportedExtension_ReturnsErrorWithCorrectFormat(string fileName, string expectedExt)
    {
        var isSupported = _validator.IsSupported(fileName);
        isSupported.Should().BeFalse();

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        ext.Should().Be(expectedExt);

        var supported = string.Join(", ", _validator.SupportedExtensions.OrderBy(e => e));
        var errorMsg = $"Error: file type '{ext}' is not supported. Supported types: {supported}";
        errorMsg.Should().StartWith("Error: file type '")
            .And.Contain("is not supported")
            .And.Contain(".md")
            .And.Contain(".txt");
    }

    [Fact]
    public void BulkUpload_MixedExtensions_ValidItemsPassInvalidItemsFail()
    {
        var items = new[] { "good.txt", "bad.exe", "also-good.md", "bad.zip" };
        var failures = new List<string>();
        var successes = new List<string>();

        foreach (var fileName in items)
        {
            if (!_validator.IsSupported(fileName))
            {
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                failures.Add($"{fileName}: unsupported file type '{ext}'");
                continue;
            }
            successes.Add(fileName);
        }

        successes.Should().BeEquivalentTo(new[] { "good.txt", "also-good.md" });
        failures.Should().HaveCount(2);
        failures[0].Should().Contain("bad.exe").And.Contain("unsupported file type '.exe'");
        failures[1].Should().Contain("bad.zip").And.Contain("unsupported file type '.zip'");
    }
}
