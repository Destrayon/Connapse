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

    /// <summary>
    /// Whether an ARM scope that translated to the bare <c>azblob://</c> wildcard genuinely covers
    /// every storage account Connapse's configured subscription can see — i.e. it is the
    /// subscription itself, a management group above it, or the tenant root.
    /// </summary>
    /// <remarks>
    /// A <b>resource-group</b> scope also lacks a <c>storageAccounts</c> segment and so translates to
    /// the same wildcard, but it is bounded to the accounts in that one resource group. Treating its
    /// wildcard as authoritative on the <i>grant</i> side would disclose accounts in other resource
    /// groups — an over-grant — so the caller drops such grants rather than widening them. (The
    /// <i>deny</i> side keeps the wildcard: over-denying is the fail-closed direction.) A resource
    /// group is recognised by its <c>resourceGroups</c> segment; a subscription/management-group/root
    /// scope has none.
    /// </remarks>
    public static bool IsSubscriptionWideOrBroader(string armScope)
    {
        string[] parts = armScope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !Array.Exists(parts,
            p => string.Equals(p, "resourceGroups", StringComparison.OrdinalIgnoreCase));
    }
}
