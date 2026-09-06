using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Resolves a searcher's effective RBAC-readable azblob scopes from ARM: one roleAssignments call
/// at the subscription scope (transitive over groups, WITHOUT atScope so account/container
/// assignments are included) minus a parallel denyAssignments call. Fails closed; caches only
/// confident answers. Mirrors <see cref="GraphDirectoryReader"/>'s transport/caching discipline.
/// </summary>
public sealed class ArmRbacReader(
    HttpClient httpClient,
    TokenCredential azureCredential,
    IMemoryCache cache,
    IOptionsMonitor<AzureProviderSettings> options) : IAzureRbacReader
{
    private const string ArmBase = "https://management.azure.com";
    private const string ApiVersion = "2022-04-01";
    private static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    private static readonly HashSet<string> BlobDataReadRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1", // Storage Blob Data Reader
        "ba92f5b4-2d11-453d-a403-e96b0029c9fe", // Storage Blob Data Contributor
        "b7e6dc6d-f1e8-4753-8033-0f276bb0955b", // Storage Blob Data Owner
    };

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public async Task<AzureRbacScopes> ResolveAsync(string primaryOid, CancellationToken ct = default)
    {
        string? sub = options.CurrentValue.SubscriptionId;
        if (string.IsNullOrWhiteSpace(sub))
            return AzureRbacScopes.Failed(); // cannot query without a subscription — fail closed

        try
        {
            return await ResolveUncachedAsync(sub, primaryOid, ct); // Task 5/6 add deny + cache
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AzureRbacScopes.Failed();
        }
    }

    private async Task<AzureRbacScopes> ResolveUncachedAsync(string sub, string oid, CancellationToken ct)
    {
        AccessToken token = await azureCredential.GetTokenAsync(new TokenRequestContext(ArmScopes), ct);

        Task<IReadOnlyList<Assignment>> grantsTask = ListAllAsync(
            $"{ArmBase}/subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments" +
            $"?api-version={ApiVersion}&$filter=assignedTo('{oid}')", token, ct);
        Task<IReadOnlyList<Assignment>> denyTask = ListAllAsync(
            $"{ArmBase}/subscriptions/{sub}/providers/Microsoft.Authorization/denyAssignments" +
            $"?api-version={ApiVersion}&$filter=assignedTo('{oid}')", token, ct);
        await Task.WhenAll(grantsTask, denyTask); // a failure in either throws → caller fails closed

        var prefixes = new List<AzureScope>();
        var tags = new List<AzureTagCondition>();
        foreach (Assignment a in grantsTask.Result)
        {
            string? roleGuid = LastSegment(a.Properties?.RoleDefinitionId);
            if (roleGuid is null || !BlobDataReadRoles.Contains(roleGuid) || a.Properties?.Scope is null)
                continue;
            ApplyGrant(a.Properties.Scope, a.Properties.Condition, prefixes, tags);
        }

        IReadOnlyList<string> denyPrefixes = DenyPrefixes(denyTask.Result);
        if (denyPrefixes.Count > 0)
        {
            prefixes = prefixes.Where(p => !CoveredByAnyDeny(p.Prefix, denyPrefixes)).ToList();
            tags = tags.Where(t => !CoveredByAnyDeny(t.Scope, denyPrefixes)).ToList();
        }

        return AzureRbacScopes.Resolved(prefixes, tags);
    }

    /// <summary>Deny scopes (as azblob prefixes) that apply to blob read for this searcher.</summary>
    private static IReadOnlyList<string> DenyPrefixes(IReadOnlyList<Assignment> denies)
    {
        var result = new List<string>();
        foreach (Assignment d in denies)
        {
            if (d.Properties?.Scope is null) continue;
            if (AppliesToBlobRead(d.Properties.Permissions))
                result.Add(AzureRbacScopeTranslator.ToAzblobPrefix(d.Properties.Scope));
        }
        return result;
    }

    private static bool AppliesToBlobRead(IReadOnlyList<Permission>? permissions)
    {
        if (permissions is null) return false;
        foreach (Permission p in permissions)
        {
            foreach (string a in p.DataActions ?? [])
            {
                if (a == "*"
                    || a.Equals("Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", StringComparison.OrdinalIgnoreCase)
                    || (a.EndsWith('*') && "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read"
                            .StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    // A grant is denied when a deny prefix is equal to, or an ancestor of, the grant prefix
    // (deny-wins over a broader-or-equal scope). "azblob://" as a deny prefix covers everything.
    private static bool CoveredByAnyDeny(string grantPrefix, IReadOnlyList<string> denyPrefixes) =>
        denyPrefixes.Any(d => grantPrefix.StartsWith(d, StringComparison.Ordinal));

    /// <summary>Maps one matched grant (scope + ABAC condition) into a prefix, a tag residue, or a drop.</summary>
    internal static void ApplyGrant(string armScope, string? condition, List<AzureScope> prefixes, List<AzureTagCondition> tags)
    {
        string basePrefix = AzureRbacScopeTranslator.ToAzblobPrefix(armScope);
        AbacResult abac = AzureAbacConditionParser.Parse(condition);
        switch (abac.Kind)
        {
            case AbacKind.None:
                prefixes.Add(new AzureScope(basePrefix));
                break;
            case AbacKind.PathPrefix:
                prefixes.Add(new AzureScope(basePrefix + abac.PathPrefix));
                break;
            case AbacKind.ContainerName:
                // Narrow an account/broader scope to the named container.
                prefixes.Add(new AzureScope(NarrowToContainer(basePrefix, abac.ContainerName!)));
                break;
            case AbacKind.Tag:
                tags.Add(new AzureTagCondition(basePrefix, abac.TagKey!, abac.TagValue!, abac.TagKeyCaseSensitive));
                break;
            case AbacKind.Unparseable:
            default:
                break; // drop this grant only (fail closed)
        }
    }

    private static string NarrowToContainer(string basePrefix, string container)
    {
        // basePrefix is "azblob://", "azblob://{acct}/", or "azblob://{acct}/{c}/". A container-name
        // condition names the container within the account; only meaningful when the account is known.
        if (basePrefix == "azblob://")
            return basePrefix; // account unknown — leave broad; the condition can't be tightened here
        // basePrefix ends with "/"; strip any existing container and append the named one.
        string acct = basePrefix["azblob://".Length..].TrimEnd('/').Split('/')[0];
        return $"azblob://{acct}/{container}/";
    }

    private static string? LastSegment(string? id) =>
        string.IsNullOrEmpty(id) ? null : id.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private async Task<IReadOnlyList<Assignment>> ListAllAsync(string url, AccessToken token, CancellationToken ct)
    {
        var all = new List<Assignment>();
        string? next = url;
        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new("Bearer", token.Token);
            using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode(); // non-2xx → throw → caller fails closed
            ArmList? page = await response.Content.ReadFromJsonAsync<ArmList>(ct);
            if (page?.Value is { } v) all.AddRange(v);
            next = page?.NextLink;
        }
        return all;
    }

    // ---- ARM DTOs ----
    internal sealed record ArmList(
        [property: JsonPropertyName("value")] IReadOnlyList<Assignment>? Value,
        [property: JsonPropertyName("nextLink")] string? NextLink);
    internal sealed record Assignment([property: JsonPropertyName("properties")] AssignmentProps? Properties);
    internal sealed record AssignmentProps(
        [property: JsonPropertyName("roleDefinitionId")] string? RoleDefinitionId,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("condition")] string? Condition,
        [property: JsonPropertyName("permissions")] IReadOnlyList<Permission>? Permissions);
    internal sealed record Permission(
        [property: JsonPropertyName("dataActions")] IReadOnlyList<string>? DataActions,
        [property: JsonPropertyName("notDataActions")] IReadOnlyList<string>? NotDataActions);
}
