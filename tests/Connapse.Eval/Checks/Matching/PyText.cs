using System.Globalization;
using System.Text;

namespace Connapse.Eval.Checks.Matching;

/// <summary>
/// Python <c>str</c> semantics the olmOCR ports depend on. Python indexes strings by code point, where
/// .NET indexes UTF-16 units, so the ports work on <c>int[]</c> code points. <see cref="IsSpace"/>,
/// <see cref="IsAlnum"/> and <see cref="Lower"/> follow CPython's <c>str.isspace</c>, <c>str.isalnum</c>
/// and <c>str.lower</c>.
/// </summary>
public static class PyText
{
    /// <summary>A regex character class equal to Python's <c>\s</c> on str patterns (<c>str.isspace</c>).</summary>
    public const string SpaceClass = @"[\t-\r\x1c-\x20\x85\xa0  -     　]";

    public static int[] CodePoints(string s)
    {
        List<int> points = new(s.Length);
        foreach (Rune rune in s.EnumerateRunes())
            points.Add(rune.Value);
        return [.. points];
    }

    public static string FromCodePoints(ReadOnlySpan<int> points)
    {
        StringBuilder builder = new(points.Length);
        foreach (int point in points)
            builder.Append(new Rune(point).ToString());
        return builder.ToString();
    }

    /// <summary>Length in code points, as Python's <c>len(str)</c>.</summary>
    public static int Length(string s)
    {
        int count = 0;
        foreach (Rune _ in s.EnumerateRunes())
            count++;
        return count;
    }

    public static bool IsSpace(int c) =>
        c is >= 0x09 and <= 0x0D or >= 0x1C and <= 0x20 or 0x85 or 0xA0 or 0x1680 or >= 0x2000 and <= 0x200A
            or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;

    public static bool IsAlnum(int c)
    {
        if (!Rune.IsValid(c))
            return false;
        return Rune.GetUnicodeCategory(new Rune(c)) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    /// <summary>Python's <c>str.strip()</c> with no arguments.</summary>
    public static string Strip(string s)
    {
        int[] points = CodePoints(s);
        int start = 0, end = points.Length;
        while (start < end && IsSpace(points[start]))
            start++;
        while (end > start && IsSpace(points[end - 1]))
            end--;
        return FromCodePoints(points.AsSpan(start, end - start));
    }

    /// <summary>
    /// Python's <c>str.lower()</c>: full lowercase mapping, where U+0130 becomes "i̇" and capital sigma
    /// becomes final sigma at the end of a word (Unicode's Final_Sigma condition).
    /// </summary>
    public static string Lower(string s)
    {
        int[] points = CodePoints(s);
        StringBuilder builder = new(s.Length);
        for (int i = 0; i < points.Length; i++)
        {
            int c = points[i];
            if (c == 0x130)
                builder.Append("i̇");
            else if (c == 0x3A3)
                builder.Append(IsFinalSigma(points, i) ? 'ς' : 'σ');
            else
                builder.Append(Rune.ToLowerInvariant(new Rune(c)).ToString());
        }
        return builder.ToString();
    }

    private static bool IsFinalSigma(int[] points, int index)
    {
        int j = index - 1;
        while (j >= 0 && IsCaseIgnorable(points[j]))
            j--;
        if (j < 0 || !IsCased(points[j]))
            return false;
        j = index + 1;
        while (j < points.Length && IsCaseIgnorable(points[j]))
            j++;
        return j == points.Length || !IsCased(points[j]);
    }

    private static bool IsCased(int c)
    {
        Rune rune = new(c);
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                   or UnicodeCategory.TitlecaseLetter
               || Rune.ToLowerInvariant(rune) != rune || Rune.ToUpperInvariant(rune) != rune;
    }

    private static bool IsCaseIgnorable(int c) =>
        c is '\'' or '.' or ':' or '^' or '`' or 0xB7 or 0x2018 or 0x2019 or 0x2024 or 0x2027
        || Rune.GetUnicodeCategory(new Rune(c)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol;
}
