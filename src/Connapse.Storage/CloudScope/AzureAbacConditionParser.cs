using System.Text.RegularExpressions;

namespace Connapse.Storage.CloudScope;

public enum AbacKind { None, PathPrefix, ContainerName, Tag, Unparseable }

public record AbacResult(
    AbacKind Kind,
    string? PathPrefix = null,
    string? ContainerName = null,
    string? TagKey = null,
    string? TagValue = null,
    bool TagKeyCaseSensitive = false,
    bool ValueCaseSensitive = false);

/// <summary>
/// Classifies the canonical Azure Blob ABAC read conditions into a path prefix, a container name, a
/// tag predicate, or Unparseable. Soundness over recall: the WHOLE condition must match one of the
/// documented read templates (an action guard <c>(!(ActionMatches{…blobs/read} [AND NOT
/// SubOperationMatches{Blob.List}])) OR (&lt;single predicate&gt;)</c>) — a fragment match is never
/// enough, because a compound/inverted expression would otherwise be read as a positive grant. Any
/// condition that is not exactly a recognized template is <see cref="AbacKind.Unparseable"/> and the
/// caller drops that grant (fail closed). A null/blank condition is <see cref="AbacKind.None"/> (an
/// unconditional grant).
/// </summary>
public static partial class AzureAbacConditionParser
{
    // Whole-condition templates: ^( (!(ActionMatches{…read} [AND NOT SubOperationMatches{Blob.List}])) OR ( <predicate> ) )$
    // Anchored so a compound/inverted/extra-clause condition cannot partially match.

    [GeneratedRegex(
        @"^\s*\(\s*\(\s*!\s*\(\s*ActionMatches\{'Microsoft\.Storage/storageAccounts/blobServices/containers/blobs/read'\}(?:\s+AND\s+NOT\s+SubOperationMatches\{'Blob\.List'\})?\s*\)\s*\)\s+OR\s+\(\s*@Resource\[Microsoft\.Storage/storageAccounts/blobServices/containers/blobs:path\]\s+(StringStartsWith|StringLike)\s+'([^']*)'\s*\)\s*\)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PathTemplate();

    [GeneratedRegex(
        @"^\s*\(\s*\(\s*!\s*\(\s*ActionMatches\{'Microsoft\.Storage/storageAccounts/blobServices/containers/blobs/read'\}(?:\s+AND\s+NOT\s+SubOperationMatches\{'Blob\.List'\})?\s*\)\s*\)\s+OR\s+\(\s*@Resource\[Microsoft\.Storage/storageAccounts/blobServices/containers:name\]\s+(StringEquals|StringEqualsIgnoreCase)\s+'([^']*)'\s*\)\s*\)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NameTemplate();

    [GeneratedRegex(
        @"^\s*\(\s*\(\s*!\s*\(\s*ActionMatches\{'Microsoft\.Storage/storageAccounts/blobServices/containers/blobs/read'\}(?:\s+AND\s+NOT\s+SubOperationMatches\{'Blob\.List'\})?\s*\)\s*\)\s+OR\s+\(\s*@Resource\[Microsoft\.Storage/storageAccounts/blobServices/containers/blobs/tags:([^<\]]+)<\$key_(case_sensitive|case_insensitive)\$>\]\s+(StringEquals|StringEqualsIgnoreCase)\s+'([^']*)'\s*\)\s*\)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TagTemplate();

    public static AbacResult Parse(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return new AbacResult(AbacKind.None);

        Match tag = TagTemplate().Match(condition);
        if (tag.Success)
            return new AbacResult(AbacKind.Tag,
                TagKey: tag.Groups[1].Value,
                TagValue: tag.Groups[4].Value,
                TagKeyCaseSensitive: tag.Groups[2].Value.Equals("case_sensitive", StringComparison.OrdinalIgnoreCase),
                ValueCaseSensitive: tag.Groups[3].Value.Equals("StringEquals", StringComparison.OrdinalIgnoreCase));

        Match path = PathTemplate().Match(condition);
        if (path.Success)
        {
            string op = path.Groups[1].Value;
            string value = path.Groups[2].Value;
            string? prefix = op.Equals("StringStartsWith", StringComparison.OrdinalIgnoreCase)
                ? value                    // literal prefix
                : SafeLikePrefix(value);   // StringLike: only "<literal>*" reduces to a prefix
            return prefix is null
                ? new AbacResult(AbacKind.Unparseable)
                : new AbacResult(AbacKind.PathPrefix, PathPrefix: prefix);
        }

        Match name = NameTemplate().Match(condition);
        if (name.Success)
            return new AbacResult(AbacKind.ContainerName, ContainerName: name.Groups[2].Value);

        return new AbacResult(AbacKind.Unparseable);
    }

    /// <summary>
    /// A <c>StringLike</c> pattern reduces to a prefix only when it is a literal followed by exactly
    /// one trailing <c>*</c> and contains no other wildcard (<c>*</c> or <c>?</c>). Anything else
    /// (an exact match with no wildcard, a mid-string wildcard) cannot be represented as a prefix
    /// safely — return null so the grant is dropped.
    /// </summary>
    private static string? SafeLikePrefix(string like)
    {
        if (!like.EndsWith('*')) return null;
        string body = like[..^1];
        return body.Contains('*') || body.Contains('?') ? null : body;
    }
}
