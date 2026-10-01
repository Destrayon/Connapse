using Connapse.Core;

namespace Connapse.Ingestion.Validation;

/// <summary>
/// Judges whether text extracted from a PDF or Office file is readable.
/// <para>
/// A PDF whose fonts carry no usable Unicode mapping extracts as private-use glyphs, replacement
/// characters and controls. Before #595 such a document was indexed as Ready: searchable, and
/// matching nothing anyone types. Measured on the 1,220 olmOCR-bench PDFs with text (2026-10-01),
/// the median document has no such characters and the 99th percentile 4%; four documents had
/// 50-88%, with 3-20% letters and digits, and their text was unreadable.
/// </para>
/// </summary>
public static class TextQuality
{
    /// <summary>Formats whose text comes out of glyph mappings rather than from bytes Connapse decodes.</summary>
    private static readonly HashSet<string> CheckedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".pptx",
    };

    public sealed record Report(int NonWhitespace, double SuspiciousRatio, double AlphanumericRatio);

    public static Report Measure(string text)
    {
        int nonWhitespace = 0, suspicious = 0, alphanumeric = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            nonWhitespace++;
            if (c == '�' || c is >= '' and <= '' || char.IsControl(c)) suspicious++;
            if (char.IsLetterOrDigit(c)) alphanumeric++;
        }

        return nonWhitespace == 0
            ? new Report(0, 0, 0)
            : new Report(nonWhitespace, (double)suspicious / nonWhitespace, (double)alphanumeric / nonWhitespace);
    }

    /// <summary>
    /// Explains why the text is unreadable, or returns null when it is fine or too short to judge.
    /// </summary>
    public static string? DescribeGarbled(string extension, Report report, UploadSettings limits)
    {
        if (!CheckedExtensions.Contains(extension) || report.NonWhitespace < limits.MinCharactersForQualityCheck)
            return null;

        if (report.SuspiciousRatio >= limits.GarbledSuspiciousRatio)
            return $"{report.SuspiciousRatio:P0} of its text is unreadable glyphs or replacement characters [garbled_text]";

        if (report.AlphanumericRatio < limits.GarbledMinAlphanumericRatio)
            return $"only {report.AlphanumericRatio:P0} of its text is letters or digits [garbled_text]";

        return null;
    }

    /// <summary>A warning for text that is partly unreadable but still worth indexing.</summary>
    public static string? DescribePartlyGarbled(string extension, Report report, UploadSettings limits) =>
        CheckedExtensions.Contains(extension)
        && report.NonWhitespace >= limits.MinCharactersForQualityCheck
        && report.SuspiciousRatio >= limits.WarnSuspiciousRatio
            ? $"{report.SuspiciousRatio:P0} of the extracted text is unreadable glyphs or replacement characters"
            : null;
}
