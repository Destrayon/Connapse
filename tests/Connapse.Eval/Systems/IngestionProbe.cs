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
    [GeneratedRegex(@"--- Page (\d+) ---")]
    private static partial Regex PageMarker();

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
        HashSet<int> pagesWithText = [];

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
                foreach (Match marker in PageMarker().Matches(document.Content))
                    pagesWithText.Add(int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture));
                parsed = StripPageMarkers(document.Content);
                warnings = document.Warnings;
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
                .Select(StripPageMarkers)
                .ToList();
        }

        IReadOnlyList<int> emptyPages = pageCount is { } n
            ? Enumerable.Range(1, n).Where(p => !pagesWithText.Contains(p)).ToList()
            : [];
        return new ProbeResult(parsed, warnings, parseError, pageCount, emptyPages, chunks);
    }

    /// <summary>
    /// Removes Connapse's page markers. They are an artifact of PdfParser, and an olmOCR "absent"
    /// check on a page number would otherwise match them.
    /// </summary>
    public static string StripPageMarkers(string text) => PageMarker().Replace(text, "");
}
