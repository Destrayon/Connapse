using System.Globalization;
using Connapse.Core;

namespace Connapse.Application.Memory;

/// <summary>
/// Dates and sources as a caller writes them, read the same way by the API and the memory tools, so one value means the
/// same on both.
/// </summary>
public static class MemoryInput
{
    /// <summary>Both dates parsed (a missing one as null), or null when either is not a date. A bare date is midnight UTC.</summary>
    public static (DateTimeOffset? From, DateTimeOffset? To)? Dates(string? from, string? to) =>
        TryDate(from, out DateTimeOffset? f) && TryDate(to, out DateTimeOffset? t) ? (f, t) : null;

    /// <summary>A date or time, or null when blank; false when it is not one. Without an offset it is UTC.</summary>
    public static bool TryDate(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            return false;
        value = parsed;
        return true;
    }

    /// <summary>A recall order as a caller writes it: blank is the usual order, "newest" (any case) newest first; null otherwise.</summary>
    public static MemoryRecallOrder? Order(string? text) =>
        string.IsNullOrWhiteSpace(text) ? MemoryRecallOrder.Usual
        : string.Equals(text.Trim(), "newest", StringComparison.OrdinalIgnoreCase) ? MemoryRecallOrder.Newest
        : null;

    /// <summary>Sources as given: a UUID is a document, "node:&lt;id&gt;" a link node. Null when one is neither.</summary>
    public static IReadOnlyList<MemorySource>? Sources(IEnumerable<string?>? sources)
    {
        List<MemorySource> parsed = [];
        foreach (string? raw in sources ?? [])
        {
            string text = raw?.Trim() ?? "";
            if (text.StartsWith("node:", StringComparison.OrdinalIgnoreCase) && text.Length > 5)
                parsed.Add(new MemorySource(MemorySourceKind.Node, text[5..]));
            else if (Guid.TryParse(text, out Guid document))
                parsed.Add(new MemorySource(MemorySourceKind.Document, document.ToString()));
            else
                return null;
        }
        return parsed;
    }

    /// <summary>A correction's kind: "changed" (also the default when blank) or "wrong", in any case; null otherwise.</summary>
    public static MemoryCorrection? Correction(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" or "changed" => MemoryCorrection.Changed,
        "wrong" => MemoryCorrection.Wrong,
        _ => null,
    };
}
