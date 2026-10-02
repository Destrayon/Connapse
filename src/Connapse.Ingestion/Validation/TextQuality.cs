using System.Globalization;
using System.Text;
using Connapse.Core;

namespace Connapse.Ingestion.Validation;

/// <summary>
/// Judges whether text extracted from a PDF or Office file is readable.
/// <para>
/// A PDF whose fonts carry no usable Unicode mapping extracts as private-use glyphs, replacement
/// characters, controls and NUL. Before #595 such a document was indexed as Ready: searchable,
/// and matching nothing anyone types. Measured on the 1,220 olmOCR-bench PDFs with text
/// (2026-10-01), the median document has no such characters and the 99th percentile 4%; four
/// documents had 50-88%, with 3-20% letters and digits, and their text was unreadable.
/// </para>
/// <para>
/// Measured on the parser's raw output, before NUL and broken surrogates are cleaned away for
/// storage: cleaning first would hide exactly the damage this exists to see.
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

    /// <summary>
    /// Counts Unicode scalar values, not UTF-16 units: a mathematical letter or an emoji is one
    /// character, not two halves of nothing. Pictographs count as content alongside letters and
    /// digits, so a slide of icons is not mistaken for junk.
    /// </summary>
    public static Report Measure(string text)
    {
        int nonWhitespace = 0, suspicious = 0, content = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            nonWhitespace++;

            // EnumerateRunes yields U+FFFD for an unpaired surrogate, so broken pairs count here.
            if (IsSuspicious(rune))
            {
                suspicious++;
                continue;
            }

            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol)
                content++;
        }

        return nonWhitespace == 0
            ? new Report(0, 0, 0)
            : new Report(nonWhitespace, (double)suspicious / nonWhitespace, (double)content / nonWhitespace);
    }

    private static bool IsSuspicious(Rune rune) =>
        rune.Value == 0xFFFD
        || Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) == UnicodeCategory.PrivateUse;

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
            return $"only {report.AlphanumericRatio:P0} of its text is letters, digits or symbols [garbled_text]";

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
