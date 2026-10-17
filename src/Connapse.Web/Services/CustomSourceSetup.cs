using Connapse.Core;
using Connapse.Core.Interfaces;
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
/// The middle man a custom connection's sources read: its address, and who checks permissions (null for a
/// connection without a provider). <paramref name="Error"/> says why it cannot be read.
/// </summary>
public sealed record CustomMiddleMan(string? BaseUrl, string? PermissionsBy, string? Error = null);

/// <summary>
/// Checks a custom middle man before something is saved: the URL rules, the token, protocol version 1, and the
/// scopes it offers. Shared by the Connections page, the New source dialog and <c>POST /api/sources</c> so all
/// of them refuse the same things.
/// </summary>
public sealed class CustomSourceSetup(
    IHttpClientFactory httpClients,
    IOptionsMonitor<CustomSourceSettings> settings,
    ICustomProviderStore providers,
    IConnectionStore connections)
{
    /// <summary>The middle man a custom connection's sources read, without asking it anything.</summary>
    public async Task<CustomMiddleMan> DescribeAsync(Connection connection, CancellationToken ct)
    {
        if (CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId)
        {
            return await providers.GetAsync(providerId, ct) is { } provider
                ? new CustomMiddleMan(provider.BaseUrl, provider.Name)
                : new CustomMiddleMan(null, null, "This connection's custom provider no longer exists. Choose another connection.");
        }

        return CustomConnectionConfig.BaseUrlOf(connection.ConfigJson) is { } baseUrl
            ? new CustomMiddleMan(baseUrl, null)
            : new CustomMiddleMan(null, null, "This connection names no middle man. Edit it on the Connections page.");
    }

    /// <summary>
    /// Checks the middle man a custom connection's sources read, with the token they read it with: the
    /// provider's, or the connection's own.
    /// </summary>
    public async Task<CustomSourceProbe> ProbeConnectionAsync(Connection connection, CancellationToken ct)
    {
        CustomMiddleMan middleMan = await DescribeAsync(connection, ct);
        if (middleMan.Error is not null)
            return new CustomSourceProbe(null, [], middleMan.Error, "connectionId");

        string? token = CustomConnectionConfig.ProviderIdOf(connection.ConfigJson) is { } providerId
            ? await providers.GetTokenAsync(providerId, ct)
            : connection.HasSecret ? await connections.GetSecretAsync(connection.Id, ct) : null;
        if (string.IsNullOrWhiteSpace(token))
            return new CustomSourceProbe(middleMan.BaseUrl, [], "This connection holds no token. Enter one on the Connections page.", "connectionId");

        CustomSourceProbe probe = await ProbeAsync(middleMan.BaseUrl, token, ct);
        // The token is the connection's, so a refusal is fixed there rather than on the source.
        return probe.Field == "secret"
            ? probe with { Error = $"{probe.Error} Replace the token on the connection or its custom provider.", Field = "connectionId" }
            : probe;
    }

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
            // Never the middle man's own words: they could quote the token just entered, in any encoding.
            return new CustomSourceProbe(normalized, [], ex.Message, "baseUrl", Unreachable: true);
        }
    }
}
