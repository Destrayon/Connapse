using System.Globalization;
using Connapse.Core;

namespace Connapse.Search.Hybrid;

/// <summary>
/// How well a search's best results match the query, from the raw cosine similarities that hybrid fusion
/// rescales away (CON-278). Fusion normalises each query's candidates to [0, 1], so its best hit scores
/// about 1 whether it answers the query or not; this puts back what the normalisation removed.
/// </summary>
/// <remarks>
/// Two signals, both from the query's ten best hits. How similar the best one is: an unrelated corpus has
/// nothing close to the query. And how far it stands above the rest: when nothing answers a query, every
/// candidate is about as (dis)similar as the others, while a real answer stands out ("Magnitude Mirage",
/// arXiv 2510.16012, found the top-1 minus top-k gap a better no-answer signal than the top score alone).
/// The weights were fitted on the eval's dev split with nomic-embed-text, the default embedding model; see
/// eval/pass-bars.md.
/// </remarks>
internal static class ScoreConfidence
{
    public const string RawVectorKey = "rawVectorScore";
    public const string RawKeywordKey = "rawKeywordScore";

    /// <summary>Hits the signals are read from, however many the caller asked for.</summary>
    public const int Window = 10;

    /// <summary>Below this confidence the search reports that nothing matched well.</summary>
    public const float NoMatchThreshold = 0.5f;

    // Logistic weights over (best cosine, best cosine minus the window's mean cosine), fitted by Newton's method
    // with an L2 penalty of 1 on the slopes and balanced classes: 448 dev queries whose best hit is relevant
    // against 767 asked of an unrelated container. Confidence crosses 0.5 near a best cosine of 0.72.
    internal const double Bias = -10.3273;
    internal const double TopWeight = 14.1187;
    internal const double GapWeight = 2.0139;

    /// <summary>
    /// Confidence in [0, 1] that the best of <paramref name="ranked"/> answers the query, or null when no hit
    /// carries a cosine similarity (a keyword-only search, or a container without vectors for the model).
    /// </summary>
    /// <param name="ranked">Hits in rank order.</param>
    /// <param name="cosine">A hit's raw cosine similarity, or null when it has none.</param>
    public static float? Of(IReadOnlyList<SearchHit> ranked, Func<SearchHit, float?> cosine)
    {
        List<float> sims = ranked.Take(Window).Select(cosine).OfType<float>().ToList();
        if (sims.Count == 0)
            return null;
        double top = sims.Max();
        double gap = top - sims.Average();
        double z = Bias + TopWeight * top + GapWeight * gap;
        return (float)(1 / (1 + Math.Exp(-z)));
    }

    /// <summary>The raw cosine a fused hit carries in its metadata.</summary>
    public static float? FusedCosine(SearchHit hit) =>
        hit.Metadata.TryGetValue(RawVectorKey, out string? raw)
        && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : null;
}
