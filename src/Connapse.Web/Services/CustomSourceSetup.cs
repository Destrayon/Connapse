using Connapse.Core;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.Custom;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>What checking a middle man found: its normalised URL and scopes, or why it cannot be used.</summary>
/// <param name="Field">The form field the error belongs to: <c>baseUrl</c> or <c>secret</c>.</param>
/// <param name="Unreachable">The address and token were acceptable but the middle man failed or answered outside the protocol.</param>
public sealed record CustomSourceProbe(
    string? BaseUrl,
    IReadOnlyList<CustomScopeItem> Scopes,
    string? Error = null,
    string? Field = null,
    bool Unreachable = false);

/// <summary>
/// Checks a content-only custom source's middle man before the source is saved: the URL rules, the token,
/// protocol version 1, and the scopes it offers. Shared by the New source dialog and <c>POST /api/sources</c>
/// so both refuse the same things.
/// </summary>
public sealed class CustomSourceSetup(IHttpClientFactory httpClients, IOptionsMonitor<CustomSourceSettings> settings)
{
    public async Task<CustomSourceProbe> ProbeAsync(string? baseUrl, string? token, CancellationToken ct)
    {
        if (!CustomSourceEndpoint.TryNormalize(baseUrl, settings.CurrentValue.AllowedHttpHosts, out string normalized, out string? error))
            return new CustomSourceProbe(null, [], error, "baseUrl");

        if (string.IsNullOrWhiteSpace(token))
            return new CustomSourceProbe(normalized, [], "Enter the token the middle man expects.", "secret");

        CustomSourceClient client = new(httpClients.CreateClient(CustomSourceClient.HttpClientName), normalized, token.Trim());
        try
        {
            CustomDiscovery discovery = await client.GetDiscoveryAsync(ct);
            if (discovery.ProtocolVersions?.Contains("1") != true)
                return new CustomSourceProbe(normalized, [],
                    "The middle man does not speak custom source protocol version 1.", "baseUrl", Unreachable: true);

            IReadOnlyList<CustomScopeItem> scopes = await client.ListScopesAsync(ct);
            return scopes.Count == 0
                ? new CustomSourceProbe(normalized, [], "The middle man offers no scopes.", "baseUrl")
                : new CustomSourceProbe(normalized, scopes);
        }
        catch (CustomSourceUnauthorizedException ex)
        {
            return new CustomSourceProbe(normalized, [], ex.Message, "secret");
        }
        catch (CustomSourceException ex)
        {
            return new CustomSourceProbe(normalized, [], ex.Message, "baseUrl", Unreachable: true);
        }
    }
}
