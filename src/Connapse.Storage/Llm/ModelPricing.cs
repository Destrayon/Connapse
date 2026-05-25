// Last verified: 2026-05-24
// Pricing is freshness-volatile. Verify against vendor pricing pages
// before relying on cost estimates in production.

namespace Connapse.Storage.Llm;

public static class ModelPricing
{
    public readonly record struct Pricing(
        decimal InputPricePerMillionTokens,
        decimal OutputPricePerMillionTokens);

    private static readonly Dictionary<string, Pricing> _rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-4-5"]   = new(1.00m, 5.00m),
        ["claude-sonnet-4-6"]  = new(3.00m, 15.00m),
        ["claude-opus-4-7"]    = new(15.00m, 75.00m),
        ["gpt-4.1-nano"]       = new(0.10m, 0.40m),
        ["gpt-4.1-mini"]       = new(0.40m, 1.60m),
        ["gemini-2.5-flash"]   = new(0.30m, 2.50m),
        ["gemini-2.5-pro"]     = new(1.25m, 10.00m),
        ["mistral-small-3"]    = new(0.10m, 0.30m),
        ["llama-3.3-70b"]      = new(0.59m, 0.79m),
    };

    public static Pricing Get(string modelId) =>
        _rates.TryGetValue(modelId, out Pricing p) ? p : new Pricing(0, 0);

    public static decimal EstimateCostUsd(string modelId, int inputTokens, int outputTokens)
    {
        Pricing p = Get(modelId);
        return (inputTokens / 1_000_000m) * p.InputPricePerMillionTokens
             + (outputTokens / 1_000_000m) * p.OutputPricePerMillionTokens;
    }
}
