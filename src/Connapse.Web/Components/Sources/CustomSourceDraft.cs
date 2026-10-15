using Connapse.Storage.Connectors.Custom;

namespace Connapse.Web.Components.Sources;

/// <summary>What the New source dialog holds for a content-only custom source while it is being filled in.</summary>
public sealed class CustomSourceDraft
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Token { get; set; } = "";
    public string? ScopeId { get; set; }
    public int? SyncIntervalSeconds { get; set; }

    /// <summary>The scopes the middle man offered when the address and token were last checked.</summary>
    public IReadOnlyList<CustomScopeItem> Scopes { get; set; } = [];

    /// <summary>Why the last check failed, and the field it belongs to: <c>baseUrl</c> or <c>secret</c>.</summary>
    public string? Error { get; set; }
    public string? ErrorField { get; set; }

    public bool Loading { get; set; }

    /// <summary>
    /// Set when the source is on a custom connection: the provider's middle man, which the source must read
    /// so the documents it lists are the ones the provider answers for. Empty when the provider is missing.
    /// </summary>
    public string? FixedBaseUrl { get; set; }

    /// <summary>The custom provider that checks this source's results, when it is on a custom connection.</summary>
    public string? PermissionsBy { get; set; }
}
