using System.Text;
using Connapse.Ingestion.Parsers;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Parsers;

[Trait("Category", "Unit")]
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

    // The same sentences extract-v1's generated-encodings dataset uses (#594).
    private const string Sentence = "Encoding check: café, naïve, Größe and 日本語 must survive.";
    private const string LatinSentence = "Encoding check: café, naïve, Größe must survive.";

    public static TheoryData<string, byte[], string, string> Encodings() => new()
    {
        { "utf8", Encoding.UTF8.GetBytes(Sentence), Sentence, "utf-8" },
        { "utf8-bom", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Sentence)], Sentence, "utf-8" },
        { "utf16le-bom", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Sentence)], Sentence, "utf-16" },
        { "utf16le", Encoding.Unicode.GetBytes(Sentence), Sentence, "utf-16" },
        { "utf16be", Encoding.BigEndianUnicode.GetBytes(Sentence), Sentence, "utf-16BE" },
        { "utf32le-bom", [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes(Sentence)], Sentence, "utf-32" },
        { "latin1", Encoding.Latin1.GetBytes(LatinSentence), LatinSentence, "" },
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task ParseAsync_Encoding_DecodesTheTextExactly(string label, byte[] bytes, string expected, string webName)
    {
        using var stream = new MemoryStream(bytes);

        var result = await _parser.ParseAsync(stream, "encoded.txt");

        result.Content.Should().Be(expected, $"the {label} file must decode without garbling");
        result.Content.Should().NotContain("�");
        if (webName.Length > 0)
            result.Metadata["Encoding"].Should().Be(webName);
    }

    [Fact]
    public async Task ParseAsync_Windows1252SmartQuotes_FallBackToWindows1252()
    {
        // 0x93/0x94 are curly quotes in Windows-1252 and control characters in Latin-1.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "He said “fin” and left — café closed.";
        using var stream = new MemoryStream(Encoding.GetEncoding(1252).GetBytes(text));

        var result = await _parser.ParseAsync(stream, "quotes.txt");

        result.Content.Should().Be(text);
    }

    [Fact]
    public async Task ParseAsync_CyrillicWindows1251_IsDetected()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "Съешь же ещё этих мягких французских булок, да выпей чаю. "
            + "Широкая электрификация южных губерний даст мощный толчок подъёму сельского хозяйства.";
        using var stream = new MemoryStream(Encoding.GetEncoding(1251).GetBytes(text));

        var result = await _parser.ParseAsync(stream, "russian.txt");

        result.Content.Should().Be(text);
        result.Metadata["Encoding"].Should().Be("windows-1251");
    }

    [Theory]
    [InlineData("A\0BC")]
    [InlineData("2026-10-01 job=7 status=ok\0\n2026-10-01 job=8 status=ok\0\n2026-10-01 job=9 status=ok\0\n")]
    public async Task ParseAsync_Utf8WithAFewNuls_IsNotMistakenForUtf16(string text)
    {
        // Valid UTF-8 may contain NUL; a few on one byte parity are not UTF-16 evidence.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var result = await _parser.ParseAsync(stream, "log.txt");

        result.Metadata["Encoding"].Should().Be("utf-8");
        result.Content.Should().Be(text.Replace("\0", ""));
    }

    [Fact]
    public async Task ParseAsync_PipelineBufferedStream_IsReadFromItsPosition()
    {
        // The pipeline hands over an expandable MemoryStream; its buffer is read in place.
        var stream = new MemoryStream();
        stream.Write(Encoding.Latin1.GetBytes(LatinSentence));
        stream.Position = 0;

        var result = await _parser.ParseAsync(stream, "buffered.txt");

        result.Content.Should().Be(LatinSentence);
    }

    [Fact]
    public async Task ParseAsync_StrayNulCharacters_AreRemoved()
    {
        // PostgreSQL text columns reject NUL, which left documents stuck in Processing.
        using var stream = new MemoryStream("before\0after"u8.ToArray());

        var result = await _parser.ParseAsync(stream, "nul.txt");

        result.Content.Should().Be("beforeafter");
    }
}
