using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Resolves a searcher's Entra identity set in one Microsoft Graph <c>$batch</c>: the deprovisioning
/// gate (<c>GET /users/{oid}?$select=id,accountEnabled</c>) and transitive security groups
/// (<c>POST /users/{oid}/getMemberGroups {securityEnabledOnly:true}</c>). Authenticates with
/// Connapse's own <see cref="TokenCredential"/>. Fails closed; caches only confident answers.
/// </summary>
public sealed class GraphDirectoryReader(
    HttpClient httpClient,
    TokenCredential azureCredential,
    IMemoryCache cache) : IAzureDirectoryReader
{
    private const string GraphBatchUrl = "https://graph.microsoft.com/v1.0/$batch";
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    /// <summary>Cache window for a confident answer; also the revocation-propagation delay.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    public async Task<AzureIdentitySet> ResolveAsync(AzureIdentityRef link, CancellationToken ct = default)
    {
        string oid = link.ObjectId;
        // Keyed by (tenant, oid) so a resolved set can never be reused across a tenant boundary,
        // even though the single-tenant deployment and globally-unique Entra object ids make a
        // collision practically impossible.
        string cacheKey = "azure-identity:" + link.TenantId + ":" + oid;
        if (cache.TryGetValue(cacheKey, out AzureIdentitySet? cached) && cached is not null)
            return cached;

        AzureIdentitySet result;
        try
        {
            result = await ResolveUncachedAsync(oid, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any transport/parse failure fails closed and is never cached.
            return AzureIdentitySet.Failed();
        }

        // Only confident answers are cached; a failure must be retried on the next search.
        if (result.Outcome is not AzureIdentityOutcome.Failed)
            cache.Set(cacheKey, result, CacheLifetime);
        return result;
    }

    private async Task<AzureIdentitySet> ResolveUncachedAsync(string oid, CancellationToken ct)
    {
        AccessToken token = await azureCredential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphBatchUrl);
        request.Headers.Authorization = new("Bearer", token.Token);
        request.Content = JsonContent.Create(new BatchRequest(
        [
            new("user", "GET", $"/users/{oid}?$select=id,accountEnabled", null, null),
            new("groups", "POST", $"/users/{oid}/getMemberGroups",
                new GetMemberGroupsBody(true),
                new Dictionary<string, string> { ["Content-Type"] = "application/json" }),
        ]));

        using HttpResponseMessage response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return AzureIdentitySet.Failed();   // whole-batch failure (e.g. 401 bad token)

        BatchResponse? batch = await response.Content.ReadFromJsonAsync<BatchResponse>(ct);
        return Interpret(oid, batch);
    }

    /// <summary>
    /// The fail-closed decision, isolated from transport so it is exhaustively unit-testable:
    /// deprovisioning gate first, then the group set. Missing/partial responses fail closed.
    /// </summary>
    internal static AzureIdentitySet Interpret(string oid, BatchResponse? batch)
    {
        SubResponse? user = batch?.Responses?.FirstOrDefault(r => r.Id == "user");
        SubResponse? groups = batch?.Responses?.FirstOrDefault(r => r.Id == "groups");
        if (user is null || groups is null)
            return AzureIdentitySet.Failed();

        // Deprovisioning gate. A 404 or an explicit accountEnabled==false is a confirmed denial
        // (cacheable). A 200 whose body is missing/omits accountEnabled is an anomalous, uncertain
        // answer — not a confirmed deprovision — so it fails closed as Failed (retried, never
        // cached), per the spec's "fail closed on an uncertain/partial response".
        if (user.Status == 404)
            return AzureIdentitySet.Deprovisioned();
        if (user.Status != 200)
            return AzureIdentitySet.Failed();
        bool? accountEnabled = user.Body?.AccountEnabled;
        if (accountEnabled is null)
            return AzureIdentitySet.Failed();
        if (accountEnabled == false)
            return AzureIdentitySet.Deprovisioned();

        // The 200 body must actually be the requested user: a $batch scopes each sub-response by
        // request id and URL, but we still require the returned id to be present and equal to the
        // requested oid before trusting accountEnabled, so a stale/misassociated user body can
        // never satisfy the gate for a different (possibly disabled) searcher.
        if (!string.Equals(user.Body?.Id, oid, StringComparison.OrdinalIgnoreCase))
            return AzureIdentitySet.Failed();

        // Groups must resolve, or the identity set is unknown — fail closed.
        if (groups.Status != 200 || groups.Body?.Value is null)
            return AzureIdentitySet.Failed();

        // Every group value must be a real Entra object GUID. A null, empty, or non-GUID element
        // is an anomalous response, so fail closed rather than admit a bogus principal into P.
        var principals = new List<string>(1 + groups.Body.Value.Count) { oid };
        foreach (string? group in groups.Body.Value)
        {
            if (!Guid.TryParse(group, out _))
                return AzureIdentitySet.Failed();
            principals.Add(group!);
        }
        return AzureIdentitySet.Resolved(principals);
    }

    // ---- Graph $batch DTOs ----
    private sealed record BatchRequest([property: JsonPropertyName("requests")] IReadOnlyList<SubRequest> Requests);
    private sealed record SubRequest(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("body")] object? Body,
        [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers);
    private sealed record GetMemberGroupsBody([property: JsonPropertyName("securityEnabledOnly")] bool SecurityEnabledOnly);

    internal sealed record BatchResponse([property: JsonPropertyName("responses")] IReadOnlyList<SubResponse>? Responses);
    internal sealed record SubResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("body")] SubBody? Body);
    internal sealed record SubBody(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("accountEnabled")] bool? AccountEnabled,
        [property: JsonPropertyName("value")] IReadOnlyList<string>? Value);
}
