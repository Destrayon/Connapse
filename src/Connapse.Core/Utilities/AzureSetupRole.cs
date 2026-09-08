namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the <c>az role assignment</c> commands that grant Connapse's blob role on exactly the
/// containers a connection allows — the Azure counterpart of <see cref="S3SetupPolicy.ForBuckets"/>.
/// </summary>
/// <remarks>
/// Text an operator runs in Cloud Shell; Connapse never assigns a role itself. Aimed at operators
/// whose access identity was granted subscription-wide by the easy setup and who want this
/// connection's grant narrowed to what it reads. A prefix inside a container cannot be expressed
/// as an RBAC scope, so the narrowest grant is the container; the connection's allowed-locations
/// list still bounds reads below that.
/// </remarks>
public static class AzureSetupRole
{
    /// <summary>One command per distinct container, or one on the whole account when nothing is allowed yet.</summary>
    /// <param name="accessClientId">The app (client) id of Connapse's access identity — the assignee.</param>
    /// <param name="accountResourceId">The storage account's ARM resource id.</param>
    /// <param name="locations">Allowed locations, each <c>container</c> or <c>container/prefix</c>.</param>
    public static string AssignmentsFor(string accessClientId, string accountResourceId, IEnumerable<string> locations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountResourceId);

        var containers = locations
            .Select(l => (l ?? string.Empty).Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.IndexOf('/') is var slash && slash >= 0 ? l[..slash] : l)
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string account = accountResourceId.Trim().TrimEnd('/');
        IEnumerable<string> scopes = containers.Count == 0
            ? [account]
            : containers.Select(c => $"{account}/blobServices/default/containers/{c}");

        return string.Join("\n", scopes.Select(scope =>
            $"az role assignment create --assignee '{Shell(accessClientId.Trim())}' "
            + $"--role '{AzureCloudShellSetup.BlobDataRoleName}' --scope '{Shell(scope)}'"));
    }

    private static string Shell(string value) => value.Replace("'", "'\\''");
}
