namespace Connapse.Core;

/// <summary>
/// Evaluates a blob's index tags against an <see cref="AzureTagCondition"/> (an RBAC ABAC grant that
/// could not reduce to a prefix, carried as residue by 4b). Pure; used by the Phase 4e verifier per
/// hit. A missing key fails closed.
/// </summary>
public static class AzureTagConditionEvaluator
{
    public static bool Matches(AzureTagCondition condition, IReadOnlyDictionary<string, string> blobTags)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(blobTags);

        string? value = FindValue(condition.TagKey, condition.KeyCaseSensitive, blobTags);
        if (value is null)
            return false;

        StringComparison valueCmp = condition.ValueCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        return string.Equals(value, condition.TagValue, valueCmp);
    }

    private static string? FindValue(string key, bool keyCaseSensitive, IReadOnlyDictionary<string, string> tags)
    {
        if (keyCaseSensitive)
            return tags.TryGetValue(key, out string? v) ? v : null;

        foreach (KeyValuePair<string, string> t in tags)
            if (string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase))
                return t.Value;
        return null;
    }
}
