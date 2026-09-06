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
        AzureProviderSettings settings = options.CurrentValue;
        string? sub = settings.SubscriptionId;
        if (string.IsNullOrWhiteSpace(sub))
            return AzureRbacScopes.Failed();

        // Key by tenant + subscription + oid: the subscription and credential come from reloadable
        // settings, so a cached result must never be reused across a subscription or tenant change
        // (which would expose accounts not authorized in the new context).
        string cacheKey = $"azure-rbac:{settings.TenantId}:{sub}:{primaryOid}";
        if (cache.TryGetValue(cacheKey, out AzureRbacScopes? cached) && cached is not null)
            return cached;

        AzureRbacScopes result;
        try
        {
            result = await ResolveUncachedAsync(sub, primaryOid, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return AzureRbacScopes.Failed();
        }

        if (result.Outcome is RbacOutcome.Resolved)
            cache.Set(cacheKey, result, CacheLifetime);
        return result;
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

    private const string BlobReadAction = "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read";

    private static bool AppliesToBlobRead(IReadOnlyList<Permission>? permissions)
    {
        if (permissions is null) return false;
        foreach (Permission p in permissions)
        {
            // A deny applies to blob read only when a dataAction covers the read action AND no
            // notDataAction excludes it (dataActions minus notDataActions). Ignoring notDataActions
            // would over-subtract and hide legitimately-readable content.
            bool granted = (p.DataActions ?? []).Any(MatchesBlobRead);
            bool excluded = (p.NotDataActions ?? []).Any(MatchesBlobRead);
            if (granted && !excluded)
                return true;
        }
        return false;
    }

    private static bool MatchesBlobRead(string action) =>
        action == "*"
        || action.Equals(BlobReadAction, StringComparison.OrdinalIgnoreCase)
        || (action.EndsWith('*') && BlobReadAction.StartsWith(action[..^1], StringComparison.OrdinalIgnoreCase));

    // A grant is removed if it OVERLAPS any applicable deny in EITHER direction:
    //  - deny at a broader-or-equal scope (deny is a prefix of the grant) covers the grant; and
    //  - deny at a narrower scope (a "hole" beneath the grant — the grant is a prefix of the deny)
    //    cannot be represented as "grant minus a hole" in a prefix set, so the whole grant is
    //    conservatively dropped (over-subtraction = under-grant, the fail-closed direction).
    // Deny wins either way. "azblob://" on either side matches everything. All emitted prefixes end
    // in "/" (or are a path-condition prefix), so container-name boundaries are respected.
    private static bool CoveredByAnyDeny(string grantPrefix, IReadOnlyList<string> denyPrefixes) =>
        denyPrefixes.Any(d =>
            grantPrefix.StartsWith(d, StringComparison.Ordinal) ||
            d.StartsWith(grantPrefix, StringComparison.Ordinal));

    /// <summary>Maps one matched grant (scope + ABAC condition) into a prefix, a tag residue, or a
    /// drop. Every ABAC restriction is intersected with the assignment's own scope; when it cannot
    /// be expressed as an azblob prefix the grant is dropped (fail closed), never broadened.</summary>
    internal static void ApplyGrant(string armScope, string? condition, List<AzureScope> prefixes, List<AzureTagCondition> tags)
    {
        string basePrefix = AzureRbacScopeTranslator.ToAzblobPrefix(armScope);
        (string? account, string? container) = SplitPrefix(basePrefix);
        AbacResult abac = AzureAbacConditionParser.Parse(condition);
        switch (abac.Kind)
        {
            case AbacKind.None:
                prefixes.Add(new AzureScope(basePrefix));
                break;

            case AbacKind.PathPrefix:
                // A blob-path condition is a prefix WITHIN a container, so it can only be expressed
                // as an azblob prefix when the scope already fixes the container. On an account or
                // broader scope the same path applies inside EVERY container (not a container named
                // for the path), which cannot be represented — drop (fail closed).
                if (container is not null)
                    prefixes.Add(new AzureScope(basePrefix + abac.PathPrefix));
                break;

            case AbacKind.ContainerName:
                string? named = abac.ContainerName;
                if (account is null || named is null)
                    break; // account unknown → can't apply → drop
                if (container is null)
                    prefixes.Add(new AzureScope($"azblob://{account}/{named}/")); // narrow account to the named container
                else if (string.Equals(container, named, StringComparison.Ordinal))
                    prefixes.Add(new AzureScope(basePrefix)); // condition names the same container as the scope
                // else: names a DIFFERENT container than the assignment's scope → grants nothing → drop
                break;

            case AbacKind.Tag:
                tags.Add(new AzureTagCondition(basePrefix, abac.TagKey!, abac.TagValue!, abac.TagKeyCaseSensitive, abac.ValueCaseSensitive));
                break;

            case AbacKind.Unparseable:
            default:
                break; // drop this grant only (fail closed)
        }
    }

    /// <summary>Splits an azblob prefix into (account?, container?): "azblob://" → (null,null);
    /// "azblob://acct/" → (acct,null); "azblob://acct/c/" → (acct,c).</summary>
    private static (string? Account, string? Container) SplitPrefix(string basePrefix)
    {
        if (basePrefix == "azblob://")
            return (null, null);
        string[] segs = basePrefix["azblob://".Length..].TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length switch
        {
            0 => (null, null),
            1 => (segs[0], null),
            _ => (segs[0], segs[1]),
        };
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
            // A 200 whose body is missing or has a null "value" is a malformed/anomalous page. It
            // must fail closed rather than be read as "no entries" — silently swallowing a deny page
            // this way would omit denies and turn into an over-grant.
            if (page?.Value is null)
                throw new InvalidOperationException("ARM list response had no 'value' array.");
            all.AddRange(page.Value);
            next = page.NextLink;
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
