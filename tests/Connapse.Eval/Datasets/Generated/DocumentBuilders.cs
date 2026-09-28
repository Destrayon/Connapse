using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Connapse.Eval.Datasets.Generated;

/// <summary>
/// Builds small documents in code, with no external tools, so generated datasets and tests are
/// reproducible from source. Every builder is deterministic: package timestamps are fixed.
/// </summary>
public static class DocumentBuilders
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A DOCX with one paragraph per string.</summary>
    public static byte[] Docx(params string[] paragraphs)
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            document.PackageProperties.Created = FixedTime;
            document.PackageProperties.Modified = FixedTime;
            MainDocumentPart main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(paragraphs.Select(p => new Paragraph(new Run(new Text(p)))).ToArray()));
            main.Document.Save();
        }
        return Repack(stream.ToArray());
    }

    /// <summary>A DOCX whose only content is one inline PNG picture.</summary>
    public static byte[] ImageOnlyDocx(byte[] png)
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            document.PackageProperties.Created = FixedTime;
            document.PackageProperties.Modified = FixedTime;
            MainDocumentPart main = document.AddMainDocumentPart();
            ImagePart image = main.AddImagePart(ImagePartType.Png, "rIdImage1");
            using (MemoryStream pngStream = new(png))
                image.FeedData(pngStream);

            const long Cx = 990000, Cy = 495000;
            Drawing drawing = new(new DW.Inline(
                new DW.Extent { Cx = Cx, Cy = Cy },
                new DW.DocProperties { Id = 1U, Name = "Picture 1" },
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = "image.png" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = "rIdImage1" }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = Cx, Cy = Cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
            main.Document = new Document(new Body(new Paragraph(new Run(drawing))));
            main.Document.Save();
        }
        return Repack(stream.ToArray());
    }

    /// <summary>
    /// A DOCX whose <c>word/document.xml</c> inflates to roughly <paramref name="inflatedBytes"/> of
    /// repeated text while the file stays small: a zip bomb in a format Connapse accepts.
    /// </summary>
    public static byte[] ZipBombDocx(long inflatedBytes)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            WriteEntry(zip, "_rels/.rels",
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");

            ZipArchiveEntry entry = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            entry.LastWriteTime = FixedTime;
            using Stream body = entry.Open();
            body.Write(Encoding.UTF8.GetBytes(
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>"""));
            byte[] block = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("all work and no play ", 3200)));
            for (long written = 0; written < inflatedBytes; written += block.Length)
                body.Write(block);
            body.Write(Encoding.UTF8.GetBytes("</w:t></w:r></w:p></w:body></w:document>"));
        }
        return stream.ToArray();
    }

    /// <summary>A PDF with one page per string, each drawn as lines of Helvetica text.</summary>
    public static byte[] TextPdf(params string[] pages)
    {
        PdfDocumentBuilder builder = new();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (string pageText in pages)
        {
            PdfPageBuilder page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            double y = 780;
            foreach (string line in pageText.Split('\n'))
            {
                page.AddText(line, 11, new PdfPoint(50, y), font);
                y -= 16;
            }
        }
        return builder.Build();
    }

    /// <summary>A one-page PDF containing only a picture: no text layer, like a scan.</summary>
    public static byte[] ImageOnlyPdf(byte[] png)
    {
        PdfDocumentBuilder builder = new();
        PdfPageBuilder page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddPng(png, new PdfRectangle(50, 500, 450, 700));
        return builder.Build();
    }

    /// <summary>A deterministic grayscale PNG with horizontal stripes that look like lines of text.</summary>
    public static byte[] StripedPng(int width = 400, int height = 200)
    {
        using MemoryStream raw = new();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            bool ink = y % 20 is >= 6 and < 12;
            for (int x = 0; x < width; x++)
                raw.WriteByte((byte)(ink && x % 37 < 30 ? 30 : 245));
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // grayscale
        WriteChunk(png, "IHDR", header);
        using (MemoryStream compressed = new())
        {
            using (ZLibStream zlib = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                raw.WriteTo(zlib);
            WriteChunk(png, "IDAT", compressed.ToArray());
        }
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        byte[] checksum = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        png.Write(checksum);
    }

    // CRC-32 (ISO-HDLC, reflected polynomial 0xEDB88320) as PNG chunks require; state is pre- and post-inverted by the caller.
    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTime;
        using Stream stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    // OpenXml stamps zip entries with the current time; rewriting them with a fixed time makes the
    // package bytes depend only on its content.
    private static byte[] Repack(byte[] package)
    {
        using MemoryStream input = new(package);
        using ZipArchive source = new(input, ZipArchiveMode.Read);
        using MemoryStream output = new();
        using (ZipArchive target = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in source.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                ZipArchiveEntry copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = FixedTime;
                using Stream from = entry.Open();
                using Stream to = copy.Open();
                from.CopyTo(to);
            }
        }
        return output.ToArray();
    }
}
