using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// Checks a custom provider before it is saved: the address rules, the token, that a wrong token is refused
/// (a middle man that answers anyone would hand out permission decisions to anyone who can reach it), and
/// that it answers a real permission check.
/// </summary>
public sealed class CustomProviderSetup(IHttpClientFactory httpClients, IOptionsMonitor<CustomSourceSettings> settings)
    : Connapse.Application.Ports.ICustomProviderChecker
{
    private const string CheckSubject = "connapse-setup-check";

    public async Task<CustomProviderCheck> CheckAsync(string? baseUrl, string? token, CancellationToken ct)
    {
        if (!CustomSourceEndpoint.TryNormalize(baseUrl, settings.CurrentValue.AllowedHttpHosts, out string normalized, out string? error))
            return new CustomProviderCheck(null, false, false, Error: error, Field: "baseUrl");

        if (string.IsNullOrWhiteSpace(token))
            return new CustomProviderCheck(normalized, false, false, Error: "Enter the token the source server expects.", Field: "secret");

        CustomSourceClient client = Client(normalized, token.Trim());
        CustomDiscovery discovery;
        try
        {
            discovery = await client.GetDiscoveryAsync(ct);
        }
        catch (CustomSourceUnauthorizedException ex)
        {
            return new CustomProviderCheck(normalized, false, false, Error: ex.Message, Field: "secret");
        }
        catch (CustomSourceException ex)
        {
            return new CustomProviderCheck(normalized, false, false, Error: ex.Message, Field: "baseUrl");
        }

        if (discovery.ProtocolVersions?.Contains("1") != true)
            return new CustomProviderCheck(normalized, false, false,
                Error: "The source server does not speak custom source protocol version 1.", Field: "baseUrl");

        if (!await RefusesAWrongTokenAsync(normalized, ct))
            return new CustomProviderCheck(normalized, false, false,
                Error: "The source server answered a request carrying a wrong token. It must refuse every request without its token, "
                    + "or anyone who can reach it could ask it about anyone's permissions.",
                Field: "baseUrl");

        CustomPermissionCapabilities? permissions = discovery.Capabilities?.Permissions;
        if (permissions?.Check is not { MaxBatch: > 0 } check)
            return new CustomProviderCheck(normalized, true, false,
                Error: "The source server does not answer permission checks: its discovery document lists no permissions.check. "
                    + "Declare it and serve POST /access/v1/evaluations, or add the source server as a content-only source instead.",
                Name: discovery.Name);

        try
        {
            IReadOnlyList<CustomScopeItem> scopes = await client.ListScopesAsync(ct);
            string scope = scopes.FirstOrDefault()?.Id ?? "connapse-setup-check";
            await client.EvaluateAsync(CheckSubject, scope, [CheckSubject], check.MaxBatch, ct);
        }
        catch (CustomSourceException ex)
        {
            return new CustomProviderCheck(normalized, true, false,
                Error: $"A test permission check failed: {ex.Message} Check the source server's logs for the call to POST /access/v1/evaluations.",
                Name: discovery.Name);
        }

        // Anyone who links must be able to unlink, from Profile, Integrations: a source server that offers linking
        // must answer the link status and removal calls, which are tried here for a user who was never linked.
        if (permissions.Linking is not null)
        {
            string neverLinked = Guid.NewGuid().ToString();
            try
            {
                await client.GetLinkAsync(neverLinked, ct);
                await client.UnlinkAsync(neverLinked, ct, strict: true);
            }
            catch (CustomSourceException ex)
            {
                return new CustomProviderCheck(normalized, true, false,
                    Error: $"The source server offers account linking but does not let people unlink: {ex.Message} "
                        + "Serve GET and DELETE /v1/links/{connapse_user_id}, or stop declaring permissions.linking.",
                    Name: discovery.Name);
            }
        }

        return new CustomProviderCheck(
            normalized, true, true, check.MaxBatch, permissions.AccessKeys is not null, permissions.Linking?.LinkUrl, Name: discovery.Name);
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
