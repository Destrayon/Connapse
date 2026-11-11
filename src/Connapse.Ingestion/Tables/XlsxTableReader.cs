using System.Globalization;
using System.Runtime.CompilerServices;
using Connapse.Core;
using Connapse.Core.Utilities;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// Turns each sheet of an .xlsx workbook into a typed table load (CON-308). A sheet is read several times and never
/// held whole: once for its merged ranges, once for its layout, once to type every column, and once more as the store
/// copies the rows in.
/// <para>
/// The table is the sheet's largest block of rows between blank rows. Its header is the first row about as wide as the
/// block's usual row, which skips a title or a note above it (Frictionless's width rule, with the most common width in
/// place of the mean). Cells inside a merged range take the range's value. A sheet that may have been read wrong still
/// loads, flagged needs-review with a warning that says why: another block beside the table, a merged header, a header
/// with no text in it, blank or repeated column names, or cells outside the header's columns.
/// </para>
/// <para>
/// A row holding an Excel error (#N/A, #DIV/0!) or a formula the file was saved without a result for is left out and
/// listed as a reject, never loaded with the cell empty. Columns are typed from every row: one kind of value throughout
/// gives that type, mixed kinds give text.
/// </para>
/// </summary>
internal static class XlsxTableReader
{
    /// <summary>The longest a table name gets, the same as a CSV's.</summary>
    public const int MaxNameChars = 256;

    /// <summary>
    /// Reads every visible sheet that has data. Hidden sheets aren't loaded; each is named in a warning. The workbook
    /// must stay open until every returned load's rows have been read.
    /// </summary>
    /// <exception cref="InvalidDataException">No visible sheet has any data.</exception>
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
            if (sheet.Hidden)
            {
                warnings.Add($"Sheet {Clean(sheet.Name)} is hidden, so it wasn't loaded as a table.");
                continue;
            }
            SheetLayout? layout = Layout(workbook, sheet, ct);
            if (layout is null)
                continue;

            string name = Unique(names, Clip($"{file} - {Clean(sheet.Name)}"));
            ColumnScan scan = Scan(workbook, sheet, layout, ct);
            List<string> reasons = [.. layout.Reasons, .. scan.Reasons];
            if (reasons.Count > 0)
                warnings.Add($"Sheet {Clean(sheet.Name)} loaded as a table but needs review: {string.Join("; ", reasons)}.");
            if (scan.NoValueRows > 0)
                warnings.Add(
                    $"Sheet {Clean(sheet.Name)} has {scan.NoValueRows} row(s) with formulas the file was saved without results " +
                    "for; they were left out of the table. Open and save the file in Excel to store the results.");

