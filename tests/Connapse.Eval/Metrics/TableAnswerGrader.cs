using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Connapse.Eval.Metrics;

/// <summary>
/// Grades a typed answer against a gold answer for the <c>tables-v1</c> suite. Ported from DataBench's
/// <c>Evaluator.default_compare</c> (databench_eval commit 65f92cfad4f986f5496170f6bbcee48323f56c2f,
/// <c>src/databench_eval/eval.py</c>; MIT, see THIRD_PARTY_NOTICES.md): the null set, the stripped characters,
/// the boolean words, the number cleaning and the list splitting are DataBench's. The CON-E6 plan changes four
/// things, and the fixtures in TableAnswerGraderFixtureTests record each case where the result differs:
/// <list type="number">
/// <item>Numbers match within 1% of the gold value, or exactly when the gold value is an integer, instead of
/// DataBench's truncation to two decimals.</item>
/// <item>Categories match after trimming, collapsing whitespace and ignoring case; DataBench compares them
/// case-sensitively.</item>
/// <item>Two categories that differ as text match as dates only when both are ISO 8601 dates or timestamps;
/// DataBench accepts anything <c>pandas.to_datetime</c> parses, such as "Dec 22, 2016".</item>
/// <item>Lists are compared as multisets: <c>list[number]</c> pairs the sorted values and applies the number rule
/// to each pair, and duplicates count. A JSON array answer is taken item by item instead of being split on commas,
/// so an item may contain a comma.</item>
/// </list>
/// Number cleaning keeps ASCII digits only; Python's <c>str.isdigit</c> also keeps other Unicode digits.
/// </summary>
public static partial class TableAnswerGrader
{
    public const string Boolean = "boolean";
    public const string Category = "category";
    public const string Number = "number";
    public const string ListCategory = "list[category]";
    public const string ListNumber = "list[number]";

    /// <summary>The answer types the grader knows, as named in DataBench.</summary>
    public static readonly IReadOnlyList<string> Types = [Boolean, Category, Number, ListCategory, ListNumber];

    /// <summary>Relative tolerance for a non-integer gold number.</summary>
    public const double RelativeTolerance = 0.01;

    private const string StripChars = "[]'\" ";
    private static readonly HashSet<string> NullSet = ["nan", "", " ", "np.nan", "None"];
    private static readonly HashSet<string> TrueWords = ["true", "yes", "y"];
    private static readonly HashSet<string> FalseWords = ["false", "no", "n"];

    /// <summary>Grades <paramref name="value"/> against <paramref name="truth"/>.</summary>
    /// <exception cref="ArgumentException">The type is not one of <see cref="Types"/>.</exception>
    public static bool Grade(JsonElement value, JsonElement truth, string type)
    {
        string semantic = (type ?? throw new ArgumentNullException(nameof(type))).Trim();
        if (!Types.Contains(semantic))
            throw new ArgumentException($"Answer type not supported: {semantic}", nameof(type));

        bool valueNull = IsNull(ToText(value));
        bool truthNull = IsNull(ToText(truth));
        if (valueNull || truthNull)
            return valueNull && truthNull;

        return semantic switch
        {
            Boolean => BooleanMatch(ToText(value)!, ToText(truth)!),
            Category => CategoryMatch(ToText(value)!, ToText(truth)!),
            Number => NumberMatch(value, truth),
            ListCategory => ListCategoryMatch(value, truth),
            _ => ListNumberMatch(value, truth),
        };
    }

    /// <summary>Grades answers given as text, as DataBench receives them.</summary>
    public static bool Grade(string? value, string? truth, string type) =>
        Grade(JsonSerializer.SerializeToElement(value), JsonSerializer.SerializeToElement(truth), type);

    private static bool BooleanMatch(string value, string truth)
    {
        string v = Strip(value).ToLowerInvariant();
        string t = Strip(truth).ToLowerInvariant();
        return (TrueWords.Contains(v) && TrueWords.Contains(t)) || (FalseWords.Contains(v) && FalseWords.Contains(t));
    }

    private static bool CategoryMatch(string value, string truth)
    {
        string v = NormalizeCategory(Strip(value));
        string t = NormalizeCategory(Strip(truth));
        if (v == t)
            return true;
        return TryIsoDate(v, out DateOnly vd) && TryIsoDate(t, out DateOnly td) && vd == td;
    }

    private static bool NumberMatch(JsonElement value, JsonElement truth) =>
        TryNumber(value, out double v, out _) && TryNumber(truth, out double t, out bool integral) && Close(v, t, integral);

    private static bool ListCategoryMatch(JsonElement value, JsonElement truth)
    {
        List<string> values = [.. Items(value, skipBlank: false).Select(CategoryItem)];
        List<string> truths = [.. Items(truth, skipBlank: false).Select(CategoryItem)];
        if (values.Count != truths.Count)
            return false;

        if (TryIsoDates(values, out List<DateOnly>? vd) && TryIsoDates(truths, out List<DateOnly>? td))
            return SameMultiset(vd, td);
        return SameMultiset(values, truths);
    }

