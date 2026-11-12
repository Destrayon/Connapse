using System.Globalization;
using System.Runtime.CompilerServices;
using Connapse.Core;
using Connapse.Core.Utilities;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns each sheet of an .xlsx workbook into typed table loads (CON-308). A sheet is read several times and never
/// held whole: once for its merged ranges, once for its layout, once for the text around its tables, once to type
/// every column, and once more as the store copies the rows in.
/// <para>
/// Each block of rows between blank rows that has at least two rows of two or more cells is a table; a sheet with
/// several loads each as its own table. A table's header is the first row about as wide as the block's usual row,
/// which skips a title or a note above it (Frictionless's width rule, with the most common width in place of the
/// mean). A header row with a cell merged across columns, over a row of text, is a two-row header: each column is
/// named from both rows, as in "2025 Hires". Cells inside a merged range take the range's value.
/// </para>
/// <para>
/// Nothing on the sheet is dropped. Text outside the tables, such as a title, a units line or a footnote, goes into
/// each table's description. Cells beside the header's columns load into columns with generated names. A hidden sheet
/// loads too, and its description says it is hidden. An Excel error (#N/A, #DIV/0!) or a formula the file was saved
/// without a result for leaves its cell empty and is listed among the table's rejects with the formula or error; the
/// rest of its row loads. Columns are typed from every value: one kind throughout gives that type, mixed kinds give text.
/// </para>
/// <para>
/// A table that may have been read wrong still loads, flagged needs-review with a warning that says why: the sheet
/// holds several tables, a header has merged cells that couldn't be read as two rows, a header with no text in it,
/// blank or repeated column names, or cells with an error or a formula that has no saved result.
/// </para>
/// </summary>
internal static class XlsxTableReader
{
    /// <summary>The longest a table name gets, the same as a CSV's.</summary>
    public const int MaxNameChars = 256;

    /// <summary>The longest the text kept from around a sheet's tables gets in a table's description.</summary>
    public const int MaxNoteChars = 4000;

    /// <summary>
    /// Reads every sheet that has data, hidden ones included. The workbook must stay open until every returned load's
    /// rows have been read.
    /// </summary>
    /// <exception cref="InvalidDataException">No sheet has any data.</exception>
    public static (IReadOnlyList<TableLoad> Tables, IReadOnlyList<string> Warnings) Read(
        XlsxWorkbook workbook, string fileName, Guid? documentId, CancellationToken ct = default)
    {
        string file = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (file.Length == 0)
            file = "workbook";
        List<TableLoad> tables = [];
        List<string> warnings = [];
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (XlsxSheet sheet in workbook.Sheets)
        {
            IReadOnlyList<XlsxMerge> merges = workbook.Merges(sheet, ct);
            List<SheetLayout> layouts = Layouts(workbook, sheet, merges, ct);
            if (layouts.Count == 0)
                continue;
            string? description = Describe(sheet.Hidden, Notes(workbook, sheet, merges, layouts, ct));

            for (int i = 0; i < layouts.Count; i++)
            {
                SheetLayout layout = layouts[i];
                string sheetName = Clean(sheet.Name);
                string label = layouts.Count == 1 ? $"Sheet {sheetName}" : $"Table {i + 1} of sheet {sheetName}";
                string name = Unique(names, Clip($"{file} - {sheetName}" + (i == 0 ? "" : $" ({i + 1})")));
                ColumnScan scan = Scan(workbook, sheet, layout, ct);
                List<string> reasons = [.. scan.Reasons];
                if (layouts.Count > 1)
                    reasons.Insert(0, $"the sheet holds {layouts.Count} separate tables, and each was loaded as its own table");
                if (reasons.Count > 0)
                    warnings.Add($"{label} loaded as a table but needs review: {string.Join("; ", reasons)}.");
                if (scan.NoValueCells > 0)
                    warnings.Add(
                        $"{label} has {scan.NoValueCells} formula cell(s) the file was saved without results for; they are empty " +
                        "in the table, and its rejects list each formula. Open and save the file in Excel to store the results.");
                if (scan.ErrorCells > 0)
                    warnings.Add($"{label} has {scan.ErrorCells} cell(s) with an Excel error such as #DIV/0!; they are empty in the table, and its rejects list them.");

                TableColumn[] columns = [.. scan.Names.Select((n, c) => new TableColumn(n, scan.Types[c]))];
                tables.Add(new TableLoad(
                    name, columns, RowsAsync(workbook, sheet, layout, scan, ct), documentId,
                    Description: description, NeedsReview: reasons.Count > 0 || scan.RejectCount > 0, Rejects: scan.Rejects));
            }
        }

        if (tables.Count == 0)
            throw new InvalidDataException("The workbook has no sheet with data.");
        return (tables, warnings);
    }

