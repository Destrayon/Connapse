namespace Connapse.Eval.Checks.Matching;

/// <summary>
/// Counts how many times the final n-gram repeats back-to-back at the end of a text, for n = 1..max.
/// Ported from olmocr <c>olmocr/repeatdetect.py</c> (commit f7cfe4c2; Apache-2.0, see
/// THIRD_PARTY_NOTICES.md). Verified against the reference in OlmOcrMatchingTests.
/// </summary>
public static class RepeatDetector
{
    public static int[] NgramRepeats(string data, int maxNgramSize)
    {
        int[] result = new int[maxNgramSize];
        if (data.Length == 0)
            return result;

        int[] text = CollapseWhitespace(PyText.CodePoints(data));
        for (int size = 1; size <= maxNgramSize; size++)
        {
            if (text.Length < size)
                continue;

            ReadOnlySpan<int> target = text.AsSpan(text.Length - size, size);
            int count = 0;
            for (int pos = text.Length - size; pos >= 0; pos -= size)
            {
                if (!text.AsSpan(pos, size).SequenceEqual(target))
                    break;
                count++;
            }
            result[size - 1] = count;
        }
        return result;
    }

    // re.sub(r"\s+", " ", text) with Python's whitespace class.
    internal static int[] CollapseWhitespace(int[] points)
    {
        List<int> collapsed = new(points.Length);
        bool inSpace = false;
        foreach (int c in points)
        {
            if (PyText.IsSpace(c))
            {
                if (!inSpace)
                    collapsed.Add(' ');
                inSpace = true;
            }
            else
            {
                collapsed.Add(c);
                inSpace = false;
            }
        }
        return [.. collapsed];
    }
}
