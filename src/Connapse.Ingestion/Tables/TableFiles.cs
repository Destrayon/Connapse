using Connapse.Core;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// The tables read from one uploaded file, with the warnings reading it raised. Disposing it closes whatever the rows
/// still stream from (an open workbook), so it must outlive the store copying the rows in.
/// </summary>
internal sealed class TableFile(IReadOnlyList<TableLoad> tables, IReadOnlyList<string> warnings, IDisposable? owner) : IDisposable
{
    public IReadOnlyList<TableLoad> Tables { get; } = tables;

    public IReadOnlyList<string> Warnings { get; } = warnings;

    public void Dispose() => owner?.Dispose();
}

/// <summary>Picks the table reader for an uploaded file by its extension (CON-308).</summary>
internal static class TableFiles
{
    public static async Task<TableFile> ReadAsync(Stream file, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        switch (extension)
        {
            case ".xlsx":
                XlsxWorkbook workbook = XlsxWorkbook.Open(file, ct);
                try
                {
                    (IReadOnlyList<TableLoad> tables, IReadOnlyList<string> warnings) = XlsxTableReader.Read(workbook, fileName, documentId, ct);
                    return new TableFile(tables, warnings, workbook);
                }
                catch
                {
                    workbook.Dispose();
                    throw;
                }
            case ".json" or ".jsonl" or ".ndjson":
                (IReadOnlyList<TableLoad> json, string? warning, IDisposable? owner) = await JsonTableReader.ReadAsync(file, fileName, documentId, ct);
                return new TableFile(json, warning is null ? [] : [warning], owner);
            default:
                return new TableFile([await CsvTableReader.ReadAsync(file, fileName, documentId, ct)], [], null);
        }
    }
}
