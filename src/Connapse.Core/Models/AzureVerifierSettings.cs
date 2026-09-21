namespace Connapse.Core;

/// <summary>Tuning for the Phase 4e verifier.</summary>
public sealed class AzureVerifierSettings
{
    public const string SectionName = "Azure:Verifier";
    /// <summary>Bounded per-hit verify concurrency.</summary>
    public int MaxParallelism { get; set; } = 16;
    /// <summary>How many candidates to over-fetch per requested result, so backfill has material.</summary>
    public int CandidateMultiplier { get; set; } = 5;
}
