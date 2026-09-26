using System.Security.Cryptography;
using System.Text;
using Connapse.Eval.Model;
using Parquet.Serialization;

namespace Connapse.Eval.Datasets;

/// <summary>
/// RAGBench subsets. The corpus is every distinct document across train, validation and test
/// (train documents act as distractors); validation questions are Dev, test questions are Test.
/// A relevance key such as "3b" means sentence b of document 3 → document 3 is relevant (grade 1).
/// The keys were produced by an LLM annotator, not people.
/// </summary>
public sealed class RagBenchAdapter : IDatasetAdapter
{
    public string Name => "ragbench";

    public static string DocId(string text) =>
        "rb-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    public async Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct)
    {
        Dictionary<string, EvalDocument> corpus = new(StringComparer.Ordinal);
        List<EvalQuery> queries = [];
        Qrels qrels = new();
        Dictionary<string, (Split Split, string Question)> seenQueries = new(StringComparer.Ordinal);

        foreach ((string file, Split? split) in new (string, Split?)[] { ("train.parquet", null), ("validation.parquet", Split.Dev), ("test.parquet", Split.Test) })
        {
            IList<RagBenchRow> rows =
                (await ParquetSerializer.DeserializeAsync<RagBenchRow>(Path.Combine(directory, file), cancellationToken: ct)).Data;
            foreach (RagBenchRow row in rows)
            {
                List<string> docIds = row.Documents.Select(DocId).ToList();
                for (int i = 0; i < docIds.Count; i++)
                    corpus.TryAdd(docIds[i], new EvalDocument(docIds[i], DocumentKind.Text, null, row.Documents[i], null,
                        new Dictionary<string, string>()));

                if (split is not Split querySplit)
                    continue;

                if (seenQueries.TryGetValue(row.Id, out (Split Split, string Question) seen))
                {
                    if (seen.Split != querySplit)
                        throw new InvalidDataException($"RAGBench query ID '{row.Id}' appears in more than one split.");
                    if (seen.Question != row.Question)
                        throw new InvalidDataException($"RAGBench query ID '{row.Id}' has differing question text across rows.");
                }
                else
                {
                    seenQueries[row.Id] = (querySplit, row.Question);
                    queries.Add(new EvalQuery(row.Id, row.Question, querySplit, []));
                }

                foreach (string key in row.AllRelevantSentenceKeys ?? [])
                {
                    int index = DocumentIndex(key);
                    if (index < docIds.Count)
                        qrels.Add(row.Id, docIds[index], 1);
                }
            }
        }

        return new EvalDataset(datasetName, entry.Version, entry.Tags, corpus.Values.ToList(), queries, qrels);
    }

    private static int DocumentIndex(string key)
    {
        int length = 0;
        while (length < key.Length && char.IsAsciiDigit(key[length]))
            length++;
        if (length == 0)
            throw new FormatException($"RAGBench relevance key '{key}' does not start with a document index.");
        return int.Parse(key.AsSpan(0, length), System.Globalization.CultureInfo.InvariantCulture);
    }
}