    private static bool ListNumberMatch(JsonElement value, JsonElement truth)
    {
        List<(double Number, bool Integral)> values = [];
        foreach (JsonElement item in ItemElements(value, skipBlank: true))
        {
            if (!TryNumber(item, out double n, out bool integral))
                return false;
            values.Add((n, integral));
        }

        List<(double Number, bool Integral)> truths = [];
        foreach (JsonElement item in ItemElements(truth, skipBlank: true))
        {
            if (!TryNumber(item, out double n, out bool integral))
                return false;
            truths.Add((n, integral));
        }

        if (values.Count != truths.Count)
            return false;

        values.Sort((a, b) => a.Number.CompareTo(b.Number));
        truths.Sort((a, b) => a.Number.CompareTo(b.Number));
        for (int i = 0; i < values.Count; i++)
        {
            if (!Close(values[i].Number, truths[i].Number, truths[i].Integral))
                return false;
        }

        return true;
    }

    /// <summary>The number rule: exact for an integer gold value, else within <see cref="RelativeTolerance"/>.</summary>
    public static bool Close(double value, double truth, bool truthIsInteger) =>
        truthIsInteger ? value == truth : Math.Abs(value - truth) <= RelativeTolerance * Math.Abs(truth);

    /// <summary>
    /// Reads a number. A JSON number is used as is and is an integer when written without a point or exponent.
    /// Text is cleaned as DataBench does (keep digits, '.' and '-') and is an integer when no point is left.
    /// </summary>
    public static bool TryNumber(JsonElement element, out double number, out bool integral)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            string raw = element.GetRawText();
            number = element.GetDouble();
            integral = raw.IndexOfAny(['.', 'e', 'E']) < 0;
            return true;
        }

        string? text = ToText(element);
        integral = false;
        number = 0;
        if (text is null)
            return false;

        StringBuilder cleaned = new(text.Length);
        foreach (char c in text)
        {
            if (c is (>= '0' and <= '9') or '.' or '-')
                cleaned.Append(c);
        }

        string s = cleaned.ToString();
        if (!double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
            return false;
        integral = !s.Contains('.');
        return true;
    }

    /// <summary>
    /// An ISO 8601 date, or a timestamp whose date part is taken as written (as <c>pandas</c>' <c>.date()</c>
    /// does): <c>2016-12-22</c>, <c>2016-12-22T00:00:00Z</c>, <c>2016-12-22 00:00:00+00:00</c>.
    /// </summary>
    public static bool TryIsoDate(string text, out DateOnly date)
    {
        date = default;
        Match m = IsoTimestamp().Match(text.Trim());
        if (!m.Success)
            return false;
        if (m.Groups["h"].Success && (int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) > 23
            || int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture) > 59
            || (m.Groups["s"].Success && int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) > 59)))
        {
            return false;
        }

        return DateOnly.TryParseExact(m.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryIsoDates(List<string> items, out List<DateOnly> dates)
    {
        dates = new List<DateOnly>(items.Count);
        foreach (string item in items)
        {
            if (!TryIsoDate(item, out DateOnly d))
                return false;
            dates.Add(d);
        }

        return true;
    }

    private static bool SameMultiset<T>(List<T> a, List<T> b) where T : notnull
    {
        Dictionary<T, int> counts = [];
        foreach (T x in a)
            counts[x] = counts.GetValueOrDefault(x) + 1;
        foreach (T x in b)
        {
            int n = counts.GetValueOrDefault(x);
            if (n == 0)
                return false;
            counts[x] = n - 1;
        }

        return true;
    }

    private static string CategoryItem(string item)
    {
        string stripped = Strip(item);
        return NullSet.Contains(stripped) ? "" : NormalizeCategory(stripped);
    }

    /// <summary>List items as text: a JSON array item by item, anything else split on commas as DataBench does.</summary>
    private static IEnumerable<string> Items(JsonElement element, bool skipBlank) =>
        ItemElements(element, skipBlank).Select(e => ToText(e) ?? "None");

    private static IEnumerable<JsonElement> ItemElements(JsonElement element, bool skipBlank)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (!skipBlank || !string.IsNullOrWhiteSpace(ToText(item)))
                    yield return item;
            }

            yield break;
        }

        foreach (string part in ToText(element)!.Trim('[', ']').Split(','))
        {
            if (!skipBlank || !string.IsNullOrWhiteSpace(part))
                yield return JsonSerializer.SerializeToElement(part.Trim());
        }
    }

    private static bool IsNull(string? text) => text is null || NullSet.Contains(Strip(text));

    private static string Strip(string text) => text.Trim(StripChars.ToCharArray());

    private static string NormalizeCategory(string text) => Whitespace().Replace(text.Trim(), " ").ToLowerInvariant();

    /// <summary>The text Python's <c>str()</c> gives the decoded JSON value; null for JSON null.</summary>
    public static string? ToText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Array => "[" + string.Join(", ", element.EnumerateArray().Select(Repr)) + "]",
        _ => element.GetRawText(),
    };

    private static string Repr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "'" + element.GetString() + "'",
        JsonValueKind.Null or JsonValueKind.Undefined => "None",
        _ => ToText(element)!,
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?<d>\d{4}-\d{2}-\d{2})(?:[Tt ](?<h>\d{2}):(?<mi>\d{2})(?::(?<s>\d{2})(?:\.\d+)?)?(?:[Zz]|[+-]\d{2}:?\d{2})?)?$")]
    private static partial Regex IsoTimestamp();
}
