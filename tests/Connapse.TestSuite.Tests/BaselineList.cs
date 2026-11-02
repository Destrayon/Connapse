using System.Text;
using System.Text.Json;

namespace Connapse.TestSuite.Tests;

/// <summary>
/// A committed list that the code must match exactly: a new entry fails, and so does a fixed one
/// still listed. A failure prints the JSON to paste, so every change to the list is a reviewed edit.
/// </summary>
internal static class BaselineList
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void AssertMatches(string fileName, IEnumerable<string> actual, string howToFix)
    {
        var expected = new SortedSet<string>(
            JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TestSources.Baseline(fileName))) ?? [], StringComparer.Ordinal);
        var current = new SortedSet<string>(actual, StringComparer.Ordinal);

        var added = current.Except(expected).ToList();
        var removed = expected.Except(current).ToList();
        if (added.Count == 0 && removed.Count == 0)
            return;

        var message = new StringBuilder();
        foreach (string a in added) message.AppendLine($"  + {a}   ({howToFix})");
        foreach (string r in removed) message.AppendLine($"  - {r}   (fixed: delete it from Baselines/{fileName})");
        message.AppendLine($"Current list for Baselines/{fileName}:");
        message.AppendLine(JsonSerializer.Serialize(current, Json));
        Assert.Fail(message.ToString());
    }
}
