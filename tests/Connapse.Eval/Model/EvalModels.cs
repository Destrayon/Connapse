namespace Connapse.Eval.Model;

public enum DocumentKind { Text, Image }

public enum Split { Dev, Test }

public sealed record EvalDocument(
    string Id,
    DocumentKind Kind,
    string? Title,
    string? Text,
    string? ImagePath,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record EvalQuery(string Id, string Text, Split Split, IReadOnlyList<string> Tags);

public sealed record EvalDataset(
    string Name,
    string Version,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EvalDocument> Corpus,
    IReadOnlyList<EvalQuery> Queries,
    Qrels Qrels);

public sealed record RankedDoc(string DocId, double Score);