    /// <summary>
    /// Where one table is on its sheet: its merged ranges, its last row, its header row, its columns, and whether its
    /// header has merged cells and may take two rows.
    /// </summary>
    private sealed record SheetLayout(
        IReadOnlyList<XlsxMerge> Merges, long FirstRow, long LastRow, long HeaderRow, int Left, int Right,
        bool MergedHeader, bool TwoRowHeader);

    private readonly record struct RowShape(long Number, int Count, int Left, int Right);

    /// <summary>Finds the tables on a sheet from each row's width alone; none for a sheet with no data.</summary>
    private static List<SheetLayout> Layouts(XlsxWorkbook workbook, XlsxSheet sheet, IReadOnlyList<XlsxMerge> merges, CancellationToken ct)
    {
        List<List<RowShape>> blocks = [];
        foreach (XlsxRow row in workbook.Rows(sheet, merges, ct))
        {
            var shape = new RowShape(row.Number, row.Cells.Count, row.Cells[0].Column, row.Cells[^1].Column);
            if (blocks.Count == 0 || blocks[^1][^1].Number != row.Number - 1)
                blocks.Add([]);
            blocks[^1].Add(shape);
        }
        if (blocks.Count == 0)
            return [];

        // A table is a block with two or more rows of two or more cells; a sheet without one loads its largest block.
        List<List<RowShape>> found = [.. blocks.Where(b => b.Count(r => r.Count >= 2) >= 2)];
        if (found.Count == 0)
            found = [blocks.MaxBy(b => b.Sum(r => (long)r.Count))!];
        return [.. found.Select(b => Layout(b, merges))];
    }

    private static SheetLayout Layout(List<RowShape> block, IReadOnlyList<XlsxMerge> merges)
    {
        // The usual width: the most common count of cells among rows with two or more; ties go to the wider.
        List<RowShape> wide = [.. block.Where(r => r.Count >= 2)];
        RowShape header;
        if (wide.Count == 0)
        {
            header = block[0];
        }
        else
        {
            int usual = wide.GroupBy(r => r.Count).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
            int least = Math.Max(2, usual - Math.Max(1, (int)Math.Round(0.1 * usual, MidpointRounding.AwayFromZero)));
            header = wide.First(r => r.Count >= least);
        }

        // The columns are every one used from the header down, so a cell beside the header's columns still loads.
        IEnumerable<RowShape> table = block.Where(r => r.Number >= header.Number);
        // A merge that starts on the header and spans columns or the row below marks a header that may take two rows.
        bool merged = merges.Any(m => m.Top <= header.Number && m.Bottom >= header.Number);
        bool twoRows = merges.Any(m => m.Top == header.Number && (m.Right > m.Left || m.Bottom == header.Number + 1));
        return new SheetLayout(merges, block[0].Number, block[^1].Number, header.Number,
            table.Min(r => r.Left), table.Max(r => r.Right), merged, twoRows);
    }

    /// <summary>
    /// The text on a sheet outside its tables, a line per row with cells joined by " | ", such as a title, a units
    /// line or a footnote; null when there is none.
    /// </summary>
    private static string? Notes(
        XlsxWorkbook workbook, XlsxSheet sheet, IReadOnlyList<XlsxMerge> merges, List<SheetLayout> layouts, CancellationToken ct)
    {
        List<string> lines = [];
        int length = 0;
        foreach (XlsxRow row in workbook.Rows(sheet, merges, ct))
        {
            if (layouts.Any(l => row.Number >= l.HeaderRow && row.Number <= l.LastRow))
                continue;
            string line = string.Join(" | ", row.Cells.Select(c => Display(c).ReplaceLineEndings(" ").Trim()).Where(t => t.Length > 0).Distinct());
            if (line.Length == 0)
                continue;
            lines.Add(line);
            length += line.Length + 3;
            if (length > MaxNoteChars)
                break;
        }
        if (lines.Count == 0)
            return null;
        string notes = string.Join(" / ", lines);
        return notes.Length > MaxNoteChars ? notes[..MaxNoteChars] + "…" : notes;
    }

