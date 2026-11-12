using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Connapse.Ingestion.Tables;

/// <summary>What a cell holds once read: the kinds a sheet's values come in.</summary>
internal enum XlsxCellKind
{
    Text,
    Number,
    Boolean,
    Date,

    /// <summary>An Excel error value such as #N/A or #DIV/0!; <see cref="XlsxCell.Text"/> holds it.</summary>
    Error,

    /// <summary>A formula the file was saved without a result for, so its value is unknown, not empty (CON-308).</summary>
    NoValue,
}

/// <summary>One non-empty cell: its 0-based column, its kind, and its value as text and, for numbers and dates, as a value.</summary>
internal readonly record struct XlsxCell(int Column, XlsxCellKind Kind, string Text, double Number = 0, DateTime Date = default)
{
    public bool IsBad => Kind is XlsxCellKind.Error or XlsxCellKind.NoValue;
}

/// <summary>A row of a sheet: its 1-based number and its non-empty cells in column order.</summary>
internal sealed record XlsxRow(long Number, IReadOnlyList<XlsxCell> Cells);

/// <summary>A merged range, 1-based rows and 0-based columns, inclusive.</summary>
internal readonly record struct XlsxMerge(long Top, int Left, long Bottom, int Right)
{
    public bool Contains(long row, int column) => row >= Top && row <= Bottom && column >= Left && column <= Right;
}

/// <summary>A worksheet of the workbook, in tab order.</summary>
internal sealed record XlsxSheet(string Name, bool Hidden, WorksheetPart Part);

/// <summary>
/// An .xlsx workbook read a row at a time with the Open XML SDK's streaming reader (CON-308), so a sheet is never held
/// in memory whole and can be read again as often as a load needs. The workbook's shared strings and cell styles are
/// read once up front. Each XML part is capped at <see cref="MaxCharactersInPart"/>; the SDK refuses DTDs itself.
/// </summary>
internal sealed class XlsxWorkbook : IDisposable
{
    /// <summary>The most characters one XML part (a sheet, the shared strings) may hold before the file is refused.</summary>
    public const long MaxCharactersInPart = 1L << 30;

    /// <summary>Excel's own limits: 1,048,576 rows and 16,384 columns.</summary>
    public const long MaxRows = 1_048_576;

    public const int MaxColumns = 16_384;

    /// <summary>Built-in number formats that show a date or a date and time (ECMA-376 18.8.30, calamine, xlrd).</summary>
    private static readonly HashSet<uint> BuiltInDateFormats =
        [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58];

    private static readonly DateTime Epoch1900 = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private readonly SpreadsheetDocument _document;
    private readonly List<string> _strings;
    private readonly bool[] _dateStyles;
    private readonly bool _date1904;

    private XlsxWorkbook(SpreadsheetDocument document, List<string> strings, bool[] dateStyles, bool date1904, IReadOnlyList<XlsxSheet> sheets)
    {
        _document = document;
        _strings = strings;
        _dateStyles = dateStyles;
        _date1904 = date1904;
        Sheets = sheets;
    }

    /// <summary>The workbook's worksheets in tab order; chart sheets and dialog sheets are left out.</summary>
    public IReadOnlyList<XlsxSheet> Sheets { get; }

    /// <summary>Opens <paramref name="file"/>, which must stay open and unread by anyone else until this is disposed.</summary>
    /// <exception cref="InvalidDataException">The file isn't a workbook.</exception>
    public static XlsxWorkbook Open(Stream file, CancellationToken ct = default)
    {
        file.Position = 0;
        SpreadsheetDocument document = SpreadsheetDocument.Open(file, false, new OpenSettings { MaxCharactersInPart = MaxCharactersInPart });
        try
        {
            WorkbookPart part = document.WorkbookPart ?? throw new InvalidDataException("The file has no workbook.");
            Workbook workbook = part.Workbook ?? throw new InvalidDataException("The file has no workbook.");
            bool date1904 = workbook.WorkbookProperties?.Date1904?.Value == true;

            List<XlsxSheet> sheets = [];
            foreach (Sheet sheet in workbook.Sheets?.Elements<Sheet>() ?? [])
            {
                if (sheet.Id?.Value is not { } id || part.GetPartById(id) is not WorksheetPart worksheet)
                    continue;
                bool hidden = sheet.State?.Value is { } state && state != SheetStateValues.Visible;
                sheets.Add(new XlsxSheet(sheet.Name?.Value ?? $"Sheet{sheets.Count + 1}", hidden, worksheet));
            }

            List<string> strings = ReadSharedStrings(part.SharedStringTablePart, ct);
            bool[] dateStyles = ReadDateStyles(part.WorkbookStylesPart?.Stylesheet);
            return new XlsxWorkbook(document, strings, dateStyles, date1904, sheets);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public void Dispose() => _document.Dispose();

    /// <summary>
    /// The sheet's merged ranges. They are written after every row, so this reads past the rows without loading them.
    /// </summary>
    public IReadOnlyList<XlsxMerge> Merges(XlsxSheet sheet, CancellationToken ct = default)
    {
        List<XlsxMerge> merges = [];
        using OpenXmlReader reader = OpenXmlReader.Create(sheet.Part);
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.ElementType == typeof(SheetData) && reader.IsStartElement)
            {
                reader.Skip();
                continue;
            }
            if (reader.ElementType != typeof(MergeCell) || !reader.IsStartElement)
                continue;
            string? range = reader.Attributes.FirstOrDefault(a => a.LocalName == "ref").Value;
            if (range is null)
                continue;
            string[] ends = range.Split(':');
            if (!TryParseReference(ends[0], out long top, out int left)
                || !TryParseReference(ends[^1], out long bottom, out int right))
                continue;
            merges.Add(new XlsxMerge(Math.Min(top, bottom), Math.Min(left, right), Math.Max(top, bottom), Math.Max(left, right)));
        }
        return merges;
    }

