namespace Connapse.Core;

/// <summary>
/// Allowed values for <see cref="SummarySettings.ContainerSummaryMethod"/>.
/// </summary>
/// <remarks>
/// Stored as a string in <c>SummarySettings</c> to match the existing
/// <c>LlmProvider</c> / <c>LlmModel</c> convention. Validation happens in the
/// <c>SummarySettings</c> validator and rejects unknown values.
/// </remarks>
public static class SummaryStrategy
{
    /// <summary>
    /// HERCULES-style: cluster documents by mean-pooled chunk embeddings and
    /// lazy-summarize K medoid documents at rollup time. Default for new installs.
    /// </summary>
    public const string DocumentClustering = "document-clustering";

    /// <summary>
    /// Legacy: summarize every document at ingest, cluster by summary embeddings,
    /// reduce K medoid summaries. Original behavior from PR #329.
    /// </summary>
    public const string SummaryClustering = "summary-clustering";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DocumentClustering,
        SummaryClustering,
    };
}
