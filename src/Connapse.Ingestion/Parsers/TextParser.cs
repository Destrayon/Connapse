using AIKnowledge.Core.Interfaces;

namespace AIKnowledge.Ingestion.Parsers;

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

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();

        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var content = await reader.ReadToEndAsync(cancellationToken);

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
}
