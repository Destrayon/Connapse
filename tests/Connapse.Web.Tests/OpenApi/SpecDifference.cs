using System.Text;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// Describes where two versions of the OpenAPI document differ, as plain text. The spec test fails with
/// this text instead of handing both documents to FluentAssertions, whose diff message reads the JSON's
/// braces as format placeholders and throws a FormatException in place of the difference (CON-126).
/// </summary>
public static class SpecDifference
{
    private const int MaxLinesPerSide = 40;

    /// <summary>
    /// Returns null when the two texts are equal. Otherwise returns the block of lines where they
    /// differ: the shared lines at the start and end are skipped, and what is left on each side is
    /// listed with its line number, "-" for the committed file and "+" for the generated one.
    /// </summary>
    public static string? Describe(string committed, string generated)
    {
        string[] expected = committed.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        string[] actual = generated.ReplaceLineEndings("\n").TrimEnd().Split('\n');

        int start = 0;
        while (start < expected.Length && start < actual.Length && expected[start] == actual[start])
            start++;

        if (start == expected.Length && start == actual.Length)
            return null;

        int expectedEnd = expected.Length;
        int actualEnd = actual.Length;
        while (expectedEnd > start && actualEnd > start && expected[expectedEnd - 1] == actual[actualEnd - 1])
        {
            expectedEnd--;
            actualEnd--;
        }

        StringBuilder message = new();
        message.Append("The committed spec differs from the generated one from line ").Append(start + 1).Append(":\n");
        AppendLines(message, "- committed", expected, start, expectedEnd);
        AppendLines(message, "+ generated", actual, start, actualEnd);
        return message.ToString().TrimEnd();
    }

    private static void AppendLines(StringBuilder message, string label, string[] lines, int start, int end)
    {
        char marker = label[0];
        int count = end - start;
        message.Append(label).Append(" (").Append(count).Append(count == 1 ? " line" : " lines").Append("):\n");
        for (int i = start; i < end && i < start + MaxLinesPerSide; i++)
            message.Append(marker).Append(' ').Append((i + 1).ToString().PadLeft(6)).Append(" | ").Append(lines[i]).Append('\n');
        if (count > MaxLinesPerSide)
            message.Append(marker).Append(" ... ").Append(count - MaxLinesPerSide).Append(" more\n");
    }
}
