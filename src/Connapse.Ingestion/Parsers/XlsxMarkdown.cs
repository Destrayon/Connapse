using System.Globalization;
using System.Text;
using Connapse.Ingestion.Tables;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Writes an Excel workbook as Markdown (CON-312): a <c>## Sheet N: name</c> heading per visible sheet, then each block
/// of cells as a pipe table whose first row is the header. A sheet can hold several tables; blocks are split at fully
/// empty rows, as Docling and Unstructured split them
/// (docs/research/con-312-table-extraction-and-passage-markers-2026-11-11.md). Values are written as Excel shows the
/// common types: shared and inline strings, numbers in invariant form, TRUE/FALSE, and dates as ISO dates.
/// </summary>
internal static class XlsxMarkdown
{
    /// <summary>The most rows read from one sheet; the rest are left out with a warning.</summary>
    public const int MaxRowsPerSheet = 100_000;

    /// <summary>The most columns read from one sheet.</summary>
    public const int MaxColumns = 512;

    public static string Convert(SpreadsheetDocument document, List<string> warnings, CancellationToken ct)
    {
        WorkbookPart? workbook = document.WorkbookPart;
        if (workbook?.Workbook?.Sheets is not { } sheets)
            return string.Empty;

        SharedStringTable? strings = workbook.SharedStringTablePart?.SharedStringTable;
        HashSet<uint> dateStyles = DateStyles(workbook.WorkbookStylesPart?.Stylesheet);
        var output = new StringBuilder();
        int number = 0;

        foreach (Sheet sheet in sheets.Elements<Sheet>())
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (sheet.State?.Value is { } state && (state == SheetStateValues.Hidden || state == SheetStateValues.VeryHidden))
                continue;
            if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part ||
                part.Worksheet?.GetFirstChild<SheetData>() is not { } data)
            {
                continue;
            }

            List<SortedDictionary<int, string>> rows = ReadRows(data, strings, dateStyles, sheet.Name?.Value, warnings, ct);
            List<List<SortedDictionary<int, string>>> blocks = Blocks(rows);
            if (blocks.Count == 0)
                continue;

            output.Append("## Sheet ").Append(number);
            if (!string.IsNullOrWhiteSpace(sheet.Name?.Value))
                output.Append(": ").Append(sheet.Name!.Value);
            output.Append("\n\n");
            foreach (List<SortedDictionary<int, string>> block in blocks)
                AppendBlock(output, block);
        }
        return output.ToString().Trim();
    }

    /// <summary>The sheet's rows in order, each as its non-empty cells by 0-based column; an empty row is an empty map.</summary>
    private static List<SortedDictionary<int, string>> ReadRows(
        SheetData data, SharedStringTable? strings, HashSet<uint> dateStyles, string? sheetName, List<string> warnings, CancellationToken ct)
    {
        List<SortedDictionary<int, string>> rows = [];
        uint lastRow = 0;
        foreach (Row row in data.Elements<Row>())
        {
            uint index = row.RowIndex?.Value ?? lastRow + 1;
            if (index > MaxRowsPerSheet)
            {
                warnings.Add($"Sheet {sheetName} has more than {MaxRowsPerSheet:N0} rows; the rest were not read");
                break;
            }
            if ((index & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();
            for (uint gap = lastRow + 1; gap < index; gap++)
                rows.Add([]);
            lastRow = index;

            var cells = new SortedDictionary<int, string>();
            int next = 0;
            foreach (Cell cell in row.Elements<Cell>())
            {
                int column = cell.CellReference?.Value is { } reference ? ColumnIndex(reference) : next;
                next = column + 1;
                if (column < 0 || column >= MaxColumns)
                    continue;
                string text = Text(cell, strings, dateStyles);
                if (text.Length > 0)
                    cells[column] = text;
            }
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>Runs of rows with at least one value, split at fully empty rows.</summary>
    private static List<List<SortedDictionary<int, string>>> Blocks(List<SortedDictionary<int, string>> rows)
    {
        List<List<SortedDictionary<int, string>>> blocks = [];
        List<SortedDictionary<int, string>>? current = null;
        foreach (SortedDictionary<int, string> row in rows)
        {
            if (row.Count == 0)
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = [];
                blocks.Add(current);
            }
            current.Add(row);
        }
        return blocks;
    }

    private static void AppendBlock(StringBuilder output, List<SortedDictionary<int, string>> block)
    {
        int first = block.Min(r => r.Keys.First());
        int last = block.Max(r => r.Keys.Last());
        if (block.Count == 1 && first == last)
        {
            // A lone value is a title or a note, not a table.
            output.Append(Escape(block[0][first])).Append("\n\n");
            return;
        }

        for (int r = 0; r < block.Count; r++)
        {
            output.Append('|');
            for (int c = first; c <= last; c++)
                output.Append(' ').Append(block[r].TryGetValue(c, out string? v) ? Escape(v) : "").Append(" |");
            output.Append('\n');
            if (r == 0)
            {
                output.Append('|');
                for (int c = first; c <= last; c++)
                    output.Append(" --- |");
                output.Append('\n');
            }
        }
        output.Append('\n');
    }

    private static string Escape(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|");

    private static string Text(Cell cell, SharedStringTable? strings, HashSet<uint> dateStyles)
    {
        string raw = cell.CellValue?.Text ?? "";
        CellValues type = cell.DataType?.Value ?? CellValues.Number;
        if (type == CellValues.SharedString)
        {
            if (strings is null || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int at))
                return "";
            return strings.ElementAtOrDefault(at) is SharedStringItem item ? item.InnerText : "";
        }
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? raw;
        if (type == CellValues.Boolean)
            return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;
        if (type == CellValues.Error || type == CellValues.String || type == CellValues.Date)
            return raw;

        if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial) &&
            serial is >= 0 and < 2958466)
        {
            DateTime when = DateTime.FromOADate(serial);
            return when.TimeOfDay == TimeSpan.Zero
                ? when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
            return number.ToString(CultureInfo.InvariantCulture);
        return raw;
    }

    /// <summary>The cell format indexes whose number format shows a date or time.</summary>
    private static HashSet<uint> DateStyles(Stylesheet? styles)
    {
        HashSet<uint> result = [];
        if (styles?.CellFormats is not { } formats)
            return result;

        Dictionary<uint, string> custom = [];
        if (styles.NumberingFormats is { } numbering)
        {
            foreach (NumberingFormat format in numbering.Elements<NumberingFormat>())
            {
                if (format.NumberFormatId?.Value is { } fid && format.FormatCode?.Value is { } code)
                    custom[fid] = code;
            }
        }

        uint index = 0;
        foreach (CellFormat format in formats.Elements<CellFormat>())
        {
            uint id = format.NumberFormatId?.Value ?? 0;
            if (id is (>= 14 and <= 22) or (>= 45 and <= 47) || (custom.TryGetValue(id, out string? code) && IsDateCode(code)))
                result.Add(index);
            index++;
        }
        return result;
    }

    /// <summary>A number format that shows a date: it has d, m, y, h or s outside quotes and brackets.</summary>
    internal static bool IsDateCode(string code) => XlsxWorkbook.IsDateFormat(code);

    /// <summary>The 0-based column of a reference like <c>AB12</c>, or -1 when it has no letters.</summary>
    internal static int ColumnIndex(string reference) =>
        XlsxWorkbook.TryParseReference(reference, out _, out int column) ? column : -1;
}
