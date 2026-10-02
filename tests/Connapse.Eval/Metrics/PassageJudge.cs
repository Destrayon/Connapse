using System.Text;
using System.Text.RegularExpressions;
using Connapse.Eval.Model;
using Connapse.Eval.Systems;

namespace Connapse.Eval.Metrics;

/// <summary>
/// Turns a query's retrieved chunks into a ranking plus judgments the ordinary ranking metrics score.
/// The first chunk of the gold document that contains every evidence string is the one relevant
/// passage; later chunks that also contain it (overlap) are judged not relevant, so a query never has
/// more than one relevant item. When no retrieved chunk qualifies, a placeholder stands for the
/// unretrieved gold passage, so recall is 0 rather than the query being dropped as unanswerable.
/// </summary>
public static partial class PassageJudge
{
    public static string PassageId(RetrievedPassage passage) => $"{passage.DocId}#{passage.ChunkId}";

    public static string MissingId(PassageGold gold) => $"{gold.DocId}#gold";

    /// <summary>The document a passage ID belongs to.</summary>
    public static string DocumentOf(string passageId)
    {
        int split = passageId.LastIndexOf('#');
        return split < 0 ? passageId : passageId[..split];
    }

    public static (IReadOnlyList<RankedDoc> Ranked, IReadOnlyDictionary<string, int> Judgments) Judge(
        PassageGold gold, IReadOnlyList<RetrievedPassage> passages)
    {
        List<string> evidence = gold.Evidence.Select(Normalize).ToList();
        List<RankedDoc> ranked = [];
        Dictionary<string, int> judgments = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool found = false;
        foreach (RetrievedPassage passage in passages)
        {
            string id = PassageId(passage);
            if (!seen.Add(id))
                continue;
            // Scores fall with rank, so the metrics' score ordering is the retrieval order.
            ranked.Add(new RankedDoc(id, passages.Count - ranked.Count));
            if (passage.DocId != gold.DocId)
                continue;
            bool relevant = !found && ContainsAll(passage.Content, evidence);
            found |= relevant;
            judgments[id] = relevant ? 1 : 0;
        }
        if (!found)
            judgments[MissingId(gold)] = 1;
        return (ranked, judgments);
    }

    public static bool ContainsAll(string text, IReadOnlyList<string> normalizedEvidence)
    {
        string haystack = $" {Normalize(text)} ";
        return normalizedEvidence.All(e => haystack.Contains($" {e} ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Case, compatibility forms (ligatures, full-width digits), line-break hyphenation, thousands
    /// separators and every kind of punctuation or markup are ignored; words must match whole.
    /// </summary>
    public static string Normalize(string text)
    {
        string s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        s = LineBreakHyphen().Replace(s, "");
        s = ThousandsSeparator().Replace(s, "");
        s = NonDecimalPoint().Replace(s, " ");
        s = Punctuation().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex(@"(?<=\p{L})-[ \t]*\r?\n\s*(?=\p{Ll})")]
    private static partial Regex LineBreakHyphen();

    [GeneratedRegex(@"(?<=\d),(?=\d{3}(?!\d))")]
    private static partial Regex ThousandsSeparator();

    [GeneratedRegex(@"\.(?!\d)|(?<!\d)\.")]
    private static partial Regex NonDecimalPoint();

    [GeneratedRegex(@"[^\p{L}\p{N}.]+")]
    private static partial Regex Punctuation();
}