    /// <summary>
    /// The sheet's non-empty rows in order, read afresh on each call. With <paramref name="merges"/>, every empty cell
    /// inside a merged range takes the value of the range's top-left cell, as Excel shows it.
    /// </summary>
    public IEnumerable<XlsxRow> Rows(XlsxSheet sheet, IReadOnlyList<XlsxMerge>? merges = null, CancellationToken ct = default)
    {
        List<XlsxMerge> pending = merges is null ? [] : [.. merges.OrderBy(m => m.Top)];
        int next = 0;
        List<XlsxMerge> active = [];
        var topLeft = new Dictionary<XlsxMerge, XlsxCell>();

        using OpenXmlReader reader = OpenXmlReader.Create(sheet.Part);
        long rowNumber = 0;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                continue;
            if ((rowNumber & 0xFF) == 0)
                ct.ThrowIfCancellationRequested();

            var row = (Row)reader.LoadCurrentElement()!;
            long number = row.RowIndex?.Value is uint r && r > rowNumber ? r : rowNumber + 1;
            if (number > MaxRows)
                throw new InvalidDataException($"The sheet {sheet.Name} has a row past Excel's last row, {MaxRows}.");
            rowNumber = number;

            List<XlsxCell> cells = ReadCells(row);
            if (pending.Count > 0 || active.Count > 0)
            {
                active.RemoveAll(m => m.Bottom < number);
                while (next < pending.Count && pending[next].Top <= number)
                {
                    if (pending[next].Bottom >= number)
                        active.Add(pending[next]);
                    next++;
                }
                if (active.Count > 0)
                    cells = Fill(number, cells, active, topLeft);
            }
            if (cells.Count > 0)
                yield return new XlsxRow(number, cells);
        }
    }

    private List<XlsxCell> ReadCells(Row row)
    {
        List<XlsxCell> cells = [];
        int column = -1;
        foreach (Cell cell in row.Elements<Cell>())
        {
            column = cell.CellReference?.Value is { } reference && TryParseReference(reference, out _, out int c) ? c : column + 1;
            if (column >= MaxColumns)
                throw new InvalidDataException($"A cell is past Excel's last column, {MaxColumns}.");
            if (Read(cell, column) is { } value)
                cells.Add(value);
        }
        cells.Sort((a, b) => a.Column.CompareTo(b.Column));
        return cells;
    }

    private static List<XlsxCell> Fill(long row, List<XlsxCell> cells, List<XlsxMerge> active, Dictionary<XlsxMerge, XlsxCell> topLeft)
    {
        bool changed = false;
        foreach (XlsxMerge merge in active)
        {
            if (merge.Top == row)
            {
                int at = cells.FindIndex(c => c.Column == merge.Left);
                if (at >= 0)
                    topLeft[merge] = cells[at];
            }
            if (!topLeft.TryGetValue(merge, out XlsxCell source))
                continue;
            for (int c = merge.Left; c <= merge.Right; c++)
            {
                if ((row == merge.Top && c == merge.Left) || cells.Exists(x => x.Column == c))
                    continue;
                cells.Add(source with { Column = c });
                changed = true;
            }
        }
        if (changed)
            cells.Sort((a, b) => a.Column.CompareTo(b.Column));
        return cells;
    }

    /// <summary>A cell's value, or null when it is empty. Follows openpyxl and calamine on each <c>t</c> type.</summary>
    private XlsxCell? Read(Cell cell, int column)
    {
        bool formula = cell.CellFormula is not null;
        CellValues type = cell.DataType?.Value ?? CellValues.Number;
        string? raw = cell.CellValue?.Text;

        if (type == CellValues.InlineString)
            return TextCell(column, cell.InlineString is { } inline ? JoinText(inline) : raw);

        // A formula whose result wasn't saved: <f> with no <v>, or an empty one, as openpyxl writes it. A string
        // formula's empty <v> is a real empty result.
        if (formula && string.IsNullOrEmpty(raw) && !(type == CellValues.String && cell.CellValue is not null))
            return new XlsxCell(column, XlsxCellKind.NoValue, cell.CellFormula!.Text is { Length: > 0 } f ? "=" + f : "=");
        if (string.IsNullOrEmpty(raw))
            return null;

        if (type == CellValues.SharedString)
        {
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= _strings.Count)
                throw new InvalidDataException($"A cell points at shared string {raw}, which the workbook doesn't have.");
            return TextCell(column, _strings[index]);
        }
        if (type == CellValues.Boolean)
            return new XlsxCell(column, XlsxCellKind.Boolean, raw.Trim() is "1" or "true" ? "TRUE" : "FALSE");
        if (type == CellValues.Error)
            return new XlsxCell(column, XlsxCellKind.Error, raw);
        if (type == CellValues.String)
            return TextCell(column, raw);
        if (type == CellValues.Date)
        {
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime iso)
                ? new XlsxCell(column, XlsxCellKind.Date, raw, Date: DateTime.SpecifyKind(iso, DateTimeKind.Unspecified))
                : TextCell(column, raw);
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            return TextCell(column, raw);
        uint style = cell.StyleIndex?.Value ?? 0;
        if (style < _dateStyles.Length && _dateStyles[style] && ToDate(number) is DateTime date)
            return new XlsxCell(column, XlsxCellKind.Date, raw, number, date);
        return new XlsxCell(column, XlsxCellKind.Number, raw, number);
    }

    private static XlsxCell? TextCell(int column, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new XlsxCell(column, XlsxCellKind.Text, text);

    /// <summary>
    /// An Excel date serial as a date and time, to the millisecond. The 1900 system counts from 1899-12-30, a day
    /// earlier below serial 61 to step over the 29 February 1900 that Excel wrongly keeps. Null outside Excel's range.
    /// </summary>
    internal DateTime? ToDate(double serial)
    {
        if (serial < 0 || serial >= 2958466)
            return null;
        if (!_date1904 && serial < 61)
        {
            if (serial >= 60)
                return null;
            serial += serial >= 1 ? 1 : 0;
        }
        double ms = Math.Round(serial * 86_400_000d);
        DateTime date = (_date1904 ? Epoch1904 : Epoch1900).AddMilliseconds(ms);
        return date.Year > 9999 ? null : date;
    }

    private static string JoinText(OpenXmlElement item)
    {
        // A shared or inline string is its own <t>, then each run's <t>; phonetic runs (<rPh>) are not the text.
        var text = new StringBuilder();
        foreach (OpenXmlElement child in item.ChildElements)
        {
            if (child is Text t)
                text.Append(t.Text);
            else if (child is Run run)
                foreach (Text rt in run.Elements<Text>())
                    text.Append(rt.Text);
        }
        return text.ToString();
    }

    private static List<string> ReadSharedStrings(SharedStringTablePart? part, CancellationToken ct)
    {
        List<string> strings = [];
        if (part is null)
            return strings;
        using OpenXmlReader reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement)
                continue;
            if ((strings.Count & 0xFFF) == 0)
                ct.ThrowIfCancellationRequested();
            strings.Add(JoinText(reader.LoadCurrentElement()!));
        }
        return strings;
    }

    /// <summary>For each cell style, whether its number format shows a date.</summary>
    private static bool[] ReadDateStyles(Stylesheet? styles)
    {
        if (styles?.CellFormats is not { } formats)
            return [];
        var custom = new Dictionary<uint, bool>();
        foreach (NumberingFormat format in styles.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is uint id)
                custom[id] = IsDateFormat(format.FormatCode?.Value);
        }
        return [.. formats.Elements<CellFormat>().Select(f =>
            f.NumberFormatId?.Value is uint id && (custom.TryGetValue(id, out bool isDate) ? isDate : BuiltInDateFormats.Contains(id)))];
    }

    /// <summary>
    /// Whether a custom number format shows a date or time, by calamine's rule: in its first section, outside quoted
    /// text, brackets and escaped characters, it has a d, m, y, h or s. A [h], [m] or [s] format is a duration, not a
    /// date, and stays a number.
    /// </summary>
    internal static bool IsDateFormat(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return false;
        bool quoted = false;
        for (int i = 0; i < code.Length; i++)
        {
            char ch = code[i];
            if (quoted)
            {
                quoted = ch != '"';
                continue;
            }
            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case '\\' or '_' or '*':
                    i++;
                    break;
                case ';':
                    return false;
                case '[':
                    int close = code.IndexOf(']', i);
                    if (close < 0)
                        return false;
                    string inside = code[(i + 1)..close].ToLowerInvariant();
                    if (inside.Length > 0 && inside.All(c => c is 'h' or 'm' or 's'))
                        return false;
                    i = close;
                    break;
                default:
                    if (char.ToLowerInvariant(ch) is 'd' or 'm' or 'y' or 'h' or 's')
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>Reads an A1 reference: 1-based row and 0-based column. The row is 0 when the reference has none.</summary>
    internal static bool TryParseReference(string reference, out long row, out int column)
    {
        row = 0;
        column = -1;
        int i = 0;
        long col = 0;
        while (i < reference.Length && char.IsAsciiLetter(reference[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            if (col > MaxColumns)
                return false;
            i++;
        }
        if (i == 0)
            return false;
        column = (int)col - 1;
        if (i < reference.Length && !long.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row))
            return false;
        return row <= MaxRows;
    }
}
