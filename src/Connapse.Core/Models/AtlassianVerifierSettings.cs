namespace Connapse.Core;

/// <summary>Tuning for the per-hit Confluence permission verifier.</summary>
public sealed class AtlassianVerifierSettings
{
    public const string SectionName = "Atlassian:Verifier";

    /// <summary>How many candidates to over-fetch per requested result, so backfill has material.</summary>
    public int CandidateMultiplier { get; set; } = 3;

    /// <summary>How many permission checks run at once in one search.</summary>
    public int MaxParallelism { get; set; } = 10;

    /// <summary>The total time one search may spend on checks; anything unchecked by then is denied.</summary>
    public int BudgetMs { get; set; } = 3000;
}
