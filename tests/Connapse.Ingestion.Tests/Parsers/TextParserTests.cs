using System.Text;
using AIKnowledge.Ingestion.Parsers;
using FluentAssertions;

namespace AIKnowledge.Ingestion.Tests.Parsers;

public class TextParserTests
{
    private readonly TextParser _parser = new();

    [Fact]
    public void SupportedExtensions_ContainsExpectedTypes()
    {
        _parser.SupportedExtensions.Should().Contain(".txt");
        _parser.SupportedExtensions.Should().Contain(".md");
        _parser.SupportedExtensions.Should().Contain(".csv");
        _parser.SupportedExtensions.Should().Contain(".json");
        _parser.SupportedExtensions.Should().Contain(".xml");
        _parser.SupportedExtensions.Should().Contain(".yaml");
        _parser.SupportedExtensions.Should().Contain(".yml");
    }

    [Fact]
    public async Task ParseAsync_PlainTextFile_ReturnsContent()
    {
        var content = "Hello, World!\nThis is a test file.";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "test.txt");

        result.Content.Should().Be(content);
        result.Metadata["FileType"].Should().Be("PlainText");
        result.Metadata["LineCount"].Should().Be("2");
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task ParseAsync_MarkdownFile_DetectsHeaders()
    {
        var content = "# Header 1\n\nSome content\n\n## Header 2\n\nMore content";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "readme.md");

        result.Content.Should().Be(content);
        result.Metadata["FileType"].Should().Be("Markdown");
        result.Metadata["HasMarkdownHeaders"].Should().Be("True");
    }

    [Fact]
    public async Task ParseAsync_MarkdownFileWithoutHeaders_DetectsNoHeaders()
    {
        var content = "Just plain text\nwithout any headers";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "notes.md");

        result.Metadata["HasMarkdownHeaders"].Should().Be("False");
    }

    [Fact]
    public async Task ParseAsync_CsvFile_DetectsDelimiter()
    {
        var content = "name,age,city\nJohn,30,New York\nJane,25,London";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "data.csv");

        result.Content.Should().Be(content);
        result.Metadata["FileType"].Should().Be("CSV");
        result.Metadata["CsvDelimiter"].Should().Be(",");
    }

    [Fact]
    public async Task ParseAsync_TabDelimitedCsv_DetectsTabDelimiter()
    {
        var content = "name\tage\tcity\nJohn\t30\tNew York";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "data.csv");

        result.Metadata["CsvDelimiter"].Should().Be("\\t");
    }

    [Fact]
    public async Task ParseAsync_SemicolonDelimitedCsv_DetectsSemicolonDelimiter()
    {
        var content = "name;age;city\nJohn;30;New York";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "data.csv");

        result.Metadata["CsvDelimiter"].Should().Be(";");
    }

    [Fact]
    public async Task ParseAsync_JsonFile_ReturnsJsonFileType()
    {
        var content = "{\"name\": \"test\", \"value\": 123}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "config.json");

        result.Content.Should().Be(content);
        result.Metadata["FileType"].Should().Be("JSON");
    }

    [Fact]
    public async Task ParseAsync_YamlFile_ReturnsYamlFileType()
    {
        var content = "name: test\nvalue: 123";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "config.yaml");

        result.Metadata["FileType"].Should().Be("YAML");
    }

    [Fact]
    public async Task ParseAsync_EmptyFile_ReturnsWarning()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());

        var result = await _parser.ParseAsync(stream, "empty.txt");

        result.Content.Should().BeEmpty();
        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("no readable text content");
    }

    [Fact]
    public async Task ParseAsync_WhitespaceOnlyFile_ReturnsWarning()
    {
        var content = "   \n\t\n   ";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "whitespace.txt");

        result.Content.Should().BeEmpty();
        result.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task ParseAsync_LargeFile_ProcessesSuccessfully()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => $"Line {i}: Some content here");
        var content = string.Join("\n", lines);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "large.txt");

        result.Content.Should().Be(content);
        result.Metadata["LineCount"].Should().Be("1000");
    }

    [Fact]
    public async Task ParseAsync_UnicodeContent_PreservesEncoding()
    {
        var content = "Hello, 世界!\nПривет мир!\n🎉🎊";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await _parser.ParseAsync(stream, "unicode.txt");

        result.Content.Should().Be(content);
    }

    [Fact]
    public async Task ParseAsync_SupportsCancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var content = "Some content";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var act = async () => await _parser.ParseAsync(stream, "test.txt", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
