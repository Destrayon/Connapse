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
        ".txt", ".md", ".markdown", ".csv", ".log", ".json", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> ZipOfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".pptx",
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
                ? $"its content is {actual}, not a {extension.TrimStart('.').ToUpperInvariant()} file"
                : $"its content is not a {extension.TrimStart('.').ToUpperInvariant()} file";
        }

        if (TextExtensions.Contains(extension))
        {
            // Text has no signature to require, so the check runs the other way: content that
            // starts like a known binary format is not text, whatever it is called.
            return Identify(head) is { } actual
                ? $"its content is {actual}, not text"
                : null;
        }

        return null;
    }
}
