using System.Text;
using Connapse.Core.Interfaces;
using Connapse.Ingestion.Tables;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Reads an Excel workbook (.xlsx) as searchable text (CON-308): each sheet under its name, one line per row,
/// cells separated by tabs. A long sheet is cut short in the text; in a container it also loads whole as a table,
/// which is where its every row can be queried.
/// </summary>
public class XlsxParser : IDocumentParser
{
    /// <summary>The most rows of one sheet written into the text.</summary>
    public const int MaxRowsPerSheet = 1000;

    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xlsx" };

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    /// <summary>
    /// 1: visible sheets as tab-separated rows, merged cells filled, hidden sheets left out. 2: hidden sheets too,
    /// marked hidden, since they load as tables.
    /// </summary>
    public int Version => 2;

    public Task<ParsedDocument> ParseAsync(Stream stream, string fileName, CancellationToken cancellationToken = default) =>
        Task.Run(() => Parse(stream, cancellationToken), cancellationToken);

    private static ParsedDocument Parse(Stream stream, CancellationToken ct)
    {
        var warnings = new List<string>();
        var metadata = new Dictionary<string, string> { ["FileType"] = "Excel Workbook" };
        var text = new StringBuilder();

        using XlsxWorkbook workbook = XlsxWorkbook.Open(stream, ct);
        int sheets = 0;
        foreach (XlsxSheet sheet in workbook.Sheets)
        {
            sheets++;
            if (text.Length > 0)
                text.AppendLine();
            text.Append("Sheet: ").Append(sheet.Name).AppendLine(sheet.Hidden ? " (hidden in Excel)" : "");

            int written = 0;
            bool more = false;
            foreach (XlsxRow row in workbook.Rows(sheet, workbook.Merges(sheet, ct), ct))
            {
                if (row.Cells.Count == 0)
                    continue;
                if (written == MaxRowsPerSheet)
                {
                    more = true;
                    break;
                }
                int column = 0;
                foreach (XlsxCell cell in row.Cells)
                {
                    for (; column < cell.Column; column++)
                        text.Append('\t');
                    text.Append(XlsxTableReader.Display(cell).ReplaceLineEndings(" "));
                }
                text.AppendLine();
                written++;
            }
            if (more)
                text.AppendLine($"(The sheet has more than {MaxRowsPerSheet:N0} rows; the rest are in its table.)");
        }

        metadata["SheetCount"] = sheets.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (sheets == 0)
            warnings.Add("The workbook has no sheet.");
        return new ParsedDocument(text.ToString(), metadata, warnings);
    }
}
