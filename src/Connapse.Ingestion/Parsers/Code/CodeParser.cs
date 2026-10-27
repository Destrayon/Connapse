using System.Globalization;
using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Parsers.Code;

/// <summary>
/// Parses C#, TypeScript and Python source with tree-sitter (CON-36): the text is kept as written, and its
/// <see cref="CodeOutline"/> goes with it in the metadata. Nothing is compiled or run. Runs in the parser host like
/// every built-in parser, where a tree-sitter crash or hang costs that process, not the web app.
/// </summary>
public sealed class CodeParser : IDocumentParser
{
    /// <summary>Files longer than this are indexed as text without an outline: a source file this size is generated.</summary>
    internal const int MaxOutlineCharacters = 2_000_000;

    /// <summary>Lines longer than this, on average, mark minified or generated code, which has no definitions worth a passage.</summary>
    internal const int MinifiedAverageLine = 300;

    /// <summary>
    /// The most an outline may hold before the file is indexed as text instead: a small upload dense with definitions
    /// or calls would otherwise cost the web process far more memory than its text does.
    /// </summary>
    internal const int MaxOutlineEntries = 100_000;

    internal const int MaxOutlineJsonCharacters = 8_000_000;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".mts", ".cts", ".tsx", ".py", ".pyi",
    };

    public IReadOnlySet<string> SupportedExtensions => Extensions;

    public async Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        var (content, encoding) = TextDecoding.Decode(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));

        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>
        {
            ["Encoding"] = encoding.WebName,
            ["FileType"] = "Code",
        };

        CodeGrammar grammar = CodeGrammar.ForExtension(Path.GetExtension(fileName))
            ?? throw new ArgumentException($"{fileName} is not a language {nameof(CodeParser)} reads.", nameof(fileName));
        metadata["CodeLanguage"] = grammar.Name;

        if (string.IsNullOrWhiteSpace(content))
        {
            warnings.Add("Document contains no readable text content");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }

        if (content.Length > MaxOutlineCharacters)
        {
            warnings.Add($"{content.Length:N0} characters is over the {MaxOutlineCharacters:N0} a source file is read for definitions; indexed as text");
            return new ParsedDocument(content, metadata, warnings);
        }

        int lines = content.Count(c => c == '\n') + 1;
        if (content.Length / lines > MinifiedAverageLine)
        {
            warnings.Add("looks minified or generated (very long lines); indexed as text without definitions");
            return new ParsedDocument(content, metadata, warnings);
        }

        CodeOutline outline = CodeSyntax.Read(grammar, content, MaxOutlineEntries);
        string json = outline.Truncated ? "" : outline.ToJson();
        if (outline.Truncated || json.Length > MaxOutlineJsonCharacters)
        {
            warnings.Add($"has more definitions and calls than the {MaxOutlineEntries:N0} a file is read for; indexed as text");
            return new ParsedDocument(content, metadata, warnings);
        }
        metadata[CodeOutline.ParsedDocumentMetadataKey] = json;
        metadata["LineCount"] = lines.ToString(CultureInfo.InvariantCulture);
        if (outline.HasErrors)
            warnings.Add("has syntax errors; definitions inside them may be missing");

        return new ParsedDocument(content, metadata, warnings);
    }
}
