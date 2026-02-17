using Connapse.Core.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Parser for PDF documents using PdfPig.
/// </summary>
public class PdfParser : IDocumentParser
{
    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
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
            // PdfPig is synchronous, but we'll wrap it in Task.Run for cancellation support
            return await Task.Run(() =>
            {
                using var document = PdfDocument.Open(stream);

                metadata["FileType"] = "PDF";
                metadata["PageCount"] = document.NumberOfPages.ToString();

                // Extract document-level metadata
                if (document.Information != null)
                {
                    var info = document.Information;
                    if (!string.IsNullOrWhiteSpace(info.Title))
                        metadata["Title"] = info.Title;
                    if (!string.IsNullOrWhiteSpace(info.Author))
                        metadata["Author"] = info.Author;
                    if (!string.IsNullOrWhiteSpace(info.Subject))
                        metadata["Subject"] = info.Subject;
                    if (!string.IsNullOrWhiteSpace(info.Creator))
                        metadata["Creator"] = info.Creator;
                    if (!string.IsNullOrWhiteSpace(info.CreationDate))
                        metadata["CreationDate"] = info.CreationDate;
                }

                // Extract text from all pages
                var textBuilder = new System.Text.StringBuilder();
                for (int i = 1; i <= document.NumberOfPages; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var page = document.GetPage(i);
                        var pageText = page.Text;

                        if (!string.IsNullOrWhiteSpace(pageText))
                        {
                            // Add page marker for better context preservation
                            textBuilder.AppendLine($"--- Page {i} ---");
                            textBuilder.AppendLine(pageText);
                            textBuilder.AppendLine();
                        }
                        else
                        {
                            warnings.Add($"Page {i} contains no extractable text (may be scanned image)");
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Error extracting text from page {i}: {ex.Message}");
                    }
                }

                var content = textBuilder.ToString();

                if (string.IsNullOrWhiteSpace(content))
                {
                    warnings.Add("PDF contains no extractable text. Consider using OCR for scanned documents.");
                    content = string.Empty;
                }

                return new ParsedDocument(content, metadata, warnings);

            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"Error parsing PDF: {ex.Message}");
            return new ParsedDocument(string.Empty, metadata, warnings);
        }
    }
}
