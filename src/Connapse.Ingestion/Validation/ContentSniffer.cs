namespace Connapse.Ingestion.Validation;

/// <summary>
/// Checks a file's leading bytes against what its extension claims.
/// <para>
/// Parsers are chosen by extension alone, so a PNG saved as <c>.txt</c> was indexed as a page of
/// mojibake, and a PDF saved as <c>.docx</c> failed with whatever the Office reader happened to
/// throw. Catching the contradiction first turns both into one permanent failure that says what
/// the file actually is.
/// </para>
/// </summary>
public static class ContentSniffer
{
    /// <summary>How many leading bytes the checks need.</summary>
    public const int HeaderLength = 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log", ".json", ".xml", ".yaml", ".yml", ".html", ".htm", ".eml",
    };

    private static readonly HashSet<string> ZipOfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".pptx", ".epub",
    };

    private static ReadOnlySpan<byte> PdfMagic => "%PDF-"u8;
    private static ReadOnlySpan<byte> ZipMagic => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> EmptyZipMagic => [0x50, 0x4B, 0x05, 0x06];
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> GifMagic => "GIF8"u8;
    private static ReadOnlySpan<byte> TiffLittleMagic => [0x49, 0x49, 0x2A, 0x00];
    private static ReadOnlySpan<byte> TiffBigMagic => [0x4D, 0x4D, 0x00, 0x2A];
    private static ReadOnlySpan<byte> OleMagic => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static ReadOnlySpan<byte> GzipMagic => [0x1F, 0x8B];

    /// <summary>
    /// Names the format the bytes start with, or null when they carry no signature this class
    /// knows. Plain text has no signature, so null is the expected answer for it.
    /// </summary>
    public static string? Identify(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PdfMagic)) return "a PDF document";
        if (head.StartsWith(ZipMagic) || head.StartsWith(EmptyZipMagic)) return "a ZIP archive";
        if (head.StartsWith(PngMagic)) return "a PNG image";
        if (head.StartsWith(JpegMagic)) return "a JPEG image";
        if (head.StartsWith(GifMagic)) return "a GIF image";
        if (head.StartsWith(TiffLittleMagic) || head.StartsWith(TiffBigMagic)) return "a TIFF image";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return "a WebP image";
        if (head.StartsWith(OleMagic)) return "a legacy Office or Outlook file";
        if (head.StartsWith(GzipMagic)) return "a gzip archive";
        return null;
    }

    /// <summary>
    /// Explains how the bytes contradict the extension, or returns null when they are consistent
    /// with it. Extensions this class has no rule for are always consistent.
    /// </summary>
    public static string? DescribeMismatch(ReadOnlySpan<byte> head, string extension)
    {
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (head.IsEmpty) return "the file is empty";

            // The PDF specification tolerates leading junk before the header, and real files
            // carry some, so the header is searched for rather than required at offset zero.
            if (head.IndexOf(PdfMagic) >= 0) return null;

            return Identify(head) is { } actual
                ? $"its content is {actual}, not a PDF"
                : "its content has no PDF header";
        }

        if (ZipOfficeExtensions.Contains(extension))
        {
            if (head.IsEmpty) return "the file is empty";

            // DOCX and PPTX are ZIP packages; anything else cannot be one.
            if (head.StartsWith(ZipMagic)) return null;

            return Identify(head) is { } actual
                ? $"its content is {actual}, not {Format(extension)}"
                : $"its content is not {Format(extension)}";
        }

        if (extension.Equals(".msg", StringComparison.OrdinalIgnoreCase))
        {
            if (head.IsEmpty) return "the file is empty";

            // An Outlook message is an OLE compound file.
            if (head.StartsWith(OleMagic)) return null;

            return Identify(head) is { } actual
                ? $"its content is {actual}, not an Outlook message"
                : "its content is not an Outlook message";
        }

        if (TextExtensions.Contains(extension))
        {
            // Text has no signature to require, so the check runs the other way: content that
            // starts like a known binary format is not text, whatever it is called. A signature is
            // only a few printable bytes, though -- a note that opens with "GIF89a" is still a
            // note -- so the header must also carry the control bytes no text file contains.
            return Identify(head) is { } actual && HasBinaryControlBytes(head)
                ? $"its content is {actual}, not text"
                : null;
        }

        return null;
    }

    /// <summary>"a DOCX file", "an EPUB file".</summary>
    private static string Format(string extension)
    {
        string name = extension.TrimStart('.').ToUpperInvariant();
        return (name.Length > 0 && "AEIOU".Contains(name[0]) ? "an " : "a ") + name + " file";
    }

    /// <summary>
    /// True when the bytes include C0 control characters that text never uses. Tab, line feed,
    /// form feed, carriage return and escape are allowed; every format <see cref="Identify"/> knows
    /// carries other control bytes (NUL, 0x1A, length fields) within its first few dozen bytes.
    /// </summary>
    private static bool HasBinaryControlBytes(ReadOnlySpan<byte> head)
    {
        foreach (byte b in head)
        {
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or 0x1B))
                return true;
        }

        return false;
    }
}
