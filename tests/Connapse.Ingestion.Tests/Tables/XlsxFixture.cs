using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Connapse.Ingestion.Tests.Tables;

/// <summary>
/// Builds small .xlsx files from worksheet XML, so each test shows exactly the cells it reads. Style 1 is the built-in
/// date format (14) and style 2 a custom date-and-time format.
/// </summary>
internal static class XlsxFixture
{
    public sealed record SheetSpec(string Name, string Xml, bool Hidden = false);

    public static MemoryStream Build(params SheetSpec[] sheets) => Build(sharedStrings: [], sheets);

    public static MemoryStream Build(string[] sharedStrings, params SheetSpec[] sheets)
    {
        var stream = new MemoryStream();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            WorkbookPart workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var list = new Sheets();
            uint id = 1;
            foreach (SheetSpec spec in sheets)
            {
                WorksheetPart part = workbook.AddNewPart<WorksheetPart>();
                part.Worksheet = new Worksheet($"<x:worksheet xmlns:x=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">{Prefix(spec.Xml)}</x:worksheet>");
                var sheet = new Sheet { Name = spec.Name, SheetId = id++, Id = workbook.GetIdOfPart(part) };
                if (spec.Hidden)
                    sheet.State = SheetStateValues.Hidden;
                list.Append(sheet);
            }
            workbook.Workbook.Append(list);

            WorkbookStylesPart styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new Stylesheet(
                new NumberingFormats(new NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy-mm-dd hh:mm" }) { Count = 1 },
                new Fonts(new Font()) { Count = 1 },
                new Fills(new Fill()) { Count = 1 },
                new Borders(new Border()) { Count = 1 },
                new CellFormats(
                    new CellFormat { NumberFormatId = 0 },
                    new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
                    new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true })
                { Count = 3 });

            if (sharedStrings.Length > 0)
            {
                SharedStringTablePart strings = workbook.AddNewPart<SharedStringTablePart>();
                strings.SharedStringTable = new SharedStringTable(sharedStrings.Select(s => new SharedStringItem(new Text(s))));
            }
        }
        stream.Position = 0;
        return stream;
    }

    /// <summary>Puts the x: prefix on every element, so test XML can be written without one.</summary>
    private static string Prefix(string xml) =>
        System.Text.RegularExpressions.Regex.Replace(xml, @"<(/?)([a-zA-Z]+)", "<$1x:$2");

    /// <summary>A row of inline-string and number cells from A onward; a null leaves the cell out.</summary>
    public static string Row(int number, params object?[] values)
    {
        var cells = new System.Text.StringBuilder();
        for (int i = 0; i < values.Length; i++)
        {
            string at = $"{Column(i)}{number}";
            cells.Append(values[i] switch
            {
                null => "",
                string s => $"<c r=\"{at}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(s)}</t></is></c>",
                bool b => $"<c r=\"{at}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>",
                DateOnly d => $"<c r=\"{at}\" s=\"1\"><v>{d.ToDateTime(TimeOnly.MinValue).ToOADate()}</v></c>",
                IFormattable n => $"<c r=\"{at}\"><v>{n.ToString(null, System.Globalization.CultureInfo.InvariantCulture)}</v></c>",
                _ => throw new ArgumentException(values[i]!.GetType().Name),
            });
        }
        return $"<row r=\"{number}\">{cells}</row>";
    }

    public static string Column(int index)
    {
        string name = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}
