using System.Text.Json;
using Connapse.Core;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// What every Atlassian source chooses alike, whichever product it reads: whether to index
/// attachments and how large one may be. <see cref="ConfluenceSpaceSelection"/> and
/// <see cref="JiraProjectSelection"/> add the spaces or projects to pick.
/// </summary>
public abstract class AtlassianSourceSelection
{
    public const int DefaultMaxAttachmentMb = 25;
    public const int MinAttachmentMb = 1;

    /// <summary>The smaller of the two connectors' ceilings, so one rule serves both products.</summary>
    public const int MaxAttachmentMbLimit =
        Connapse.Storage.Connectors.Atlassian.ConfluenceSpaceConfig.MaxAttachmentMbCeiling
        < Connapse.Storage.Connectors.Atlassian.JiraProjectConfig.MaxAttachmentMbCeiling
            ? Connapse.Storage.Connectors.Atlassian.ConfluenceSpaceConfig.MaxAttachmentMbCeiling
            : Connapse.Storage.Connectors.Atlassian.JiraProjectConfig.MaxAttachmentMbCeiling;

    private const int MaxSourceName = 128;

    public const string AttachmentsLabel = "Attachments";
    public const string IncludeAttachmentsLabel = "Include attachments";
    public const string MaxAttachmentMbLabel = "Max attachment size (MB)";

    public static string MaxAttachmentMbHelp => $"{MinAttachmentMb} to {MaxAttachmentMbLimit} MB. Larger attachments are skipped.";

    /// <summary>Where the setup guide explains adding a missing scope to a site's credential.</summary>
    public const string MissingScopeGuideUrl = "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#missing-scope";

    /// <summary>The site on the Connections page with Replace credentials open, where a missing permission is added.</summary>
    public static string ReplaceCredentialsUrl(Guid connectionId) => $"/connections?edit={connectionId}&replace=1";

    public static string AttachmentRangeError =>
        $"Max attachment size must be between {MinAttachmentMb} and {MaxAttachmentMbLimit} MB.";

    /// <summary>The optional scope this product's attachments need on the site's credential.</summary>
    public abstract string RequiredAttachmentScope { get; }

    // What the edit page shows for this product, so one attachments section serves both.

    /// <summary>The prefix of the edit page's field ids: <c>confluence</c> or <c>jira</c>.</summary>
    public abstract string FieldIdPrefix { get; }

    /// <summary>Why the space or project is shown but not editable.</summary>
    public abstract string ItemFixedNote { get; }

    /// <summary>What including attachments indexes, in a sentence.</summary>
    public abstract string AttachmentsShortHelp { get; }

    public string AttachmentScopeHelpText => "Attachments need the " + RequiredAttachmentScope + " permission on the site's credential.";

    /// <summary>Whether the site's last passing check confirmed <paramref name="scope"/>.</summary>
    protected static bool ScopeConfirmed(ConnectionHealth? health, string scope) =>
        health?.ConfirmedScopes?.Contains(scope, StringComparer.OrdinalIgnoreCase) ?? false;

    /// <summary>The site's last test did not confirm the attachment scope; the form says so.</summary>
    public bool AttachmentScopeUnconfirmed { get; init; }

    /// <summary>The id every setup event of this selection carries, from opening the dialog to the sources created.</summary>
    public Guid Attempt { get; } = Guid.NewGuid();

    /// <summary>When this selection began, as <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>, for the time to a created source.</summary>
    public long StartedTimestamp { get; } = System.Diagnostics.Stopwatch.GetTimestamp();

    public bool IncludeAttachments { get; set; } = true;

    public int MaxAttachmentMb { get; set; } = DefaultMaxAttachmentMb;

    /// <summary>The problem with the max attachment size, shown next to it; null when there is none.</summary>
    public string? MaxAttachmentMbError { get; set; }

    /// <summary>Checks the max attachment size alone, as when the field loses focus.</summary>
    public bool ValidateMaxAttachmentMb()
    {
        MaxAttachmentMbError = IncludeAttachments && MaxAttachmentMb is < MinAttachmentMb or > MaxAttachmentMbLimit
            ? AttachmentRangeError
            : null;
        return MaxAttachmentMbError is null;
    }

    /// <summary>
    /// The size to store. With attachments off the field is disabled and unchecked, so a value
    /// left out of range there is replaced by the default rather than stored: the connector
    /// refuses a source whose cap is not positive, whether or not attachments are on.
    /// </summary>
    public static int StorableMaxAttachmentMb(int maxAttachmentMb, int fallback = DefaultMaxAttachmentMb) =>
        maxAttachmentMb is >= MinAttachmentMb and <= MaxAttachmentMbLimit ? maxAttachmentMb : fallback;

    public static string SourceName(string siteHost, string itemName)
    {
        string name = $"{siteHost} / {itemName}";
        return name.Length <= MaxSourceName ? name : name[..(MaxSourceName - 1)] + "…";
    }

    /// <summary>The values of <paramref name="key"/> in the scopes of a connection's sources: the spaces or projects already sourced.</summary>
    protected static HashSet<string> IdsOf(IEnumerable<Source> sources, string key)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            try
            {
                using var doc = JsonDocument.Parse(source.ScopeJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(key, out var id))
                {
                    string? value = id.ValueKind switch
                    {
                        JsonValueKind.String => id.GetString(),
                        JsonValueKind.Number => id.GetRawText(),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(value))
                        ids.Add(value);
                }
            }
            catch (JsonException)
            {
            }
        }

        return ids;
    }
}
