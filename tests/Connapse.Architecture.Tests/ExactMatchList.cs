using System.Text;
using System.Text.Json;

namespace Connapse.Architecture.Tests;

/// <summary>
/// A committed list of today's violations that the code must match exactly. A new violation fails,
/// and so does a fixed one still on the list, so each migration ticket's first commit is deleting
/// its own entries. Nothing here ever rewrites a list: a failure prints the JSON to paste, which
/// keeps every change to a list a reviewed edit.
/// </summary>
internal static class ExactMatchList
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<string> Load(string fileName)
    {
        string path = RepoPaths.Baseline(fileName);
        return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [];
    }

    public static void AssertMatches(string fileName, IEnumerable<string> actual, string howToFix)
    {
        var expected = new SortedSet<string>(Load(fileName), StringComparer.Ordinal);
        var current = new SortedSet<string>(actual, StringComparer.Ordinal);

        var added = current.Except(expected).ToList();
        var fixedButListed = expected.Except(current).ToList();
        if (added.Count == 0 && fixedButListed.Count == 0)
            return;

        var message = new StringBuilder();
        if (added.Count > 0)
        {
            message.AppendLine($"New violations not in Baselines/{fileName}. {howToFix}");
            foreach (string v in added) message.AppendLine($"  + {v}");
        }
        if (fixedButListed.Count > 0)
        {
            message.AppendLine($"No longer violating, so delete these from Baselines/{fileName}:");
            foreach (string v in fixedButListed) message.AppendLine($"  - {v}");
        }
        message.AppendLine("Current list, for review:");
        message.AppendLine(JsonSerializer.Serialize(current, Json));
        Assert.Fail(message.ToString());
    }
}
