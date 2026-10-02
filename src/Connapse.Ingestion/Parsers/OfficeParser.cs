using Connapse.Core.Interfaces;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentFormat.OpenXml.Presentation;
using System.Text;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for Microsoft Office documents (.docx, .pptx) using OpenXML.
/// </summary>
public class OfficeParser : IDocumentParser
{
    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx",
        ".pptx"
    };

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    /// <summary>
    /// 2: DOCX as Markdown in document order -- tables once, headings, lists, headers, footers,
    /// footnotes, endnotes and text boxes; tracked deletions excluded (#599).
    /// 3: PPTX as Markdown -- a heading per slide with its title, paragraphs instead of one line
    /// per run, tables, grouped shapes and speaker notes (#599).
    /// </summary>
    public int Version => 3;

    public async Task<ParsedDocument> ParseAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string>();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        try
        {
            // OpenXML operations are synchronous
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                return extension switch
                {
                    ".docx" => ParseWordDocument(stream, metadata, warnings, cancellationToken),
                    ".pptx" => ParsePowerPointDocument(stream, metadata, warnings, cancellationToken),
                    _ => throw new NotSupportedException($"Extension {extension} is not supported by OfficeParser")
                };

            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing Office document: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }

    private static ParsedDocument ParseWordDocument(
        Stream stream,
        Dictionary<string, string> metadata,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(stream, false);

        metadata["FileType"] = "Word Document";

        // Extract core properties
        var coreProps = document.PackageProperties;
        if (!string.IsNullOrWhiteSpace(coreProps.Title))
            metadata["Title"] = coreProps.Title;
        if (!string.IsNullOrWhiteSpace(coreProps.Creator))
            metadata["Author"] = coreProps.Creator;
        if (!string.IsNullOrWhiteSpace(coreProps.Subject))
            metadata["Subject"] = coreProps.Subject;
        if (coreProps.Created.HasValue)
            metadata["CreationDate"] = coreProps.Created.Value.ToString("O");

        if (document.MainDocumentPart?.Document?.Body is null)
        {
            warnings.Add("Document body is empty or inaccessible");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }

        var content = DocxMarkdown.Convert(document, cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            warnings.Add("Document contains no extractable text");
            content = string.Empty;
        }

        return new ParsedDocument(content, metadata, warnings);
    }

    private static ParsedDocument ParsePowerPointDocument(
        Stream stream,
        Dictionary<string, string> metadata,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var document = PresentationDocument.Open(stream, false);

        metadata["FileType"] = "PowerPoint Presentation";

        // Extract core properties
        var coreProps = document.PackageProperties;
        if (!string.IsNullOrWhiteSpace(coreProps.Title))
            metadata["Title"] = coreProps.Title;
        if (!string.IsNullOrWhiteSpace(coreProps.Creator))
            metadata["Author"] = coreProps.Creator;
        if (!string.IsNullOrWhiteSpace(coreProps.Subject))
            metadata["Subject"] = coreProps.Subject;
        if (coreProps.Created.HasValue)
            metadata["CreationDate"] = coreProps.Created.Value.ToString("O");

        var presentationPart = document.PresentationPart;
        if (presentationPart == null)
        {
            warnings.Add("Presentation is empty or inaccessible");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }

        var slideIdList = presentationPart.Presentation?.SlideIdList;
        if (slideIdList == null)
        {
            warnings.Add("No slides found in presentation");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }

        metadata["SlideCount"] = slideIdList.Count().ToString();

        var content = PptxMarkdown.Convert(presentationPart, cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            warnings.Add("Presentation contains no extractable text");
            content = string.Empty;
        }

        return new ParsedDocument(content, metadata, warnings);
    }
}
