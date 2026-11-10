using System.Globalization;
using Connapse.Core;

namespace Connapse.Ingestion.Tables;

/// <summary>
/// One way a column's text can be read as a typed value: a type plus the exact format that reads it, such as a
/// day-first date or a comma decimal. The format is saved in the table's Frictionless schema, so an agent can see how
/// the file wrote its values.
/// </summary>
internal sealed class ValueReader(TableColumnType type, TableColumnFormat? format, ValueReader.ReadFunc read)
{
    internal delegate bool ReadFunc(string text, out object? value);

    public TableColumnType Type { get; } = type;

    public TableColumnFormat? Format { get; } = format;

    /// <summary>Reads one non-empty cell; false when the text isn't a value of this type in this format.</summary>
    public bool TryRead(string text, out object? value) => read(text, out value);

    public override string ToString() => Format?.Pattern is { } p ? $"{Type} ({p})" : Type.ToString();
}

/// <summary>
/// The readers type guessing tries, in DuckDB's priority order: boolean, date, timestamp, timestamp with zone, whole
/// number, decimal number, then text. A column takes the first reader that reads every value in the sample.
/// Source: https://duckdb.org/docs/2.0/data/csv/auto_detection.html (type detection and date formats).
/// </summary>
internal static class ValueReaders
{
    private static readonly string[] TrueSpellings = ["true", "True", "TRUE", "t", "T", "yes", "Yes", "YES"];
    private static readonly string[] FalseSpellings = ["false", "False", "FALSE", "f", "F", "no", "No", "NO"];

    /// <summary>DuckDB's date order: ISO, then year-first, day-first, month-first, so day-first wins a tie.</summary>
    private static readonly (string Net, string Strptime)[] DateParts =
    [
        ("yyyy{0}M{0}d", "%Y{0}%m{0}%d"),
        ("yy{0}M{0}d", "%y{0}%m{0}%d"),
        ("d{0}M{0}yy", "%d{0}%m{0}%y"),
        ("d{0}M{0}yyyy", "%d{0}%m{0}%Y"),
        ("M{0}d{0}yy", "%m{0}%d{0}%y"),
        ("M{0}d{0}yyyy", "%m{0}%d{0}%Y"),
    ];

    private static readonly char[] DateSeparators = ['-', '/', '.'];

