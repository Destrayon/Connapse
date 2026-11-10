using System.Globalization;
using Connapse.Core;

namespace Connapse.Search.Hybrid;

/// <summary>
/// Whether a search found anything that matches the query, from the raw cosine similarity of its best
/// vector match (CON-278). Hybrid fusion normalises each query's candidates to [0, 1], so its best hit
/// scores about 1 whether it answers the query or not; the raw cosine is what that normalisation removed.
/// </summary>
/// <remarks>
/// A query whose best cosine falls below the embedding model's cutoff is flagged as having no good match, and
/// every score it returns is multiplied by one factor (<see cref="Factor"/>), so its order is unchanged. Queries
/// at or above the cutoff keep their scores. A cutoff is only meaningful for the model it was measured with, so
/// each model has its own (<see cref="CutoffFor"/>); a model without one is never flagged and keeps its scores.
/// The rule that sets a cutoff, and the numbers behind the built-in ones, are in eval/pass-bars.md.
/// </remarks>
internal static class ScoreConfidence
{
    public const string RawVectorKey = "rawVectorScore";
    public const string RawKeywordKey = "rawKeywordScore";

    /// <summary>Factor for a query whose best cosine is just under the cutoff.</summary>
    internal const float FactorAtCutoff = 0.79f;

    /// <summary>Factor for a query whose best cosine is at or below the floor point, the least it falls to.</summary>
    internal const float LowestFactor = 0.3f;

    /// <summary>
    /// Built-in cutoffs, by model name, for the models' published recipes (<see cref="EmbeddingSettings.UseModelPrefixes"/>).
    /// Cutoff: the 5th percentile of the best cosine over eval questions whose best hit is relevant. Floor point:
    /// the median best cosine over questions asked of an unrelated corpus.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, NoMatchCutoff> BuiltIn =
        new Dictionary<string, NoMatchCutoff>(StringComparer.OrdinalIgnoreCase)
        {
            ["nomic-embed-text"] = new(0.69f, 0.61f),
        };

    /// <summary>
    /// The cutoff for the configured embedding model: the search settings' own when set (a cutoff of zero or less
    /// turns the flag off), otherwise the built-in one for the model's published recipe, otherwise none.
    /// </summary>
    public static NoMatchCutoff? CutoffFor(EmbeddingSettings embedding, SearchSettings search)
    {
        if (search.NoMatchCutoff is double cutoff)
        {
            if (cutoff <= 0)
                return null;
            double floor = search.NoMatchFloor ?? cutoff - DefaultFloorDistance;
            return new NoMatchCutoff((float)cutoff, (float)Math.Min(floor, cutoff));
        }

        // Custom prefixes move every vector, so a cutoff measured with the published recipe no longer applies.
        if (!embedding.UseModelPrefixes)
            return null;
        return BuiltIn.TryGetValue(ModelName(embedding.Model), out NoMatchCutoff? builtIn) ? builtIn : null;
    }

    /// <summary>Floor point below a configured cutoff when none is given: nomic-embed-text's distance.</summary>
    private const double DefaultFloorDistance = 0.08;

    /// <summary>
    /// The factor every score of a query is multiplied by: 1 at or above the cutoff, <see cref="FactorAtCutoff"/>
    /// just under it, falling linearly to <see cref="LowestFactor"/> at the floor point and staying there.
    /// </summary>
    public static float Factor(float bestCosine, NoMatchCutoff cutoff)
    {
        if (bestCosine >= cutoff.Cutoff)
            return 1f;
        float span = cutoff.Cutoff - cutoff.Floor;
        float t = span <= 0 ? 1f : Math.Clamp((cutoff.Cutoff - bestCosine) / span, 0f, 1f);
        return FactorAtCutoff - t * (FactorAtCutoff - LowestFactor);
    }

    /// <summary>The best cosine among <paramref name="hits"/>, or null when none carries one.</summary>
    public static float? BestCosine(IEnumerable<SearchHit> hits, Func<SearchHit, float?> cosine) =>
        hits.Select(cosine).Max();

    /// <summary>The raw cosine a fused hit carries in its metadata.</summary>
    public static float? FusedCosine(SearchHit hit) =>
        hit.Metadata.TryGetValue(RawVectorKey, out string? raw)
        && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;

    /// <summary>The model name without Ollama's default <c>:latest</c> tag.</summary>
    internal static string ModelName(string model)
    {
        string name = model.Trim();
        return name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^":latest".Length] : name;
    }
}

/// <summary>
/// An embedding model's no-match cutoff on the best cosine, and the floor point where the score factor stops falling.
/// </summary>
internal sealed record NoMatchCutoff(float Cutoff, float Floor);
