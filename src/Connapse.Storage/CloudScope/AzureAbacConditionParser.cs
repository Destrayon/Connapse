using System.Text.RegularExpressions;

namespace Connapse.Storage.CloudScope;

public enum AbacKind { None, PathPrefix, ContainerName, Tag, Unparseable }

public record AbacResult(
    AbacKind Kind,
    string? PathPrefix = null,
    string? ContainerName = null,
    string? TagKey = null,
    string? TagValue = null,
    bool TagKeyCaseSensitive = false);

/// <summary>
/// Classifies the canonical Azure Blob ABAC read conditions into a prefix, a container name, a tag
/// predicate, or Unparseable. Only the documented read templates are understood; anything else is
/// Unparseable and the caller drops that grant (fail closed). A null/blank condition is None (an
/// unconditional grant).
/// </summary>
public static partial class AzureAbacConditionParser
{
    [GeneratedRegex(@"blobServices/containers/blobs:path\]\s+String(?:Like|StartsWith)\s+'([^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"blobServices/containers:name\]\s+StringEquals\s+'([^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"blobServices/containers/blobs/tags:([^<\]]+)<\$key_(case_sensitive|case_insensitive)\$>\]\s+StringEquals\s+'([^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"@(?:Resource|Request|Environment|Principal)\[", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeRefRegex();

    public static AbacResult Parse(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return new AbacResult(AbacKind.None);

        // Only the canonical SINGLE-attribute read template is understood. A compound condition —
        // more than one attribute reference (a path AND a tag, or a recognized clause AND an
        // unrecognized/restrictive one) — must never be partially honored, or a restrictive clause
        // would be silently dropped and the grant over-broadened. Require exactly one attribute
        // reference; anything else is Unparseable and the caller drops the grant (fail closed).
        if (AttributeRefRegex().Matches(condition).Count != 1)
            return new AbacResult(AbacKind.Unparseable);

        Match tag = TagRegex().Match(condition);
        if (tag.Success)
            return new AbacResult(AbacKind.Tag,
                TagKey: tag.Groups[1].Value,
                TagValue: tag.Groups[3].Value,
                TagKeyCaseSensitive: string.Equals(tag.Groups[2].Value, "case_sensitive", StringComparison.OrdinalIgnoreCase));

        Match path = PathRegex().Match(condition);
        if (path.Success)
        {
            string p = path.Groups[1].Value;
            if (p.EndsWith('*')) p = p[..^1];
            return new AbacResult(AbacKind.PathPrefix, PathPrefix: p);
        }

        Match name = NameRegex().Match(condition);
        if (name.Success)
            return new AbacResult(AbacKind.ContainerName, ContainerName: name.Groups[1].Value);

        return new AbacResult(AbacKind.Unparseable);
    }
}
