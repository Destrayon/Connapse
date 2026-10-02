namespace Connapse.Eval.Model;

/// <summary>Text is uploaded as a .txt file; File uploads the bytes at FilePath under its own extension.</summary>
public enum DocumentKind { Text, Image, File }

public enum Split { Dev, Test }

public sealed record EvalDocument(
    string Id,
    DocumentKind Kind,
    string? Title,
    string? Text,
    string? ImagePath,
    IReadOnlyDictionary<string, string> Metadata,
    string? FilePath = null);

public sealed record EvalQuery(string Id, string Text, Split Split, IReadOnlyList<string> Tags);

public sealed record EvalDataset(
    string Name,
    string Version,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EvalDocument> Corpus,
    IReadOnlyList<EvalQuery> Queries,
    Qrels Qrels)
{
    /// <summary>
    /// Passage-level gold by query ID. When set, the runner ranks retrieved chunks instead of
    /// documents and judges each one against its query's evidence.
    /// </summary>
    public IReadOnlyDictionary<string, PassageGold>? Passages { get; init; }
}

public sealed record RankedDoc(string DocId, double Score);

/// <summary>
/// The passage that answers a query: a chunk of <paramref name="DocId"/> containing every evidence
/// string, with at most <paramref name="MaxGapWords"/> other words between them, so a label and a
/// value that a parser scattered apart do not count as kept together.
/// </summary>
public sealed record PassageGold(string DocId, IReadOnlyList<string> Evidence, string Answer, int MaxGapWords = int.MaxValue);
