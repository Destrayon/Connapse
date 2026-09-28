using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Connapse.Eval.Checks.Matching;

namespace Connapse.Eval.Checks.OlmOcr;

/// <summary>
/// <c>normalize_text</c> from olmocr <c>olmocr/bench/tests.py</c> (commit f7cfe4c2; Apache-2.0, see
/// THIRD_PARTY_NOTICES.md), plus the Python formatting its explanation strings use.
/// </summary>
public static partial class OlmOcrText
{
    private static readonly (string Fancy, string Plain)[] Replacements =
    [
        ("‘", "'"), ("’", "'"), ("‚", "'"), ("“", "\""), ("”", "\""), ("„", "\""), ("＿", "_"),
        ("–", "-"), ("—", "-"), ("‑", "-"), ("‒", "-"), ("−", "-"), ("µ", "μ"),
    ];

    [GeneratedRegex("<br/?>")]
    private static partial Regex Br();

    [GeneratedRegex(@"\*\*(.*?)\*\*")]
    private static partial Regex StarBold();

    [GeneratedRegex("__(.*?)__")]
    private static partial Regex UnderscoreBold();

    [GeneratedRegex("</?b>")]
    private static partial Regex BTag();

    [GeneratedRegex("</?i>")]
    private static partial Regex ITag();

    [GeneratedRegex(@"(\*\*|__)(.*?)\1")]
    private static partial Regex PairedBold();

    [GeneratedRegex(@"(\*|_)(.*?)\1")]
    private static partial Regex PairedItalic();

    [GeneratedRegex(PyText.SpaceClass + "+")]
    private static partial Regex Whitespace();

    public static string? Normalize(string? mdContent)
    {
        if (mdContent is null)
            return null;

        mdContent = Br().Replace(mdContent, " ");
        mdContent = StarBold().Replace(mdContent, "$1");
        mdContent = UnderscoreBold().Replace(mdContent, "$1");
        mdContent = BTag().Replace(mdContent, "");
        mdContent = ITag().Replace(mdContent, "");
        mdContent = PairedBold().Replace(mdContent, "$2");
        mdContent = PairedItalic().Replace(mdContent, "$2");
        mdContent = Whitespace().Replace(mdContent, " ");
        mdContent = mdContent.Normalize(NormalizationForm.FormC);
        foreach ((string fancy, string plain) in Replacements)
            mdContent = mdContent.Replace(fancy, plain, StringComparison.Ordinal);
        return mdContent;
    }

    /// <summary>Python <c>s[:n]</c> by code point.</summary>
    public static string Head(string s, int n)
    {
        int[] points = PyText.CodePoints(s);
        return PyText.FromCodePoints(points.AsSpan(0, Math.Min(n, points.Length)));
    }

    /// <summary>Python <c>repr(float)</c>: shortest round-trip digits, always with a decimal point or exponent.</summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";
        string r = value.ToString("R", CultureInfo.InvariantCulture);
        int e = r.IndexOf('E');
        if (e < 0)
            return r.Contains('.') ? r : r + ".0";
        // .NET "1E-05" → Python "1e-05"; Python pads the exponent to two digits.
        string mantissa = r[..e];
        string exponent = r[(e + 1)..];
        string sign = exponent.StartsWith('-') ? "-" : "+";
        string digits = exponent.TrimStart('+', '-').PadLeft(2, '0');
        return $"{mantissa}e{sign}{digits}";
    }

    /// <summary>Python <c>f"{value:.3f}"</c> / <c>:.2f</c>.</summary>
    public static string Fixed(double value, int decimals) => value.ToString("F" + decimals, CultureInfo.InvariantCulture);
}
