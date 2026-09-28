using System.Globalization;
using System.Text.RegularExpressions;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Eval.Systems;

/// <summary>
/// Two views of one ingested document. <see cref="ParsedText"/> is what Connapse's parser extracts
/// from the file (null when no parser claims the extension or the parser threw), and
/// <see cref="Chunks"/> is what was stored for search, in chunk order. Both have Connapse's
/// <c>--- Page N ---</c> markers removed.
/// </summary>
public sealed record ProbeResult(
    string? ParsedText,
    IReadOnlyList<string> ParserWarnings,
    string? ParseError,
    int? PageCount,
    IReadOnlyList<int> EmptyPages,
    IReadOnlyList<string> Chunks);

/// <summary>
/// Reads back what Connapse made of a document, through the in-process host: the registered
/// <see cref="IDocumentParser"/> chosen by extension the way the ingestion pipeline chooses it, and
/// the stored chunk rows.
/// </summary>
public static partial class IngestionProbe
{
    // PdfParser writes each marker on a line of its own, just before that page's text.
    [GeneratedRegex(@"^--- Page (\d+) ---\r?$", RegexOptions.Multiline)]
    private static partial Regex PageMarkerLine();

    // PdfParser's warnings for a page with no text or a page that failed to extract.
    [GeneratedRegex(@"^(?:Page (\d+) contains no extractable text|Error extracting text from page (\d+):)")]
    private static partial Regex EmptyPageWarning();

    public static async Task<ProbeResult> ProbeAsync(
        IServiceProvider services, string filePath, string? connapseDocId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        string? parsed = null;
        string? parseError = null;
        IReadOnlyList<string> warnings = [];
        int? pageCount = null;
        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        IDocumentParser? parser = scope.ServiceProvider.GetServices<IDocumentParser>()
            .FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
        HashSet<int> emptyPageSet = [];
        HashSet<int> markedPages = [];

        if (parser is null)
        {
            parseError = $"No parser for extension '{extension}'";
        }
        else
        {
            try
            {
                await using FileStream stream = File.OpenRead(filePath);
                ParsedDocument document = await parser.ParseAsync(stream, Path.GetFileName(filePath), ct);
                foreach (Match marker in PageMarkerLine().Matches(document.Content))
                    markedPages.Add(int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture));
                parsed = PageMarkerLine().Replace(document.Content, "");
                warnings = document.Warnings;
                foreach (string warning in warnings)
                    if (EmptyPageWarning().Match(warning) is { Success: true } empty)
                        emptyPageSet.Add(int.Parse(empty.Groups[1].Success ? empty.Groups[1].Value : empty.Groups[2].Value, CultureInfo.InvariantCulture));
                if (document.Metadata.TryGetValue("PageCount", out string? pages)
                    && int.TryParse(pages, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    pageCount = count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                parseError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        List<string> chunks = [];
        if (connapseDocId is not null && Guid.TryParse(connapseDocId, out Guid documentId))
        {
            IDbContextFactory<KnowledgeDbContext> factory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
            await using KnowledgeDbContext db = await factory.CreateDbContextAsync(ct);
            chunks = (await db.Chunks.AsNoTracking()
                    .Where(c => c.DocumentId == documentId)
                    .OrderBy(c => c.ChunkIndex)
                    .Select(c => c.Content)
                    .ToListAsync(ct))
                .Select(chunk => StripPageMarkers(chunk, markedPages))
                .ToList();
        }

        IReadOnlyList<int> emptyPages = emptyPageSet.Order().ToList();
        return new ProbeResult(parsed, warnings, parseError, pageCount, emptyPages, chunks);
    }

    /// <summary>
    /// Removes Connapse's page markers from a chunk. They are an artifact of PdfParser, and an olmOCR
    /// "absent" check on a page number would otherwise match them. Chunkers can join lines, so a marker
    /// may sit mid-line; only markers for pages the parser actually marked are removed.
    /// </summary>
    public static string StripPageMarkers(string chunk, IReadOnlySet<int> markedPages) =>
        markedPages.Count == 0
            ? chunk
            : Regex.Replace(chunk, @"--- Page (\d+) ---", m =>
                markedPages.Contains(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) ? "" : m.Value);
}
