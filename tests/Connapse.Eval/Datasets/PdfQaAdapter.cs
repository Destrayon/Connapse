using System.Text.Json;
using Connapse.Eval.Metrics;
using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// Questions over real PDFs (#643). Every .pdf file in the entry is a document; questions.jsonl
/// holds one question per line, with the PDF that answers it, the evidence strings the answering
/// passage must contain, the answer, the page it was read from, and its kind (table-ruled, scan-typed,
/// ...), which becomes a "kind:" tag the scores are broken down by.
/// </summary>
public sealed class PdfQaAdapter : IDatasetAdapter
{
    public const string QuestionsFile = "questions.jsonl";

    public string Name => "pdf-qa";

    /// <summary>
    /// Question kinds and how many other words may sit between a question's evidence strings. A table
    /// row's label and cell stay within a few cells of each other; prose evidence spans a sentence or
    /// a column wrap.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Kinds = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["table-ruled"] = 15, ["table-borderless"] = 15,
        ["columns"] = 60, ["header-break"] = 60, ["scan-typed"] = 60, ["scan-degraded"] = 60,
    };

    private sealed record QuestionLine(
        string Id, string Question, string Doc, IReadOnlyList<string> Evidence, string Answer, int? Page, string? Kind,
        int? MaxGapWords);

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        List<EvalDocument> corpus = entry.Files
            .Where(f => f.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => new EvalDocument(f.Name, DocumentKind.File, Path.GetFileNameWithoutExtension(f.Name), null, null,
                new Dictionary<string, string>(), Path.Combine(directory, f.Name)))
            .ToList();
        HashSet<string> documents = corpus.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        List<EvalQuery> queries = [];
        Dictionary<string, PassageGold> passages = new(StringComparer.Ordinal);
        Qrels qrels = new();
        int lineNumber = 0;
        foreach (string line in await File.ReadAllLinesAsync(Path.Combine(directory, QuestionsFile), ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            QuestionLine q = JsonSerializer.Deserialize<QuestionLine>(line, EvalJson.Options)
                ?? throw new InvalidDataException($"{datasetName}/{QuestionsFile} line {lineNumber} is empty.");
            string where = $"{datasetName}/{QuestionsFile} line {lineNumber} ({q.Id})";
            if (string.IsNullOrWhiteSpace(q.Id) || string.IsNullOrWhiteSpace(q.Question) || string.IsNullOrWhiteSpace(q.Answer))
                throw new InvalidDataException($"{where} needs an id, a question and an answer.");
            if (!documents.Contains(q.Doc))
                throw new InvalidDataException($"{where} names '{q.Doc}', which is not a PDF in the dataset.");
            if (q.Evidence is not { Count: > 0 } || q.Evidence.Any(e => PassageJudge.Normalize(e).Length == 0))
                throw new InvalidDataException($"{where} needs evidence strings with words in them.");
            if (q.Kind is null || !Kinds.TryGetValue(q.Kind, out int maxGap))
                throw new InvalidDataException($"{where} needs a kind: one of {string.Join(", ", Kinds.Keys)}.");
            // A row whose cells are prose puts more words between label and value; such a question
            // sets its own gap, measured on the intact row.
            if (!passages.TryAdd(q.Id, new PassageGold(q.Doc, q.Evidence, q.Answer, q.MaxGapWords ?? maxGap)))
                throw new InvalidDataException($"{where} repeats a question ID.");
            queries.Add(new EvalQuery(q.Id, q.Question, Split.Test, [$"kind:{q.Kind}"]));
            qrels.Add(q.Id, q.Doc, 1);
        }

        return new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, queries, qrels) { Passages = passages };
    }
}