            TableColumn[] columns = [.. scan.Names.Select((n, i) => new TableColumn(n, scan.Types[i]))];
            tables.Add(new TableLoad(
                name, columns, RowsAsync(workbook, sheet, layout, scan, ct), documentId,
                Description: null, NeedsReview: reasons.Count > 0 || scan.RejectCount > 0, Rejects: scan.Rejects));
        }

        if (tables.Count == 0)
            throw new InvalidDataException("The workbook has no visible sheet with data.");
        return (tables, warnings);
    }

    /// <summary>Where a sheet's table is: its rows, its header and its columns, and what about it is doubtful.</summary>
    private sealed record SheetLayout(
        IReadOnlyList<XlsxMerge> Merges, long FirstRow, long LastRow, long HeaderRow, int Left, int Right, List<string> Reasons);

    private readonly record struct RowShape(long Number, int Count, int Left, int Right);

    /// <summary>Finds the table on a sheet from each row's width alone; null for a sheet with no data.</summary>
    private static SheetLayout? Layout(XlsxWorkbook workbook, XlsxSheet sheet, CancellationToken ct)
    {
        IReadOnlyList<XlsxMerge> merges = workbook.Merges(sheet, ct);
        List<List<RowShape>> blocks = [];
        foreach (XlsxRow row in workbook.Rows(sheet, merges, ct))
        {
            var shape = new RowShape(row.Number, row.Cells.Count, row.Cells[0].Column, row.Cells[^1].Column);
            if (blocks.Count == 0 || blocks[^1][^1].Number != row.Number - 1)
                blocks.Add([]);
            blocks[^1].Add(shape);
        }
        if (blocks.Count == 0)
            return null;

        List<RowShape> block = blocks.MaxBy(b => b.Sum(r => (long)r.Count))!;
        List<string> reasons = [];
        if (blocks.Any(b => !ReferenceEquals(b, block) && b.Count >= 2 && b.Max(r => r.Count) >= 2))
            reasons.Add("the sheet has more than one block of rows, and only the largest was loaded");

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

        if (merges.Any(m => m.Top <= header.Number && m.Bottom >= header.Number))
            reasons.Add("its header has merged cells, so it may take more than one row");

        return new SheetLayout(merges, block[0].Number, block[^1].Number, header.Number, header.Left, header.Right, reasons);
    }

    /// <summary>What a pass over every row found: the column names and types, and the rows that can't load.</summary>
    private sealed record ColumnScan(
        IReadOnlyList<string> Names, IReadOnlyList<TableColumnType> Types, bool HasHeader,
        IReadOnlyList<TableReject> Rejects, long RejectCount, long NoValueRows, List<string> Reasons);

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
        IReadOnlyList<XlsxCell> headerCells = [];
        List<TableReject> rejects = [];
        long rejectCount = 0, noValueRows = 0, outside = 0, dataRows = 0;

        foreach (XlsxRow row in workbook.Rows(sheet, layout.Merges, ct))
        {
            if (row.Number < layout.HeaderRow)
                continue;
            if (row.Number > layout.LastRow)
                break;
            if (row.Number == layout.HeaderRow)
            {
                headerCells = row.Cells;
                continue;
            }
            dataRows++;
            if (row.Cells.Any(c => c.Column < layout.Left || c.Column > layout.Right))
                outside++;
            if (Check(row, layout) is { } bad)
            {
                rejectCount++;
                if (bad.Exists(r => r.Reason.StartsWith(NoValueReason, StringComparison.Ordinal)))
                    noValueRows++;
                foreach (TableReject reject in bad)
                {
                    if (rejects.Count < CsvTableReader.MaxRejects)
                        rejects.Add(reject);
                }
                continue;
            }
            foreach (XlsxCell cell in InSpan(row, layout))
                kinds[cell.Column - layout.Left].Add(cell);
        }

        List<string> reasons = [];
        bool hasHeader = headerCells.Any(c => c.Kind == XlsxCellKind.Text);
        if (!hasHeader)
        {
            reasons.Add("its first row has no text in it, so the columns were given generated names");
            if (headerCells.Any(c => c.IsBad))
                rejects.Insert(0, Check(new XlsxRow(layout.HeaderRow, headerCells), layout)![0]);
            else
                foreach (XlsxCell cell in headerCells.Where(c => c.Column >= layout.Left && c.Column <= layout.Right))
                    kinds[cell.Column - layout.Left].Add(cell);
        }

        string?[] header = new string?[width];
        if (hasHeader)
        {
            foreach (XlsxCell cell in headerCells.Where(c => c.Column >= layout.Left && c.Column <= layout.Right))
                header[cell.Column - layout.Left] = cell.Kind == XlsxCellKind.Text ? cell.Text : Display(cell);
            if (header.Any(CsvSniffer.IsNull))
                reasons.Add("some columns have no name, so they were given generated ones");
            int distinct = header.Where(h => !CsvSniffer.IsNull(h)).Select(h => h!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct < header.Count(h => !CsvSniffer.IsNull(h)))
                reasons.Add("some column names repeat, so _1, _2… were added to make them unique");
        }
        if (dataRows > 0 && outside > Math.Max(1, dataRows / 20))
            reasons.Add($"{outside} row(s) have cells outside the header's columns, which weren't loaded");

        return new ColumnScan(
            CsvSniffer.Names(hasHeader ? header : null, width), [.. kinds.Select(k => k.Type)], hasHeader,
            rejects, rejectCount + (!hasHeader && headerCells.Any(c => c.IsBad) ? 1 : 0), noValueRows, reasons);
    }

    private const string NoValueReason = "is a formula the file was saved without a result for";

    /// <summary>Why a row can't load, one entry per error or result-less formula in the table's columns; null when it loads.</summary>
    private static List<TableReject>? Check(XlsxRow row, SheetLayout layout)
    {
        List<TableReject>? rejects = null;
        foreach (XlsxCell cell in InSpan(row, layout))
        {
            if (!cell.IsBad)
                continue;
            string reason = cell.Kind == XlsxCellKind.Error ? $"is the Excel error {cell.Text}" : NoValueReason;
            (rejects ??= []).Add(new TableReject(row.Number, cell.Column - layout.Left + 1, Clip(cell.Text, CsvTableReader.MaxRejectValueChars), reason));
        }
        return rejects;
    }

    private static IEnumerable<XlsxCell> InSpan(XlsxRow row, SheetLayout layout) =>
        row.Cells.Where(c => c.Column >= layout.Left && c.Column <= layout.Right);

    private static async IAsyncEnumerable<IReadOnlyList<object?>> RowsAsync(
        XlsxWorkbook workbook, XlsxSheet sheet, SheetLayout layout, ColumnScan scan, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        int width = layout.Right - layout.Left + 1;
        foreach (XlsxRow row in workbook.Rows(sheet, layout.Merges, ct))
        {
            if (row.Number < layout.HeaderRow || (scan.HasHeader && row.Number == layout.HeaderRow))
                continue;
            if (row.Number > layout.LastRow)
                yield break;
            var values = new object?[width];
            bool any = false, bad = false;
            foreach (XlsxCell cell in InSpan(row, layout))
            {
                if (cell.IsBad)
                {
                    bad = true;
                    break;
                }
                int c = cell.Column - layout.Left;
                values[c] = Convert(cell, scan.Types[c]);
                any = true;
            }
            if (any && !bad)
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
