namespace Connapse.CustomSource.Example;

/// <summary>The example system's own permission rules: keys a user holds, and per-document decisions.</summary>
public sealed class AccessPolicy(ExampleConfig config, LinkStore links, FolderCatalog catalog)
{
    public IReadOnlyList<string> KeysFor(string connapseUserId, string scopeId) =>
        KeysOf(links.ExternalUser(connapseUserId), scopeId);

    private IReadOnlyList<string> KeysOf(string? user, string scopeId)
    {
        if (user is null || !config.Grants.TryGetValue(user, out List<string>? keys)) return [];

        string prefix = $"folder:{scopeId}/";
        return keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// True only when the user is linked, holds the document's key, and is not denied that document.
    /// Anything unknown is false. The linked account is read once, so a relink mid-decision cannot take the
    /// grants from one account and the denies from another.
    /// </summary>
    public bool CanRead(string connapseUserId, string scopeId, string documentId)
    {
        if (!config.HasScope(scopeId)) return false;

        string? user = links.ExternalUser(connapseUserId);
        if (user is null) return false;

        ExampleDocument? document = catalog.Find(scopeId, documentId);
        if (document is null) return false;

        IReadOnlyList<string> held = KeysOf(user, scopeId);
        if (!document.AccessKeys.Any(k => held.Contains(k, StringComparer.Ordinal))) return false;

        return !(config.Denies.TryGetValue(user, out List<string>? denied)
            && denied.Contains($"{scopeId}/{document.Path}", StringComparer.Ordinal));
    }
}
