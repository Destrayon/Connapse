using System.Text;

namespace Connapse.Ingestion.Parsers;

/// <summary>
/// Puts OCR'd text lines in reading order and joins them into paragraphs (#598).
/// <para>
/// The recogniser returns one line per detected text box, top to bottom across the whole page, so
/// a two-column scan comes out with its columns interleaved line by line. A recursive XY-cut puts
/// it back: the line boxes are split at the widest band of empty space running across or down the
/// page, and each side is ordered the same way, so a full-width title is cut off first and the
/// columns beneath it are then read one after the other. Words broken over a line end with a
/// hyphen are rejoined, and a gap taller than a line starts a new paragraph.
/// </para>
/// </summary>
public static class OcrLayout
{
    /// <summary>A recognised line and its box, in pixels from the top-left corner.</summary>
    public readonly record struct Line(int Left, int Top, int Right, int Bottom, string Text)
    {
        public int Height => Math.Max(1, Bottom - Top);
    }

    /// <summary>Deep enough for any real page; stops a degenerate set of boxes from recursing on.</summary>
    private const int MaxDepth = 64;

    public static string Arrange(IReadOnlyList<Line> lines)
    {
        var usable = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (usable.Count == 0)
            return string.Empty;

        var ordered = new List<List<Line>>();
        Cut(usable, ordered, 0);
        return Join(ordered);
    }

    /// <summary>Appends the lines in reading order, as runs that share a column.</summary>
    private static void Cut(List<Line> lines, List<List<Line>> output, int depth)
    {
        if (lines.Count > 1 && depth < MaxDepth)
        {
            var (xGap, xAt) = LargestGap(lines.Select(l => (l.Left, l.Right)));
            var (yGap, yAt) = LargestGap(lines.Select(l => (l.Top, l.Bottom)));

            if (xGap > 0 && xGap >= yGap)
            {
                Cut(lines.Where(l => l.Right <= xAt).ToList(), output, depth + 1);
                Cut(lines.Where(l => l.Left >= xAt).ToList(), output, depth + 1);
                return;
            }

            if (yGap > 0)
            {
                Cut(lines.Where(l => l.Bottom <= yAt).ToList(), output, depth + 1);
                Cut(lines.Where(l => l.Top >= yAt).ToList(), output, depth + 1);
                return;
            }
        }

        // Nothing separates these lines any further: they overlap, so read them top to bottom.
        var run = lines.OrderBy(l => l.Top).ThenBy(l => l.Left).ToList();
        if (output.Count > 0 && Continues(output[^1], run))
            output[^1].AddRange(run);
        else
            output.Add(run);
    }

    /// <summary>
    /// A run continues the one before when it sits just below it in the same column: the XY-cut
    /// splits every paragraph gap in a column into its own run, and those belong together.
    /// </summary>
    private static bool Continues(List<Line> previous, List<Line> next)
    {
        Line last = previous[^1];
        Line first = next[0];
        bool sameColumn = first.Left < last.Right && last.Left < first.Right;
        return sameColumn && first.Top >= last.Top;
    }

    /// <summary>The widest empty band between the projected intervals, and where to cut it.</summary>
    private static (int Gap, int At) LargestGap(IEnumerable<(int Start, int End)> intervals)
    {
        int gap = 0;
        int at = 0;
        int? reach = null;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start))
        {
            if (reach is int covered && start > covered && start - covered > gap)
            {
                gap = start - covered;
                at = covered + gap / 2;
            }

            reach = reach is int r ? Math.Max(r, end) : end;
        }

        return (gap, at);
    }

    private static string Join(List<List<Line>> runs)
    {
        var text = new StringBuilder();
        foreach (var run in runs)
        {
            if (text.Length > 0)
                text.Append("\n\n");

            for (int i = 0; i < run.Count; i++)
            {
                string line = run[i].Text.Trim();
                if (i == 0)
                {
                    text.Append(line);
                    continue;
                }

                Line previous = run[i - 1];
                bool paragraphBreak = run[i].Top - previous.Bottom > previous.Height;
                if (paragraphBreak)
                {
                    text.Append("\n\n").Append(line);
                }
                else if (text.Length > 1 && text[^1] == '-' && char.IsLetter(text[^2]) && line.Length > 0 && char.IsLower(line[0]))
                {
                    // "harbour-" + "master" was one word before the line ended.
                    text.Length--;
                    text.Append(line);
                }
                else
                {
                    text.Append('\n').Append(line);
                }
            }
        }

        return text.ToString();
    }
}