    private static readonly string[] TimeParts = ["H:mm", "H:mm:ss", "H:mm:ss.FFFFFFF", "h:mm tt", "h:mm:ss tt"];

    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd HH:mm:ssK", "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd HH:mmK",
    ];

    /// <summary>Decimal and group separators a number may use: 1234.5, 1,234.5, 1234,5, 1.234,5 and 1 234,5.</summary>
    private static readonly (char Decimal, char? Group)[] NumberSeparators =
        [('.', null), ('.', ','), (',', null), (',', '.'), (',', ' ')];

    public static readonly ValueReader Text = new(TableColumnType.Text, null, (string s, out object? v) => { v = s; return true; });

    /// <summary>Every candidate, highest priority first, ending with text, which reads anything.</summary>
    public static IReadOnlyList<ValueReader> Candidates { get; } = Build();

    private static List<ValueReader> Build()
    {
        List<ValueReader> all = [Boolean()];
        foreach (char sep in DateSeparators)
            foreach ((string net, string strp) in DateParts)
                all.Add(Date(string.Format(CultureInfo.InvariantCulture, net, Quote(sep)), string.Format(CultureInfo.InvariantCulture, strp, sep)));
        foreach (char sep in DateSeparators)
            foreach ((string net, string strp) in DateParts)
                foreach (char between in new[] { ' ', 'T' })
                    all.Add(Timestamp(string.Format(CultureInfo.InvariantCulture, net, Quote(sep)), string.Format(CultureInfo.InvariantCulture, strp, sep), between));
        all.Add(TimestampWithZone());
        all.Add(WholeNumber(null));
        all.Add(WholeNumber(','));
        foreach ((char dec, char? group) in NumberSeparators)
            all.Add(Number(dec, group));
        all.Add(Text);
        return all;
    }

    private static string Quote(char c) => "'" + c + "'";

    private static ValueReader Boolean() =>
        new(TableColumnType.Boolean, new TableColumnFormat(TrueValues: TrueSpellings, FalseValues: FalseSpellings),
            (string s, out object? v) =>
            {
                s = s.Trim();
                if (Array.IndexOf(TrueSpellings, s) >= 0) { v = true; return true; }
                if (Array.IndexOf(FalseSpellings, s) >= 0) { v = false; return true; }
                v = null;
                return false;
            });

    private static ValueReader Date(string net, string strptime) =>
        new(TableColumnType.Date, new TableColumnFormat(Pattern: strptime),
            (string s, out object? v) =>
            {
                if (DateOnly.TryParseExact(s.Trim(), net, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
                {
                    v = d;
                    return true;
                }
                v = null;
                return false;
            });

    private static ValueReader Timestamp(string netDate, string strpDate, char between)
    {
        string sep = between == 'T' ? "'T'" : " ";
        string[] formats = [.. TimeParts.Select(t => netDate + sep + t)];
        string pattern = strpDate + between + "%H:%M:%S";
        return new(TableColumnType.DateTime, new TableColumnFormat(Pattern: pattern),
            (string s, out object? v) =>
            {
                if (DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                {
                    v = d;
                    return true;
                }
                v = null;
                return false;
            });
    }

    /// <summary>ISO timestamps with an offset or Z, stored as the UTC moment they name.</summary>
    private static ValueReader TimestampWithZone() =>
        new(TableColumnType.DateTime, new TableColumnFormat(Pattern: "%Y-%m-%dT%H:%M:%S%z"),
            (string s, out object? v) =>
            {
                if (DateTimeOffset.TryParseExact(s.Trim(), OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d))
                {
                    v = d.UtcDateTime;
                    return true;
                }
                v = null;
                return false;
            });

    private static ValueReader WholeNumber(char? group) =>
        new(TableColumnType.WholeNumber, group is null ? null : new TableColumnFormat(GroupChar: group.ToString()),
            (string s, out object? v) =>
            {
                v = null;
                if (!NumberText.TryNormalize(s, null, group, allowFraction: false, out string normal))
                    return false;
                if (!long.TryParse(normal, System.Globalization.NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
                    return false;
                v = n;
                return true;
            });

    private static ValueReader Number(char dec, char? group) =>
        new(TableColumnType.Number, new TableColumnFormat(DecimalChar: dec.ToString(), GroupChar: group?.ToString()),
            (string s, out object? v) =>
            {
                v = null;
                if (!NumberText.TryNormalize(s, dec, group, allowFraction: true, out string normal))
                    return false;
                if (!decimal.TryParse(normal, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n))
                    return false;
                v = n;
                return true;
            });
}

/// <summary>
/// Checks a number's shape and rewrites it with '.' decimals and no grouping. Stricter than a culture parser on
/// purpose: groups must be exactly three digits, and a whole part with a leading zero ("007", "02134") is not a number,
/// so zip codes and IDs stay text. DuckDB would read those as integers; dropping the zeros loses data.
/// </summary>
internal static class NumberText
{
    public static bool TryNormalize(string text, char? dec, char? group, bool allowFraction, out string normal)
    {
        normal = "";
        ReadOnlySpan<char> s = text.AsSpan().Trim();
        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        if (i < s.Length && (s[i] == '-' || s[i] == '+'))
            sb.Append(s[i++]);

        // Whole part, with optional grouping.
        int wholeStart = i, digitsInGroup = 0;
        bool sawGroup = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
                digitsInGroup++;
                i++;
            }
            else if (group is { } g && c == g && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
            {
                if (sawGroup ? digitsInGroup != 3 : digitsInGroup is < 1 or > 3)
                    return false;
                sawGroup = true;
                digitsInGroup = 0;
                i++;
            }
            else
            {
                break;
            }
        }
        int wholeDigits = sb.Length - (wholeStart > 0 ? 1 : 0);
        if (sawGroup && digitsInGroup != 3)
            return false;

        bool fraction = false;
        if (allowFraction && dec is { } d && i < s.Length && s[i] == d)
        {
            fraction = true;
            sb.Append('.');
            i++;
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                sb.Append(s[i++]);
            if (i == start && wholeDigits == 0)
                return false;
        }

        if (wholeDigits == 0 && !fraction)
            return false;
        // "0", "-0" and "0.5" are fine; "007", "00" and "012.5" are not.
        if (wholeDigits > 1 && s[wholeStart] == '0')
            return false;

        if (allowFraction && i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            sb.Append('e');
            i++;
            if (i < s.Length && (s[i] == '-' || s[i] == '+'))
                sb.Append(s[i++]);
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                sb.Append(s[i++]);
            if (i == start)
                return false;
        }

        if (i != s.Length)
            return false;
        normal = sb.ToString();
        return true;
    }
}