    private static string? Describe(bool hidden, string? notes)
    {
        List<string> parts = [];
        if (hidden)
            parts.Add("This sheet is hidden in Excel.");
        if (notes is not null)
            parts.Add($"Text on the sheet outside the table: {notes}");
        return parts.Count == 0 ? null : StorableText.Clean(string.Join(" ", parts));
    }

    /// <summary>
    /// What a pass over every row found: the column names and types, the first row of data, and the cells that
    /// couldn't load.
    /// </summary>
    private sealed record ColumnScan(
        IReadOnlyList<string> Names, IReadOnlyList<TableColumnType> Types, long DataRow,
        IReadOnlyList<TableReject> Rejects, long RejectCount, long NoValueCells, long ErrorCells, List<string> Reasons);

    private sealed class ColumnKinds
    {
        public bool Text, Number, Boolean, Date, Fraction, TooLarge, Time;

        public void Add(XlsxCell cell)
        {
            switch (cell.Kind)
            {
                case XlsxCellKind.Text: Text = true; break;
                case XlsxCellKind.Boolean: Boolean = true; break;
                case XlsxCellKind.Date:
                    Date = true;
                    Time |= cell.Date.TimeOfDay != TimeSpan.Zero;
                    break;
                case XlsxCellKind.Number:
                    Number = true;
                    Fraction |= Math.Floor(cell.Number) != cell.Number || Math.Abs(cell.Number) >= 9.2e18;
                    TooLarge |= Math.Abs(cell.Number) >= 7.9e28;
                    break;
            }
        }

        public TableColumnType Type => (Text, Number, Boolean, Date) switch
        {
            (false, true, false, false) => TooLarge ? TableColumnType.Text : Fraction ? TableColumnType.Number : TableColumnType.WholeNumber,
            (false, false, true, false) => TableColumnType.Boolean,
            (false, false, false, true) => Time ? TableColumnType.DateTime : TableColumnType.Date,
            _ => TableColumnType.Text,
        };
    }

