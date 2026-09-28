using System.Text;
using System.Text.RegularExpressions;
using Connapse.Eval.Checks.Matching;

namespace Connapse.Eval.Checks.OlmOcr;

/// <summary>A check's result: pass or fail with Python's explanation string, or skipped.</summary>
public sealed record OlmOcrOutcome(bool Passed, string Explanation, bool Skipped = false)
{
    public static readonly OlmOcrOutcome Pass = new(true, "");
}

/// <summary>
/// The olmOCR-bench unit tests, ported from olmocr <c>olmocr/bench/tests.py</c> (commit f7cfe4c2;
/// Apache-2.0, see THIRD_PARTY_NOTICES.md): <c>TextPresenceTest</c>, <c>TextOrderTest</c>,
/// <c>TableTest</c> and <c>BaselineTest</c>. <c>MathTest</c> is loaded but always skipped, because it
/// compares LaTeX rendered in a browser. Verified against the reference in OlmOcrCheckTests.
/// </summary>
public abstract record OlmOcrTest(string Pdf, int Page, string Id, string Type, int MaxDiffs)
{
    public abstract OlmOcrOutcome Run(string content);

    /// <summary>1 − max_diffs / len(text), the fuzzy-match threshold every text check uses.</summary>
    protected double Threshold(string text)
    {
        int length = PyText.Length(text);
        return 1.0 - ((double)MaxDiffs / (length > 0 ? length : 1));
    }
}

public sealed record TextPresenceTest(
    string Pdf, int Page, string Id, string Type, int MaxDiffs,
    string Text, bool CaseSensitive = true, int? FirstN = null, int? LastN = null)
    : OlmOcrTest(Pdf, Page, Id, Type, MaxDiffs)
{
    public override OlmOcrOutcome Run(string content)
    {
        string reference = Text;
        string md = OlmOcrText.Normalize(content)!;
        if (!CaseSensitive)
        {
            reference = PyText.Lower(reference);
            md = PyText.Lower(md);
        }

        // Python truthiness (0 and None are false) and slice semantics, so negative values count from the end.
        int firstN = FirstN ?? 0;
        int lastN = LastN ?? 0;
        if (firstN != 0 || lastN != 0)
        {
            int[] points = PyText.CodePoints(md);
            string head = PyText.FromCodePoints(PyText.Slice(points, null, firstN));
            string tail = PyText.FromCodePoints(PyText.Slice(points, -lastN, null));
            md = firstN != 0 && lastN != 0 ? head + tail : firstN != 0 ? head : tail;
        }

        double threshold = Threshold(reference);
        double bestRatio = RapidFuzz.PartialRatio(reference, md) / 100.0;
        string head40 = OlmOcrText.Head(reference, 40);

        if (Type == "present")
            return bestRatio >= threshold
                ? OlmOcrOutcome.Pass
                : new OlmOcrOutcome(false, $"Expected '{head40}...' with threshold {OlmOcrText.Repr(threshold)} but best match ratio was {OlmOcrText.Fixed(bestRatio, 3)}");
        return bestRatio < threshold
            ? OlmOcrOutcome.Pass
            : new OlmOcrOutcome(false, $"Expected absence of '{head40}...' with threshold {OlmOcrText.Repr(threshold)} but best match ratio was {OlmOcrText.Fixed(bestRatio, 3)}");
    }
}

public sealed record TextOrderTest(string Pdf, int Page, string Id, string Type, int MaxDiffs, string Before, string After)
    : OlmOcrTest(Pdf, Page, Id, Type, MaxDiffs)
{
    public override OlmOcrOutcome Run(string content)
    {
        string md = OlmOcrText.Normalize(content)!;
        int[] points = PyText.CodePoints(md);
        IReadOnlyList<NearMatch> before = FuzzySearch.FindNearMatches(PyText.CodePoints(Before), points, MaxDiffs);
        IReadOnlyList<NearMatch> after = FuzzySearch.FindNearMatches(PyText.CodePoints(After), points, MaxDiffs);

        if (before.Count == 0)
            return new OlmOcrOutcome(false, $"'before' text '{OlmOcrText.Head(Before, 40)}...' not found with max_l_dist {MaxDiffs}");
        if (after.Count == 0)
            return new OlmOcrOutcome(false, $"'after' text '{OlmOcrText.Head(After, 40)}...' not found with max_l_dist {MaxDiffs}");

        foreach (NearMatch b in before)
            foreach (NearMatch a in after)
                if (b.Start < a.Start)
                    return OlmOcrOutcome.Pass;
        return new OlmOcrOutcome(false,
            $"Could not find a location where '{OlmOcrText.Head(Before, 40)}...' appears before '{OlmOcrText.Head(After, 40)}...'.");
    }
}

