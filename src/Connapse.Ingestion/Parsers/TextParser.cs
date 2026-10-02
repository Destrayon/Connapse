using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for plain text files (.txt, .md, .csv).
/// </summary>
public class TextParser : IDocumentParser
{
    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
        ".markdown",
        ".csv",
        ".log",
        ".json",
        ".xml",
        ".yaml",
        ".yml"
    };

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    /// <summary>2: encoding detection (#594). Version 1 read every file as UTF-8 and garbled Latin-1.</summary>
    public int Version => 2;

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();

        try
        {
            var (content, encoding) = TextDecoding.Decode(await ReadAllBytesAsync(stream, cancellationToken));
            metadata["Encoding"] = encoding.WebName;

            // Detect file type from extension
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            metadata["FileType"] = extension switch
            {
                ".md" or ".markdown" => "Markdown",
                ".csv" => "CSV",
                ".json" => "JSON",
                ".xml" => "XML",
                ".yaml" or ".yml" => "YAML",
                ".log" => "Log",
                _ => "PlainText"
            };

            // Basic validation
            if (string.IsNullOrWhiteSpace(content))
            {
                warnings.Add("Document contains no readable text content");
                content = string.Empty;
            }

            // Count lines and estimate structure
            var lines = content.Split('\n');
            metadata["LineCount"] = lines.Length.ToString();

            // For markdown, detect if there are headers
            if (extension is ".md" or ".markdown")
            {
                var hasHeaders = lines.Any(line => line.TrimStart().StartsWith('#'));
                metadata["HasMarkdownHeaders"] = hasHeaders.ToString();
            }

            // For CSV, detect delimiter (basic heuristic)
            if (extension == ".csv")
            {
                var firstLine = lines.FirstOrDefault() ?? string.Empty;
                var commaCount = firstLine.Count(c => c == ',');
                var tabCount = firstLine.Count(c => c == '\t');
                var semicolonCount = firstLine.Count(c => c == ';');

                metadata["CsvDelimiter"] = (commaCount, tabCount, semicolonCount) switch
                {
                    var (c, t, s) when c >= t && c >= s => ",",
                    var (c, t, s) when t > c && t >= s => "\\t",
                    _ => ";"
                };
            }

            return new ParsedDocument(content, metadata, warnings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Error reading text file: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }

    /// <summary>
    /// The pipeline has already buffered the file in a MemoryStream, so its buffer is read in
    /// place rather than copied: a second full copy of every large text file would double the
    /// worker's peak memory for nothing.
    /// </summary>
    internal static async Task<ReadOnlyMemory<byte>> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> segment))
        {
            int start = (int)memory.Position;
            memory.Position = memory.Length;
            return segment.AsMemory(start, (int)memory.Length - start);
        }

        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }
}
