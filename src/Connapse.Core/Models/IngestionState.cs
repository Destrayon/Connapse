namespace Connapse.Core;

/// <summary>
/// Multi-stage state of a document in the ingestion + enrichment pipeline.
/// Mirrors Ragie's lifecycle: searchability and summarization are separate terminal states.
/// </summary>
public enum IngestionState
{
    /// <summary>Doc row created; ingestion job not yet run (or in-flight).</summary>
    Pending = 0,

    /// <summary>Parse + chunk + embed + save complete; doc is searchable.</summary>
    Indexed = 1,

    /// <summary>Per-doc summary stored on the document row.</summary>
    SummaryIndexed = 2,

    /// <summary>Ingestion job exhausted retries. UI shows red pill + retry button.</summary>
    Failed = 3
}
