using System.Text.Json.Serialization;
using Connapse.Eval.Model;
using Parquet;
using Parquet.Serialization;

namespace Connapse.Eval.Datasets;

public sealed class EnterpriseDocumentRow
{
    [JsonPropertyName("doc_id")] public string DocId { get; set; } = "";
    [JsonPropertyName("source_type")] public string SourceType { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

public sealed class EnterpriseQuestionRow
{
    [JsonPropertyName("question_id")] public string QuestionId { get; set; } = "";
    [JsonPropertyName("question_type")] public string QuestionType { get; set; } = "";
    [JsonPropertyName("source_types")] public List<string>? SourceTypes { get; set; }
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("expected_doc_ids")] public List<string>? ExpectedDocIds { get; set; }
}

/// <summary>
/// EnterpriseRAG-Bench (Onyx, MIT; #662): about 500,000 generated documents of one fictional
/// company from Slack, Gmail, Linear, Google Drive, HubSpot, Fireflies, GitHub, Jira and
/// Confluence, and 500 questions with their gold documents.
/// <list type="bullet">
/// <item>Each question is tagged <c>kind:{question_type}</c> and <c>source:{source}</c> for every
/// source its answer is in, so scores break down by both.</item>
/// <item>Questions with no gold documents ("high level", "info not found") are graded on generated
/// answers by the benchmark; Connapse is search, so they are left out.</item>
/// </list>
/// The corpus is the one eval/tools/build_enterprise_suite.py wrote: the whole corpus, or a
/// 50,000-document subset (gold, BM25-pooled distractors, hash-chosen fill), in 10,000-row groups,
/// read one group at a time.
/// </summary>
public sealed class EnterpriseRagBenchAdapter : IDatasetAdapter
{
    public const string DocumentsFile = "documents.parquet";
    public const string QuestionsFile = "questions.parquet";

    public string Name => "enterprise-rag-bench";

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        var questions = (await ParquetSerializer.DeserializeAsync<EnterpriseQuestionRow>(
            Path.Combine(directory, QuestionsFile), cancellationToken: ct)).Data;

        var scored = questions.Where(q => q.ExpectedDocIds is { Count: > 0 }).ToList();
        HashSet<string> gold = scored.SelectMany(q => q.ExpectedDocIds!).ToHashSet(StringComparer.Ordinal);

        var corpus = new List<EvalDocument>();
        HashSet<string> seen = new(StringComparer.Ordinal);
        await using (FileStream stream = File.OpenRead(Path.Combine(directory, DocumentsFile)))
        {
            int groups;
            await using (ParquetReader reader = await ParquetReader.CreateAsync(stream, leaveStreamOpen: true, cancellationToken: ct))
                groups = reader.RowGroupCount;
            for (int g = 0; g < groups; g++)
            {
                ct.ThrowIfCancellationRequested();
                stream.Position = 0;
                var rows = (await ParquetSerializer.DeserializeAsync<EnterpriseDocumentRow>(stream, rowGroupIndex: g, cancellationToken: ct)).Data;
                foreach (EnterpriseDocumentRow row in rows)
                {
                    if (!seen.Add(row.DocId))
                        throw new InvalidDataException($"Dataset '{datasetName}' has duplicate document ID '{row.DocId}'.");
                    corpus.Add(new EvalDocument(row.DocId, DocumentKind.Text,
                        string.IsNullOrWhiteSpace(row.Title) ? null : row.Title, row.Content ?? "", null,
                        new Dictionary<string, string> { ["source"] = row.SourceType }));
                }
            }
        }

        var missing = gold.Where(id => !seen.Contains(id)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"Dataset '{datasetName}': {missing.Count} gold documents are not in the corpus (for example '{missing[0]}').");

        Qrels qrels = new();
        var queries = new List<EvalQuery>();
        foreach (EnterpriseQuestionRow q in scored)
        {
            foreach (string id in q.ExpectedDocIds!)
                qrels.Add(q.QuestionId, id, 1);
            var tags = new List<string> { $"kind:{q.QuestionType}" };
            tags.AddRange((q.SourceTypes ?? []).Distinct(StringComparer.Ordinal).Select(s => $"source:{s}"));
            queries.Add(new EvalQuery(q.QuestionId, q.Question, Split.Test, tags));
        }

        return new EvalDataset(datasetName, entry.Version, entry.Tags, corpus, queries, qrels);
    }
}
