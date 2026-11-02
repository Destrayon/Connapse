using System.Text;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// Describes where two versions of the OpenAPI document differ, as plain text. The spec test fails with
/// this text instead of handing both documents to FluentAssertions, whose diff message reads the JSON's
/// braces as format placeholders and throws a FormatException in place of the difference (CON-126).
/// </summary>
public static class SpecDifference
{
    private const int MaxPlaces = 20;
    private const int MaxLinesPerPlace = 20;
    private const int MaxEdits = 2000;

    /// <summary>
    /// Returns null when the two texts are equal, ignoring line endings. Otherwise lists each place where
    /// they differ, like a diff without context: "-" lines are the committed file's and "+" lines the
    /// generated one's, each with its own line number.
    /// </summary>
    public static string? Describe(string committed, string generated)
    {
        string[] expected = committed.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        string[] actual = generated.ReplaceLineEndings("\n").TrimEnd().Split('\n');

        List<Place>? places = Diff(expected, actual);
        if (places is { Count: 0 })
            return null;
        if (places is null)
            return TooManyDifferences(expected, actual);

        StringBuilder message = new();
        message.Append("The committed spec differs from the generated one in ").Append(places.Count)
            .Append(places.Count == 1 ? " place" : " places").Append(" (- committed, + generated):\n");
        foreach (Place place in places.Take(MaxPlaces))
        {
            message.Append("@@ committed line ").Append(place.ExpectedStart + 1)
                .Append(", generated line ").Append(place.ActualStart + 1).Append(" @@\n");
            List<string> lines = [
                .. place.Removed.Select((line, i) => Line('-', place.ExpectedStart + i, line)),
                .. place.Added.Select((line, i) => Line('+', place.ActualStart + i, line)),
            ];
            foreach (string line in lines.Take(MaxLinesPerPlace))
                message.Append(line).Append('\n');
            if (lines.Count > MaxLinesPerPlace)
                message.Append("  ... ").Append(lines.Count - MaxLinesPerPlace).Append(" more lines here\n");
        }
        if (places.Count > MaxPlaces)
            message.Append("... ").Append(places.Count - MaxPlaces).Append(" more places\n");
        return message.ToString().TrimEnd();
    }

    private static string Line(char marker, int index, string text) =>
        $"{marker} {(index + 1).ToString().PadLeft(6)} | {text}";

    private static string TooManyDifferences(string[] expected, string[] actual)
    {
        int first = 0;
        while (first < expected.Length && first < actual.Length && expected[first] == actual[first])
            first++;
        return $"The committed spec differs from the generated one in more than {MaxEdits} lines, starting at line {first + 1} " +
            $"(committed: {expected.Length} lines, generated: {actual.Length} lines).";
    }

    private sealed record Place(int ExpectedStart, int ActualStart, List<string> Removed, List<string> Added);

    /// <summary>
    /// Myers' shortest edit script over lines, grouped into places of consecutive changes. Returns null
    /// when the documents need more than <see cref="MaxEdits"/> line edits, which keeps the work bounded.
    /// </summary>
    private static List<Place>? Diff(string[] a, string[] b)
    {
        int n = a.Length;
        int m = b.Length;
        int max = Math.Min(n + m, MaxEdits);
        List<int[]> trace = [];
        int[] v = new int[3];
        int found = -1;

        for (int d = 0; d <= max && found < 0; d++)
        {
            // v holds, for each diagonal k in [-d, d], the furthest x reached; index is k + d + 1.
            int[] next = new int[2 * d + 3];
            for (int k = -d; k <= d; k += 2)
            {
                int down = k + 1;
                int right = k - 1;
                int x = k == -d || (k != d && Get(v, d - 1, right) < Get(v, d - 1, down))
                    ? Get(v, d - 1, down)
                    : Get(v, d - 1, right) + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                next[k + d + 1] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            trace.Add(next);
            v = next;
        }

        if (found < 0)
            return null;

        List<(char Op, int X, int Y)> edits = [];
        int cx = n;
        int cy = m;
        for (int d = found; d > 0; d--)
        {
            int[] previous = trace[d - 1];
            int k = cx - cy;
            int prevK = k == -d || (k != d && Get(previous, d - 1, k - 1) < Get(previous, d - 1, k + 1)) ? k + 1 : k - 1;
            int prevX = Get(previous, d - 1, prevK);
            int prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                cx--;
                cy--;
            }
            if (cx == prevX)
                edits.Add(('+', cx, prevY));
            else
                edits.Add(('-', prevX, cy));
            cx = prevX;
            cy = prevY;
        }
        edits.Reverse();

        List<Place> places = [];
        Place? current = null;
        foreach ((char op, int x, int y) in edits)
        {
            bool continues = current is not null &&
                x == current.ExpectedStart + current.Removed.Count && y == current.ActualStart + current.Added.Count;
            if (!continues)
            {
                current = new Place(x, y, [], []);
                places.Add(current);
            }
            if (op == '-')
                current!.Removed.Add(a[x]);
            else
                current!.Added.Add(b[y]);
        }
        return places;
    }

    // Reads diagonal k from the v array of round d, where index = k + d + 1. Round -1 is all zeros.
    private static int Get(int[] v, int d, int k)
    {
        int index = k + d + 1;
        return index >= 0 && index < v.Length ? v[index] : 0;
    }
}
