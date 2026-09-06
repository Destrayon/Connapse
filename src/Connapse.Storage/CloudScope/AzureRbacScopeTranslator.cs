namespace Connapse.Storage.CloudScope;

/// <summary>
/// Translates an ARM role-assignment scope into the <c>azblob://</c> prefix it governs. Resource
/// provider and type segments are matched case-insensitively (ARM is case-insensitive on them);
/// the account and container names keep their original case (they are the resource_uri content).
/// </summary>
public static class AzureRbacScopeTranslator
{
    public static string ToAzblobPrefix(string armScope)
    {
        string[] parts = armScope.Split('/', StringSplitOptions.RemoveEmptyEntries);

        int acctIdx = Array.FindIndex(parts,
            p => string.Equals(p, "storageAccounts", StringComparison.OrdinalIgnoreCase));
        if (acctIdx < 0 || acctIdx + 1 >= parts.Length)
            return "azblob://"; // broader than an account (RG / subscription / management group)

        string account = parts[acctIdx + 1];

        int containersIdx = Array.FindIndex(parts, acctIdx + 1,
            p => string.Equals(p, "containers", StringComparison.OrdinalIgnoreCase));
        if (containersIdx >= 0 && containersIdx + 1 < parts.Length)
            return $"azblob://{account}/{parts[containersIdx + 1]}/";

        return $"azblob://{account}/";
    }
}
