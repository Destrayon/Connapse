namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire queue name constants. Job classes reference these via [Queue] attribute.
/// Worker pool separation (Onyx-style) — bounded-duration ingestion vs variable-duration LLM calls.
/// </summary>
public static class JobQueues
{
    public const string Ingestion = "ingestion";
    public const string Summarization = "summarization";
    public const string Default = "default";
}
