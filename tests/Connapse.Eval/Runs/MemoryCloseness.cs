using System.Text;

namespace Connapse.Eval.Runs;

/// <summary>
/// The word-overlap candidate scores the CON-245 sorting eval compared with cosine: the share of a memory's content
/// words that appear in a passage. Words are runs of letters or digits, lower-cased, without common function words, so
/// "CHG-9333, 120 days" reads as chg, 9333, 120 and days.
/// </summary>
public static class MemoryCloseness
{
    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "in", "is", "it", "its", "of",
        "on", "or", "that", "the", "this", "to", "was", "were", "with",
    };

    /// <summary>The distinct content words of <paramref name="text"/>.</summary>
    public static HashSet<string> Words(string? text)
    {
        HashSet<string> words = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
            return words;
        StringBuilder current = new();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }
            Add(words, current);
        }
        Add(words, current);
        return words;
    }

    /// <summary>
    /// The share of <paramref name="note"/>'s words found in <paramref name="passages"/> taken together, from 0 to 1;
    /// 0 when the note has no content words.
    /// </summary>
    public static double Coverage(IReadOnlySet<string> note, params IEnumerable<string>[] passages)
    {
        if (note.Count == 0)
            return 0;
        HashSet<string> found = [];
        foreach (IEnumerable<string> passage in passages)
            found.UnionWith(passage.Where(note.Contains));
        return (double)found.Count / note.Count;
    }

    private static void Add(HashSet<string> words, StringBuilder current)
    {
        if (current.Length == 0)
            return;
        string word = current.ToString();
        current.Clear();
        if (!FunctionWords.Contains(word))
            words.Add(word);
    }
}
