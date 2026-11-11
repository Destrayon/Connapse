using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Connapse.Ingestion.Tests.Tables;

/// <summary>Builds small Word, PowerPoint and Excel files holding tables, for the CON-312 table tests.</summary>
internal static class TestOffice
{
    /// <summary>A Word document: a paragraph, then each table, each followed by a paragraph.</summary>
    public static byte[] Docx(params string[][][] tables)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart();
            var body = new W.Body(Para("Quarterly report."));
            foreach (string[][] rows in tables)
            {
                body.Append(new W.Table(rows.Select(r => new W.TableRow(r.Select(c => new W.TableCell(Para(c)))))));
                body.Append(Para("Text after the table."));
            }
            main.Document = new W.Document(body);
            main.Document.Save();
        }
        return stream.ToArray();

        static W.Paragraph Para(string text) =>
            new(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    /// <summary>A deck with one slide per table.</summary>
    public static byte[] Pptx(params string[][][] tables)
    {
        uint ids = 2;
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = document.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation(new P.SlideIdList());
            uint slideId = 256;
            foreach (string[][] rows in tables)
            {
                var table = new A.Table(new A.TableProperties(), new A.TableGrid());
                foreach (string[] row in rows)
                {
                    var tableRow = new A.TableRow { Height = 370840 };
                    foreach (string text in row)
                        tableRow.Append(new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(),
                            new A.Paragraph(new A.Run(new A.Text(text)))), new A.TableCellProperties()));
                    table.Append(tableRow);
                }
                var frame = new P.GraphicFrame(
                    new P.NonVisualGraphicFrameProperties(
                        new P.NonVisualDrawingProperties { Id = ids++, Name = "t" },
                        new P.NonVisualGraphicFrameDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.Transform(),
                    new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));

                var slidePart = presentationPart.AddNewPart<SlidePart>();
                var tree = new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()),
                    frame);
                slidePart.Slide = new P.Slide(new P.CommonSlideData(tree));
                presentationPart.Presentation.SlideIdList!.Append(
                    new P.SlideId { Id = slideId++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }
            presentationPart.Presentation.Save();
        }
        return stream.ToArray();
    }

    /// <summary>A workbook with one sheet per entry; each row is a list of values, null for an empty cell.</summary>
    public static byte[] Xlsx(params (string Name, object?[][] Rows, bool Hidden)[] sheets)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook(new S.Sheets());

            // Style 0 is plain; style 1 shows a date (built-in format 14).
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = new S.Stylesheet(
                new S.Fonts(new S.Font()) { Count = 1 },
                new S.Fills(new S.Fill()) { Count = 1 },
                new S.Borders(new S.Border()) { Count = 1 },
                new S.CellFormats(
                    new S.CellFormat { NumberFormatId = 0 },
                    new S.CellFormat { NumberFormatId = 14, ApplyNumberFormat = true })
                { Count = 2 });
            styles.Stylesheet.Save();

            uint sheetId = 1;
            foreach ((string name, object?[][] rows, bool hidden) in sheets)
            {
                var part = workbook.AddNewPart<WorksheetPart>();
                var data = new S.SheetData();
                for (int r = 0; r < rows.Length; r++)
                {
                    var row = new S.Row { RowIndex = (uint)(r + 1) };
                    for (int c = 0; c < rows[r].Length; c++)
                    {
                        object? value = rows[r][c];
                        if (value is null)
                            continue;
                        string reference = (char)('A' + c) + (r + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        row.Append(value switch
                        {
                            bool b => new S.Cell { CellReference = reference, DataType = S.CellValues.Boolean, CellValue = new S.CellValue(b ? "1" : "0") },
                            DateTime d => new S.Cell { CellReference = reference, StyleIndex = 1, CellValue = new S.CellValue(d.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)) },
                            int or long or double or decimal => new S.Cell { CellReference = reference, CellValue = new S.CellValue(System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!) },
                            _ => new S.Cell { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(value.ToString()!)) },
                        });
                    }
                    data.Append(row);
                }
                part.Worksheet = new S.Worksheet(data);
                part.Worksheet.Save();
                var sheet = new S.Sheet { Id = workbook.GetIdOfPart(part), SheetId = sheetId++, Name = name };
                if (hidden)
                    sheet.State = S.SheetStateValues.Hidden;
                workbook.Workbook.Sheets!.Append(sheet);
            }
            workbook.Workbook.Save();
        }
        return stream.ToArray();
    }
}
