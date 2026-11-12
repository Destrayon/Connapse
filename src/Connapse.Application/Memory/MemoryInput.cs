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

    /// <summary>The check-by rule for a fact, as written on both surfaces (CON-340).</summary>
    public const string CheckByRule =
        "checkBy must be a date (YYYY-MM-DD), a number of days such as \"30d\" (1 to 3650), or \"never\".";

    /// <summary>
    /// When a fact should be checked again: a date or time, "&lt;n&gt;d" for n days after each save or confirm, or "never".
    /// Blank is null: the space's default for the attribute. False when it is none of these.
    /// </summary>
    public static bool TryCheckBy(string? text, out MemoryCheckBy? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        string trimmed = text.Trim();
        if (trimmed.Equals("never", StringComparison.OrdinalIgnoreCase))
        {
            value = MemoryCheckBy.Never;
            return true;
        }
        if (trimmed.Length > 1 && trimmed[^1] is 'd' or 'D' && trimmed[..^1].All(char.IsAsciiDigit))
        {
            if (!int.TryParse(trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int days)
                || days < 1 || days > MemoryCheckBy.MaxDays)
                return false;
            value = MemoryCheckBy.After(days);
            return true;
        }
        if (!TryDate(trimmed, out DateTimeOffset? at) || at is null)
            return false;
        value = new MemoryCheckBy(at, null);
        return true;
    }

    /// <summary>A check filter: "due", "fresh" or "any" in any case, blank being any; null otherwise.</summary>
    public static MemoryFactCheck? Check(string? check) => check?.Trim().ToLowerInvariant() switch
    {
        null or "" or "any" => MemoryFactCheck.Any,
        "due" => MemoryFactCheck.Due,
        "fresh" => MemoryFactCheck.Fresh,
        _ => null,
    };

    /// <summary>A correction's kind: "changed" (also the default when blank) or "wrong", in any case; null otherwise.</summary>
    public static MemoryCorrection? Correction(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        null or "" or "changed" => MemoryCorrection.Changed,
        "wrong" => MemoryCorrection.Wrong,
        _ => null,
    };
}
