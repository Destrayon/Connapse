using Connapse.Core;
using Connapse.Storage.Connectors.Custom;

namespace Connapse.Web.Components.Sources;

/// <summary>What the New source dialog holds for a custom source while it is being filled in.</summary>
public sealed class CustomSourceDraft
{
    /// <summary>The custom connection the source sits on: it holds the middle man's address and token.</summary>
    public required Connection Connection { get; init; }

    /// <summary>The middle man the connection reads, for display; null when it names none.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The custom provider that checks this source's results; null for a content-only connection.</summary>
    public string? PermissionsBy { get; init; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? ScopeId { get; set; }
    public int? SyncIntervalSeconds { get; set; }

    /// <summary>The interval as typed, so a value that is not a number stays on screen with its error.</summary>
    public string? IntervalText { get; set; }

    public string? NameError { get; set; }
    public string? IntervalError { get; set; }

    /// <summary>The scopes the middle man offered when the connection was last checked.</summary>
    public IReadOnlyList<CustomScopeItem> Scopes { get; set; } = [];

    /// <summary>Why the connection's middle man could not be read.</summary>
    public string? Error { get; set; }

    public bool Loading { get; set; }
}
