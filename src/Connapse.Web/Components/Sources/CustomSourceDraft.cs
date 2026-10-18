using Connapse.Core;
using Connapse.Storage.Connectors.Custom;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// What the New source dialog holds for custom sources while they are being chosen: the scopes of a custom
/// connection's middle man to tick, one source per tick, as Confluence spaces and Jira projects are.
/// </summary>
public sealed class CustomSourceDraft
{
    private const int MaxSourceName = 128;

    /// <summary>The custom connection the sources sit on: it holds the middle man's address and token.</summary>
    public required Connection Connection { get; init; }

    /// <summary>The middle man the connection reads, for display; null when it names none.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The custom provider that checks these sources' results; null for a content-only connection.</summary>
    public string? PermissionsBy { get; init; }

    /// <summary>The scopes this connection already has a source for, which cannot be ticked again.</summary>
    public IReadOnlySet<string> ExistingScopeIds { get; init; } = new HashSet<string>();

    /// <summary>The scopes the middle man offered when the connection was last checked.</summary>
    public IReadOnlyList<CustomScopeItem> Scopes { get; set; } = [];

    /// <summary>The ticked scopes, each of which becomes a source.</summary>
    public HashSet<string> Selected { get; } = [];

    public int? SyncIntervalSeconds { get; set; }

    /// <summary>The interval as typed, so a value that is not a number stays on screen with its error.</summary>
    public string? IntervalText { get; set; }

    /// <summary>Why the connection's middle man could not be read.</summary>
    public string? Error { get; set; }

    /// <summary>Why the ticks cannot be added: none ticked, or a scope the middle man no longer offers.</summary>
    public string? ScopesError { get; set; }

    public string? IntervalError { get; set; }

    public bool Loading { get; set; }

    /// <summary>The scopes that can still be added on this connection.</summary>
    public IEnumerable<CustomScopeItem> Addable => Scopes.Where(s => !ExistingScopeIds.Contains(s.Id));

    /// <summary>Takes freshly loaded scopes, keeping ticks that still apply; a lone addable scope is ticked for you.</summary>
    public void ScopesLoaded(IReadOnlyList<CustomScopeItem> scopes)
    {
        Scopes = scopes;
        Selected.RemoveWhere(id => !Addable.Any(s => s.Id == id));
        if (Selected.Count == 0 && Addable.Count() == 1)
            Selected.Add(Addable.Single().Id);
        ScopesError = null;
    }

    /// <summary>A source's name: the connection's, then the scope's, within the store's limit.</summary>
    public string SourceNameFor(CustomScopeItem scope)
    {
        string name = $"{Connection.Name} / {scope.Name}";
        return name.Length <= MaxSourceName ? name : name[..(MaxSourceName - 1)] + "…";
    }

    /// <summary>Reads the typed interval: empty for the default, or a whole number of at least 60 seconds.</summary>
    public void ValidateInterval()
    {
        string trimmed = IntervalText?.Trim() ?? "";
        (SyncIntervalSeconds, IntervalError) = trimmed.Length == 0
            ? (null, null)
            : int.TryParse(trimmed, out int seconds) && seconds >= 60
                ? (seconds, null)
                : ((int?)null, "Enter a whole number of seconds, at least 60, or leave it empty.");
    }
}