    private static ColumnScan Scan(XlsxWorkbook workbook, XlsxSheet sheet, SheetLayout layout, CancellationToken ct)
    {
        int width = layout.Right - layout.Left + 1;
        var kinds = new ColumnKinds[width];
        for (int c = 0; c < width; c++)
            kinds[c] = new ColumnKinds();
        IReadOnlyList<XlsxCell>? headerCells = null, secondCells = null;
        bool hasHeader = false;
        List<TableReject> rejects = [];
        long rejectCount = 0, noValue = 0, errors = 0;

        foreach (XlsxRow row in workbook.Rows(sheet, layout.Merges, ct))
        {
            if (row.Number < layout.HeaderRow)
                continue;
            if (row.Number > layout.LastRow)
                break;
            if (row.Number == layout.HeaderRow)
            {
                headerCells = row.Cells;
                hasHeader = row.Cells.Any(c => c.Kind == XlsxCellKind.Text);
                if (hasHeader)
                    continue;
            }
            else if (hasHeader && layout.TwoRowHeader && row.Number == layout.HeaderRow + 1
                && row.Cells.All(c => c.Kind == XlsxCellKind.Text))
            {
                secondCells = row.Cells;
                continue;
            }

            foreach (XlsxCell cell in row.Cells)
            {
                int c = cell.Column - layout.Left;
                if (!cell.IsBad)
                {
                    kinds[c].Add(cell);
                    continue;
                }
                rejectCount++;
                string reason;
                if (cell.Kind == XlsxCellKind.Error)
                {
                    errors++;
                    reason = $"is the Excel error {cell.Text}, so the cell is empty in the table";
                }
                else
                {
                    noValue++;
                    reason = "is a formula the file was saved without a result for, so the cell is empty in the table";
                }
                if (rejects.Count < CsvTableReader.MaxRejects)
                    rejects.Add(new TableReject(row.Number, c + 1, Clip(cell.Text, CsvTableReader.MaxRejectValueChars), reason));
            }
        }

        List<string> reasons = [];
        string?[] header = new string?[width];
        if (!hasHeader)
        {
            reasons.Add("its first row has no text in it, so the columns were given generated names");
        }
        else
        {
            foreach (XlsxCell cell in headerCells!)
                header[cell.Column - layout.Left] = cell.Kind == XlsxCellKind.Text ? cell.Text : Display(cell);
            if (secondCells is not null)
            {
                foreach (XlsxCell cell in secondCells)
                {
                    int c = cell.Column - layout.Left;
                    string below = cell.Text.Trim();
                    string? above = header[c]?.Trim();
                    header[c] = CsvSniffer.IsNull(above) || string.Equals(above, below, StringComparison.OrdinalIgnoreCase)
                        ? below
                        : $"{above} {below}";
                }
            }
            else if (layout.MergedHeader)
            {
                reasons.Add("its header has merged cells, so it may take more than one row");
            }
            if (header.Any(CsvSniffer.IsNull))
                reasons.Add("some columns have no name, so they were given generated ones");
            int distinct = header.Where(h => !CsvSniffer.IsNull(h)).Select(h => h!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct < header.Count(h => !CsvSniffer.IsNull(h)))
                reasons.Add("some column names repeat, so _1, _2… were added to make them unique");
        }

        long dataRow = !hasHeader ? layout.HeaderRow : layout.HeaderRow + (secondCells is null ? 1 : 2);
        return new ColumnScan(
            CsvSniffer.Names(hasHeader ? header : null, width), [.. kinds.Select(k => k.Type)], dataRow,
            rejects, rejectCount, noValue, errors, reasons);
    }

    private static async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(
        XlsxWorkbook workbook, XlsxSheet sheet, SheetLayout layout, ColumnScan scan, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        int width = layout.Right - layout.Left + 1;
        foreach (XlsxRow row in workbook.Rows(sheet, layout.Merges, ct))
        {
            if (row.Number < scan.DataRow)
                continue;
            if (row.Number > layout.LastRow)
                yield break;
            var values = new object?[width];
            foreach (XlsxCell cell in row.Cells)
            {
                // An error or a formula with no saved result stays empty; its reject says what the cell held.
                if (cell.IsBad)
                    continue;
                int c = cell.Column - layout.Left;
                values[c] = Convert(cell, scan.Types[c]);
            }
            yield return values;
        }
    }

    private static object? Convert(XlsxCell cell, TableColumnType type) => type switch
    {
        TableColumnType.WholeNumber => (long)cell.Number,
        TableColumnType.Number => decimal.Parse(cell.Number.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
        TableColumnType.Boolean => cell.Text == "TRUE",
        TableColumnType.Date => DateOnly.FromDateTime(cell.Date),
        TableColumnType.DateTime => cell.Date,
        _ => StorableText.Clean(cell.Kind == XlsxCellKind.Text ? cell.Text : Display(cell)),
    };

    /// <summary>A value as text, the way a text column holds it: numbers to Excel's 15 digits, dates in ISO form.</summary>
    internal static string Display(XlsxCell cell) => cell.Kind switch
    {
        XlsxCellKind.Number => cell.Number.ToString("G15", CultureInfo.InvariantCulture),
        XlsxCellKind.Date => cell.Date.TimeOfDay == TimeSpan.Zero
            ? cell.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : cell.Date.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF", CultureInfo.InvariantCulture),
        _ => cell.Text,
    };

    private static string Clean(string text) => StorableText.Clean(text.Trim());

    private static string Clip(string text) => Clip(text, MaxNameChars);

    private static string Clip(string text, int max) => StorableText.Clean(text.Length > max ? text[..max] : text);

    private static string Unique(HashSet<string> used, string name)
    {
        string unique = name;
        for (int n = 1; !used.Add(unique); n++)
            unique = $"{name}_{n}";
        return unique;
    }
}
