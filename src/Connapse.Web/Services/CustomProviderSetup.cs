using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>What checking a custom provider's middle man found, step by step.</summary>
/// <param name="EndpointOk">The address is acceptable, the middle man speaks protocol 1, accepts the token and refuses a wrong one.</param>
/// <param name="PermissionsOk">It advertises <c>permissions.check</c> and answered a real check call.</param>
/// <param name="Field">The form field an error belongs to: <c>baseUrl</c> or <c>secret</c>.</param>
public sealed record CustomProviderCheck(
    string? BaseUrl,
    bool EndpointOk,
    bool PermissionsOk,
    int MaxCheckBatch = 0,
    bool SupportsAccessKeys = false,
    string? LinkUrl = null,
    string? Error = null,
    string? Field = null);

/// <summary>
/// Checks a custom provider before it is saved: the address rules, the token, that a wrong token is refused
/// (a middle man that answers anyone would hand out permission decisions to anyone who can reach it), and
/// that it answers a real permission check.
/// </summary>
public sealed class CustomProviderSetup(IHttpClientFactory httpClients, IOptionsMonitor<CustomSourceSettings> settings)
{
    private const string CheckSubject = "connapse-setup-check";

    public async Task<CustomProviderCheck> CheckAsync(string? baseUrl, string? token, CancellationToken ct)
    {
        if (!CustomSourceEndpoint.TryNormalize(baseUrl, settings.CurrentValue.AllowedHttpHosts, out string normalized, out string? error))
            return new CustomProviderCheck(null, false, false, Error: error, Field: "baseUrl");

        if (string.IsNullOrWhiteSpace(token))
            return new CustomProviderCheck(normalized, false, false, Error: "Enter the token the middle man expects.", Field: "secret");

        CustomSourceClient client = Client(normalized, token.Trim());
        CustomDiscovery discovery;
        try
        {
            discovery = await client.GetDiscoveryAsync(ct);
        }
        catch (CustomSourceUnauthorizedException ex)
        {
            return new CustomProviderCheck(normalized, false, false, Error: CustomSourceSetup.Scrub(ex.Message, token), Field: "secret");
        }
        catch (CustomSourceException ex)
        {
            return new CustomProviderCheck(normalized, false, false, Error: CustomSourceSetup.Scrub(ex.Message, token), Field: "baseUrl");
        }

        if (discovery.ProtocolVersions?.Contains("1") != true)
            return new CustomProviderCheck(normalized, false, false,
                Error: "The middle man does not speak custom source protocol version 1.", Field: "baseUrl");

        if (!await RefusesAWrongTokenAsync(normalized, ct))
            return new CustomProviderCheck(normalized, false, false,
                Error: "The middle man answered a request carrying a wrong token. It must refuse every request without its token, "
                    + "or anyone who can reach it could ask it about anyone's permissions.",
                Field: "baseUrl");

        CustomPermissionCapabilities? permissions = discovery.Capabilities?.Permissions;
        if (permissions?.Check is not { MaxBatch: > 0 } check)
            return new CustomProviderCheck(normalized, true, false,
                Error: "The middle man does not answer permission checks: its discovery document lists no permissions.check.");

        try
        {
            IReadOnlyList<CustomScopeItem> scopes = await client.ListScopesAsync(ct);
            string scope = scopes.FirstOrDefault()?.Id ?? "connapse-setup-check";
            await client.EvaluateAsync(CheckSubject, scope, [CheckSubject], check.MaxBatch, ct);
        }
        catch (CustomSourceException ex)
        {
            return new CustomProviderCheck(normalized, true, false, Error: $"A test permission check failed: {CustomSourceSetup.Scrub(ex.Message, token)}");
        }

        return new CustomProviderCheck(
            normalized, true, true, check.MaxBatch, permissions.AccessKeys is not null, permissions.Linking?.LinkUrl);
    }

    private async Task<bool> RefusesAWrongTokenAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            await Client(baseUrl, $"connapse-wrong-token-{Guid.NewGuid():N}").GetDiscoveryAsync(ct);
            return false;
        }
        catch (CustomSourceUnauthorizedException)
        {
            return true;
        }
        catch (CustomSourceException)
        {
            // Any other refusal still refuses; only an answer counts against it.
            return true;
        }
    }

    private CustomSourceClient Client(string baseUrl, string token) =>
        new(httpClients.CreateClient(CustomSourceClient.HttpClientName), baseUrl, token);
}
