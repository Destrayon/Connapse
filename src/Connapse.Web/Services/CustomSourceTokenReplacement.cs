using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Web.Services;

/// <summary>The outcome of replacing a custom source's token; <c>Error</c> is null when it was replaced.</summary>
/// <param name="Field">The field the error belongs to: <c>secret</c>, <c>baseUrl</c> or <c>scope</c>.</param>
/// <param name="Unreachable">The middle man could not be reached, as opposed to refusing.</param>
public sealed record CustomTokenReplacement(string? Error = null, string? Field = null, bool Unreachable = false, bool NotFound = false)
{
    public static readonly CustomTokenReplacement Replaced = new();
}

/// <summary>
/// Replaces the token a custom source holds, when its middle man rotated the old one. The new token is
/// checked as on creation, against the source's own middle man and scope, which stay fixed, before it
/// replaces the stored one. Used by <c>PATCH /api/sources/{id}</c> and the source edit page.
/// </summary>
public sealed class CustomSourceTokenReplacement(
    ISourceStore sources,
    IConnectionStore connections,
    CustomSourceSetup setup,
    IAuditLogger audit)
{
    /// <summary>Whether the source holds a custom source token: it is a custom source, connected or not.</summary>
    public async Task<bool> AppliesToAsync(Source source, CancellationToken ct) =>
        source.Provider is ConnectionProvider.Custom
        || (source.ConnectionId is Guid connectionId
            && await connections.GetAsync(connectionId, ct) is { Provider: ConnectionProvider.Custom });

    public async Task<CustomTokenReplacement> ReplaceAsync(Guid sourceId, string? token, CancellationToken ct)
    {
        Source? source = await sources.GetAsync(sourceId, ct);
        if (source is null) return new CustomTokenReplacement($"Source {sourceId} not found", NotFound: true);
        if (!await AppliesToAsync(source, ct))
            return new CustomTokenReplacement("Only a custom source takes a secret.", "secret");
        if (string.IsNullOrWhiteSpace(token))
            return new CustomTokenReplacement("Enter the token the middle man expects.", "secret");
        if (CustomSourceScope.TryParse(source.ScopeJson) is not { } scope)
            return new CustomTokenReplacement("This custom source's scope names no middle man.", "scope");

        string trimmed = token.Trim();
        CustomSourceProbe probe = await setup.ProbeAsync(scope.BaseUrl, trimmed, ct);
        // Fixed wording, never the middle man's own: what it answered could quote the token just entered.
        if (probe.Error is not null)
            return probe.Field == "secret"
                ? new CustomTokenReplacement("The middle man refused this token.", "secret")
                : new CustomTokenReplacement(
                    "Could not check the token: the middle man did not answer as the protocol requires. Try again; the old token is still in use.",
                    "secret", Unreachable: true);
        if (!probe.Scopes.Any(s => s.Id == scope.ScopeId))
            return new CustomTokenReplacement($"With this token the middle man offers no scope '{scope.ScopeId}'.", "secret");

        if (!await sources.ReplaceSecretAsync(sourceId, trimmed, ct))
            return new CustomTokenReplacement($"Source {sourceId} not found", NotFound: true);
        await audit.LogAsync("source.secret_replaced", "source", sourceId.ToString(), new { source.Name }, ct);
        return CustomTokenReplacement.Replaced;
    }
}
