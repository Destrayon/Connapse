namespace Connapse.Core;

/// <summary>
/// Which of a search's words the facts it showed hold (CON-337). <paramref name="Words"/> are the search's words that
/// are not stop words, <paramref name="Missing"/> those no shown fact holds, and <paramref name="OneHoldsAll"/> whether
/// a single shown fact holds every word. A word counts as held when a fact has it as a whole word (stemmed), or, for a
/// word of <see cref="MinPrefixLength"/> or more letters, as the start of a longer word; a look-alike spelling does not
/// count, so a search answered only by near matches reads as partial.
/// </summary>
public sealed record MemoryWordCoverage(IReadOnlyList<string> Words, IReadOnlyList<string> Missing, bool OneHoldsAll)
{
    /// <summary>The shortest word that may match as the start of a longer word, as search's own prefix rule.</summary>
    public const int MinPrefixLength = 4;

    /// <summary>Whether no shown fact holds the whole search, so the facts shown may only be near what was asked.</summary>
    public bool Partial => !OneHoldsAll;

    /// <summary>
    /// The coverage from one row per word and fact holding it: a word no fact holds comes once with no fact, and a stop
    /// word is left out. Words keep the order they first come in.
    /// </summary>
    public static MemoryWordCoverage From(IEnumerable<(string Word, bool Stop, Guid? Fact)> rows)
    {
        List<string> words = [];
        Dictionary<string, HashSet<Guid>> holders = new(StringComparer.Ordinal);
        foreach ((string word, bool stop, Guid? fact) in rows)
        {
            if (stop)
                continue;
            if (!holders.TryGetValue(word, out HashSet<Guid>? facts))
            {
                holders[word] = facts = [];
                words.Add(word);
            }
            if (fact is { } id)
                facts.Add(id);
        }

        List<string> missing = [.. words.Where(w => holders[w].Count == 0)];
        bool oneHoldsAll = words.Count == 0
            || missing.Count == 0 && words.Skip(1).Aggregate(new HashSet<Guid>(holders[words[0]]), (both, w) =>
            {
                both.IntersectWith(holders[w]);
                return both;
            }).Count > 0;
        return new MemoryWordCoverage(words, missing, oneHoldsAll);
    }
}
