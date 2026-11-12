using System.Globalization;
using System.Text;
using Connapse.Core.Utilities;

namespace Connapse.Core;

/// <summary>How close a current fact is to one just saved, closest first.</summary>
public enum MemoryMatchKind
{
    /// <summary>The same subject and attribute, ignoring case, spacing and punctuation.</summary>
    SameSubjectAndAttribute = 0,

    /// <summary>The same subject, with an attribute worded differently or a value that says the same thing.</summary>
    SameSubject = 1,

    /// <summary>A subject spelled or extended differently, or one a fact describes in the new subject's words, with the same or a near attribute.</summary>
    SimilarSubject = 2,
}

/// <summary>A current fact a save may repeat or contradict, and how close it is.</summary>
public sealed record MemoryFactMatch(MemoryFact Fact, MemoryMatchKind Kind);

/// <summary>A fact's words as search stems them: its subject's, attribute's and value's.</summary>
public sealed record MemoryMatchWords(IReadOnlyList<string> Subject, IReadOnlyList<string> Attribute, IReadOnlyList<string> Value);

/// <summary>A current fact the save's search found, with its stemmed words and how well it ranked.</summary>
public sealed record MemoryMatchCandidate(MemoryFact Fact, MemoryMatchWords Words, float Rank);

/// <summary>
/// Picks, among the facts one search found, those a new fact may repeat or contradict (CON-332). It only reads: what
/// to do about a match is the saver's choice. Subjects that hold different numbers ("CON-291", "CON-292") are never
/// the same or similar, since a number usually names a different thing.
/// </summary>
public static class MemoryFactMatcher
{
    /// <summary>The most matches a save shows.</summary>
    public const int MaxMatches = 3;

    /// <summary>The least trigram similarity at which two attributes count as one reworded.</summary>
    internal const double AttributeTrigram = 0.4;

    /// <summary>The least trigram similarity at which two subjects count as one spelled differently.</summary>
    internal const double SubjectTrigram = 0.6;

    /// <summary>The shortest text that may count as part of a longer one, or as the start of a longer word.</summary>
    internal const int MinPartLength = 4;

    public static IReadOnlyList<MemoryFactMatch> Match(
        MemoryFact saved, MemoryMatchWords words, IEnumerable<MemoryMatchCandidate> candidates)
    {
        string subject = Compact(saved.Subject), attribute = Compact(saved.Attribute);
        if (subject.Length == 0)
            return [];
        List<MemoryMatchCandidate> others = [.. candidates.Where(c => c.Fact.Id != saved.Id && c.Fact.ChainId != saved.ChainId)];

        // A fact whose value holds every word of the new subject describes its own subject by it: "SSO gateway" is Wicket.
        HashSet<string> aliases = new(StringComparer.Ordinal);
        if (words.Subject.Count > 0)
        {
            foreach (MemoryMatchCandidate c in others.Where(c => words.Subject.All(w => c.Words.Value.Contains(w))))
                aliases.Add(Compact(c.Fact.Subject));
        }

        List<(MemoryMatchCandidate Candidate, MemoryMatchKind Kind, double Closeness)> matches = [];
        foreach (MemoryMatchCandidate c in others)
        {
            string theirSubject = Compact(c.Fact.Subject), theirAttribute = Compact(c.Fact.Attribute);
            double closeness = attribute == theirAttribute ? 1 : AttributeCloseness(attribute, words.Attribute, theirAttribute, c.Words.Attribute);
            if (theirSubject == subject)
            {
                if (attribute == theirAttribute)
                    matches.Add((c, MemoryMatchKind.SameSubjectAndAttribute, 1));
                else if (closeness > 0 || ValueNear(words.Value, c.Words.Value))
                    matches.Add((c, MemoryMatchKind.SameSubject, closeness));
            }
            else if (closeness > 0 && (SubjectSimilar(saved.Subject, subject, words.Subject, c.Fact.Subject, theirSubject, c.Words.Subject)
                || aliases.Contains(theirSubject)))
            {
                matches.Add((c, MemoryMatchKind.SimilarSubject, closeness));
            }
        }

        return [.. matches
            .OrderBy(m => m.Kind)
            .ThenByDescending(m => m.Closeness)
            .ThenByDescending(m => m.Candidate.Rank)
            .ThenByDescending(m => m.Candidate.Fact.RecordedAt)
            .ThenBy(m => m.Candidate.Fact.Id)
            .Take(MaxMatches)
            .Select(m => new MemoryFactMatch(m.Candidate.Fact, m.Kind))];
    }

