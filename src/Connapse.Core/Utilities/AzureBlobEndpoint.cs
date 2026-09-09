using System.Text.RegularExpressions;

namespace Connapse.Core.Utilities;

/// <summary>
/// Ties the address a connection reads from to the account its documents are labelled as.
/// </summary>
/// <remarks>
/// Every Azure permission decision — role assignments, deny assignments, ACLs, tags — is made
/// against the account in a document's <c>azblob://account/...</c> URI, and that account is the
/// name typed on the connection form. The blob endpoint override is a separate string; left
/// unchecked, a connection could read one account through its endpoint while labelling the
/// content as another, and every later check would evaluate the wrong account. It would also
/// hand Connapse's storage token to whatever host the endpoint named. So an override is accepted
/// only when its host is that same account on a known Azure cloud, or a local emulator (Azurite)
/// serving that account under a loopback address.
/// </remarks>
public static partial class AzureBlobEndpoint
{
    /// <summary>Azure's own rule for storage account names: 3–24 lowercase letters and digits.</summary>
    [GeneratedRegex("^[a-z0-9]{3,24}$")]
    private static partial Regex AccountName();

    /// <summary>The blob-service host suffixes of the Azure clouds.</summary>
    private static readonly string[] CloudSuffixes =
    [
        ".blob.core.windows.net",
        ".blob.core.usgovcloudapi.net",
        ".blob.core.chinacloudapi.cn",
        ".blob.core.cloudapi.de",
    ];

    /// <summary>The account name as Azure spells it, or null when it is not a valid one.</summary>
    public static string? NormaliseAccount(string? accountName)
    {
        string? name = accountName?.Trim().ToLowerInvariant();
        return name is not null && AccountName().IsMatch(name) ? name : null;
    }

    /// <summary>The public endpoint of an account on the global Azure cloud.</summary>
    public static Uri DefaultFor(string accountName) =>
        new($"https://{accountName}.blob.core.windows.net");

    /// <summary>
    /// The endpoint a connection reads from: the account's public one when no override is set,
    /// otherwise the override once it is shown to belong to the account. Throws when it does not.
    /// </summary>
    public static Uri Resolve(string accountName, string? endpointOverride)
    {
        string? problem = Validate(accountName, endpointOverride, out Uri? endpoint);
        return problem is null ? endpoint! : throw new InvalidOperationException(problem);
    }

    /// <summary>
    /// Why an account name and endpoint override cannot be used together, or null when they can,
    /// with the endpoint to use in <paramref name="endpoint"/>.
    /// </summary>
    public static string? Validate(string? accountName, string? endpointOverride, out Uri? endpoint)
    {
        endpoint = null;
        string? account = NormaliseAccount(accountName);
        if (account is null)
            return "The storage account name must be 3 to 24 lowercase letters and digits, as Azure names it.";

        if (string.IsNullOrWhiteSpace(endpointOverride))
        {
            endpoint = DefaultFor(account);
            return null;
        }

        if (!Uri.TryCreate(endpointOverride.Trim(), UriKind.Absolute, out Uri? candidate)
            || candidate.Scheme is not ("https" or "http"))
            return "The blob endpoint must be a full https:// address, or be left blank for the account's default.";

        string host = candidate.Host.ToLowerInvariant();

        // The account itself, on any Azure cloud.
        foreach (string suffix in CloudSuffixes)
        {
            if (host == account + suffix)
            {
                endpoint = candidate;
                return null;
            }
        }

        // A local emulator serves accounts as the first path segment: http://127.0.0.1:10000/<account>.
        if (candidate.IsLoopback)
        {
            string firstSegment = candidate.AbsolutePath.Trim('/').Split('/')[0].ToLowerInvariant();
            if (firstSegment == account)
            {
                endpoint = candidate;
                return null;
            }
            return $"An emulator endpoint must serve this account: expected {candidate.Scheme}://{candidate.Authority}/{account}.";
        }

        return $"The blob endpoint '{candidate.Host}' is not storage account '{account}' on an Azure cloud "
            + $"(expected {account}.blob.core.windows.net or the equivalent on another cloud). A connection "
            + "reads from the account it is named for; leave the endpoint blank unless you are pointing at "
            + "a local emulator.";
    }
}
