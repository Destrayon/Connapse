using System.Text.Json.Serialization;

namespace Connapse.Eval.Datasets;

// Public so Parquet.Net's compiled accessors and the test fixtures can both use them.

public sealed class BeirCorpusRow
{
    [JsonPropertyName("_id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

public sealed class BeirQueryRow
{
    [JsonPropertyName("_id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

public sealed class BeirQrelRow
{
    [JsonPropertyName("query-id")] public string QueryId { get; set; } = "";
    [JsonPropertyName("corpus-id")] public string CorpusId { get; set; } = "";
    [JsonPropertyName("score")] public long? Score { get; set; }
}

public sealed class RagBenchRow
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("documents")] public List<string> Documents { get; set; } = [];
    [JsonPropertyName("all_relevant_sentence_keys")] public List<string>? AllRelevantSentenceKeys { get; set; }
}