    /// <summary>Lower-cased letters and digits only, so "CON-291", "con 291" and "CON291" read alike.</summary>
    internal static string Compact(string text)
    {
        StringBuilder compact = new(text.Length);
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch))
                compact.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }
        return compact.ToString();
    }

    /// <summary>The runs of digits in <paramref name="text"/>, in order: "Release 0.5.0" holds 0, 5, 0.</summary>
    internal static string Numbers(string text)
    {
        StringBuilder numbers = new();
        bool inRun = false;
        foreach (char ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                numbers.Append(ch);
                inRun = true;
            }
            else if (inRun)
            {
                numbers.Append(' ');
                inRun = false;
            }
        }
        return numbers.ToString().TrimEnd();
    }

    /// <summary>
    /// How close two attributes are, from 0 (not near) to 1: one holds the other ("status", "current status"), they
    /// share a word stem ("tech lead", "lead engineer"), or they are spelled alike ("state", "status").
    /// </summary>
    internal static double AttributeCloseness(string mine, IReadOnlyList<string> myWords, string theirs, IReadOnlyList<string> theirWords)
    {
        if (mine.Length == 0 || theirs.Length == 0)
            return 0;
        if (mine == theirs)
            return 1;
        double trigram = Trigrams.Similarity(mine, theirs);
        double shared = Jaccard(myWords, theirWords);
        double best = Math.Max(trigram >= AttributeTrigram ? trigram : 0, shared);
        if (Holds(mine, theirs))
            best = Math.Max(best, 0.9);
        return best;
    }

    /// <summary>
    /// Whether two different subjects name the same thing: the same numbers, and one holds the other ("Halcyon API"),
    /// they share most words, they are one typo apart ("Halycon"), or they are spelled alike.
    /// </summary>
    internal static bool SubjectSimilar(
        string mine, string myCompact, IReadOnlyList<string> myWords, string theirs, string theirCompact, IReadOnlyList<string> theirWords)
    {
        if (myCompact.Length == 0 || theirCompact.Length == 0 || Numbers(mine) != Numbers(theirs))
            return false;
        return Holds(myCompact, theirCompact)
            || Jaccard(myWords, theirWords) >= 0.5
            || (Math.Min(myCompact.Length, theirCompact.Length) > MinPartLength && OneEditApart(myCompact, theirCompact))
            || Trigrams.Similarity(myCompact, theirCompact) >= SubjectTrigram;
    }

    /// <summary>
    /// Whether two values say much the same thing: at least half the shorter one's words are in the other, counting a
    /// word as present when it is there whole, as the start of a longer word ("postgr", "postgresql"), or spelled
    /// alike; and at least one of those words is not a number.
    /// </summary>
    internal static bool ValueNear(IReadOnlyList<string> mine, IReadOnlyList<string> theirs)
    {
        if (mine.Count == 0 || theirs.Count == 0)
            return false;
        IReadOnlyList<string> shorter = mine.Count <= theirs.Count ? mine : theirs, longer = ReferenceEquals(shorter, mine) ? theirs : mine;
        List<string> present = [.. shorter.Where(w => longer.Any(o => WordNear(w, o)))];
        return present.Count * 2 >= shorter.Count && present.Any(w => w.Length >= 3 && !w.All(char.IsAsciiDigit));
    }

    private static bool WordNear(string a, string b)
    {
        if (a == b)
            return true;
        if (a.Length < MinPartLength || b.Length < MinPartLength)
            return false;
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)
            || Trigrams.Similarity(a, b) >= 0.5;
    }

    /// <summary>Whether one holds the other and the shorter is long enough to mean something on its own.</summary>
    private static bool Holds(string a, string b)
    {
        (string shorter, string longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= MinPartLength && longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static double Jaccard(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        HashSet<string> both = new(a, StringComparer.Ordinal);
        int shared = b.Distinct(StringComparer.Ordinal).Count(both.Contains);
        both.UnionWith(b);
        return (double)shared / both.Count;
    }

    /// <summary>Whether one insertion, deletion, substitution or swap of neighbours turns one into the other.</summary>
    internal static bool OneEditApart(string a, string b)
    {
        if (a == b)
            return true;
        if (Math.Abs(a.Length - b.Length) > 1)
            return false;
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
            start++;
        if (a.Length == b.Length)
        {
            if (a.AsSpan(start + 1).SequenceEqual(b.AsSpan(start + 1)))
                return true;
            return start + 1 < a.Length && a[start] == b[start + 1] && a[start + 1] == b[start]
                && a.AsSpan(start + 2).SequenceEqual(b.AsSpan(start + 2));
        }
        (string shorter, string longer) = a.Length < b.Length ? (a, b) : (b, a);
        return shorter.AsSpan(start).SequenceEqual(longer.AsSpan(start + 1));
    }
}

/// <summary>What the save's one search found: the saved fact's own words, and the current facts that share any of them or its subject.</summary>
public sealed record MemoryMatchSearch(MemoryMatchWords Saved, IReadOnlyList<MemoryMatchCandidate> Candidates)
{
    public static MemoryMatchSearch None { get; } = new(new([], [], []), []);
}