public sealed record TableTest(
    string Pdf, int Page, string Id, string Type, int MaxDiffs,
    string Cell, string? Up = null, string? Down = null, string? Left = null, string? Right = null,
    string? TopHeading = null, string? LeftHeading = null, bool IgnoreMarkdownTables = false)
    : OlmOcrTest(Pdf, Page, Id, Type, MaxDiffs)
{
    public override OlmOcrOutcome Run(string content)
    {
        double threshold = Math.Max(0.5, Threshold(Cell));
        List<TableData> tables = IgnoreMarkdownTables ? [] : [.. MarkdownTables.Parse(content)];
        if (tables.Count == 0)
            return new OlmOcrOutcome(false, "No tables found in the content");

        List<string> failedReasons = [];
        foreach (TableData table in tables)
        {
            List<CellPos> matches = table.CellText
                .Where(p => RapidFuzz.Ratio(Cell, OlmOcrText.Normalize(p.Value)!) / 100.0 >= threshold)
                .Select(p => p.Key)
                .ToList();

            foreach (CellPos cell in matches)
            {
                List<string> reasons = [];
                bool satisfied = true;

                void Check(string? expected, IReadOnlySet<CellPos> candidates)
                {
                    if (string.IsNullOrEmpty(expected))
                        return;
                    bool found = false;
                    double best = 0;
                    string? bestText = null;
                    foreach (CellPos other in candidates)
                    {
                        string text = OlmOcrText.Normalize(table.CellText[other])!;
                        double similarity = RapidFuzz.Ratio(expected, text) / 100.0;
                        if (similarity > best)
                        {
                            best = similarity;
                            bestText = text;
                        }
                        if (similarity >= Math.Max(0.5, Threshold(expected)))
                            found = true;
                    }
                    if (!found)
                    {
                        satisfied = false;
                        reasons.Add($"Cell compared to '{bestText ?? "None"}' doesn't match expected '{expected}' (best similarity: {OlmOcrText.Fixed(best, 2)})");
                    }
                }

                Check(Up, table.Up[cell]);
                Check(Down, table.Down[cell]);
                Check(Left, table.Left[cell]);
                Check(Right, table.Right[cell]);
                Check(LeftHeading, table.LeftHeadings(cell));
                Check(TopHeading, table.TopHeadings(cell));

                if (satisfied)
                    return OlmOcrOutcome.Pass;
                failedReasons.AddRange(reasons);
            }
        }

        return failedReasons.Count == 0
            ? new OlmOcrOutcome(false, $"No cell matching '{Cell}' found in any table with threshold {OlmOcrText.Repr(threshold)}")
            : new OlmOcrOutcome(false, $"Found cells matching '{Cell}' but relationships were not satisfied: {string.Join("; ", failedReasons)}");
    }
}

public sealed partial record BaselineTest(
    string Pdf, int Page, string Id, string Type, int MaxDiffs,
    int? MaxLength = null, bool MaxLengthSkipsImageAltTags = false, int MaxRepeats = 30, bool CheckDisallowedCharacters = true)
    : OlmOcrTest(Pdf, Page, Id, Type, MaxDiffs)
{
    [GeneratedRegex(@"!\[.*?\]\(.*?\)")]
    private static partial Regex ImageTag();

    public override OlmOcrOutcome Run(string content)
    {
        int alnum = PyText.CodePoints(content).Count(PyText.IsAlnum);

        if (MaxLength is { } maxLength)
        {
            if (MaxLengthSkipsImageAltTags)
                alnum = PyText.CodePoints(ImageTag().Replace(content, "")).Count(PyText.IsAlnum);
            return alnum > maxLength
                ? new OlmOcrOutcome(false, $"{alnum} characters were output for a page we expected to be blank")
                : OlmOcrOutcome.Pass;
        }

        if (alnum == 0)
            return new OlmOcrOutcome(false, "The text contains no alpha numeric characters");

        int[] repeats = RepeatDetector.NgramRepeats(content, 5);
        for (int i = 0; i < repeats.Length; i++)
            if (repeats[i] > MaxRepeats)
                return new OlmOcrOutcome(false, $"Text ends with {repeats[i]} repeating {i + 1}-grams, invalid");

        List<string> disallowed = PyText.CodePoints(content).Where(IsDisallowed).Select(c => char.ConvertFromUtf32(c)).ToList();
        if (CheckDisallowedCharacters && disallowed.Count > 0)
            return new OlmOcrOutcome(false, $"Text contains disallowed characters {PythonList(disallowed)}");

        return OlmOcrOutcome.Pass;
    }

    // CJK ideographs, hiragana, katakana, and four emoji blocks.
    private static bool IsDisallowed(int c) =>
        c is >= 0x4E00 and <= 0x9FFF or >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF
            or >= 0x1F600 and <= 0x1F64F or >= 0x1F300 and <= 0x1F5FF or >= 0x1F680 and <= 0x1F6FF or >= 0x1F1E0 and <= 0x1F1FF;

    private static string PythonList(List<string> items)
    {
        StringBuilder builder = new("[");
        for (int i = 0; i < items.Count; i++)
            builder.Append(i == 0 ? "" : ", ").Append('\'').Append(items[i]).Append('\'');
        return builder.Append(']').ToString();
    }
}

/// <summary>Loaded so math rows validate, but never run: it needs LaTeX rendered in a browser.</summary>
public sealed record MathTest(string Pdf, int Page, string Id, string Type, int MaxDiffs, string Math)
    : OlmOcrTest(Pdf, Page, Id, Type, MaxDiffs)
{
    public override OlmOcrOutcome Run(string content) =>
        new(false, "math checks compare rendered LaTeX and are not run", Skipped: true);
}
