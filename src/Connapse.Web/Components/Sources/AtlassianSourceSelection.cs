using System.Text.Json;
using Connapse.Core;
using Connapse.Web.Services;

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

    /// <summary>The product this selection adds sources of: <c>confluence</c> or <c>jira</c>.</summary>
    public abstract string Product { get; }

    /// <summary>The scope key naming the space or project, which is fixed once a source exists.</summary>
    public abstract string IdField { get; }

    /// <summary>The optional scope this product's attachments need on the site's credential.</summary>
    public abstract string RequiredAttachmentScope { get; }

    /// <summary>Whether the site's last passing check confirmed <see cref="RequiredAttachmentScope"/>.</summary>
    public bool AttachmentScopeConfirmedFor(ConnectionHealth? health) => ScopeConfirmed(health, RequiredAttachmentScope);

    // What the edit page shows for this product, so one attachments section serves both.

    /// <summary>The prefix of the edit page's field ids: <c>confluence</c> or <c>jira</c>.</summary>
    public abstract string FieldIdPrefix { get; }

    /// <summary>The setup guide's section for this product's sources.</summary>
    public abstract string SetupGuideUrl { get; }

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

    /// <summary>
    /// A source's default name, <c>{host} / {item}</c> followed by <paramref name="suffix"/>. A name too
    /// long is shortened before the suffix, so the suffix always survives.
    /// </summary>
    public static string SourceName(string siteHost, string itemName, string suffix = "")
    {
        string name = $"{siteHost} / {itemName}";
        return name.Length + suffix.Length <= MaxSourceName
            ? name + suffix
            : name[..(MaxSourceName - 1 - suffix.Length)] + "…" + suffix;
    }

    /// <summary>
    /// What a source's default name ends with: nothing for Confluence, <c> (Jira)</c> for Jira, so a
    /// space and a project of one name on one site get different names.
    /// </summary>
    public virtual string NameSuffix => "";

    /// <summary>The ids of this product's items that already have a source, from the scopes of a connection's sources.</summary>
    public HashSet<string> ExistingIdsOf(IEnumerable<Source> sources) => IdsOf(sources, IdField);

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

/// <summary>
/// What a space or project picker does alike for both products: hold the items the site offers and
/// the ones ticked, check them, check them again against a fresh listing, and turn them into one
/// create request each. <typeparamref name="TSelf"/> is the product's own selection, so a verified
/// copy keeps its type.
/// </summary>
public abstract class AtlassianSourceSelection<TItem, TSelf> : AtlassianSourceSelection
    where TItem : IAtlassianSourceItem
    where TSelf : AtlassianSourceSelection<TItem, TSelf>, new()
{
    /// <summary>The items the site offers, as last listed.</summary>
    public IReadOnlyList<TItem> Available { get; set; } = [];

    /// <summary>Ids of the items ticked.</summary>
    public HashSet<string> Selected { get; } = [];

    /// <summary>The problem with the item list, shown next to it; null when there is none.</summary>
    public string? ItemsError { get; set; }

    /// <summary>The error when nothing is ticked.</summary>
    protected abstract string NoItemChosen { get; }

    /// <summary>How the dialog names ticked items the site does not have, before their names.</summary>
    public abstract string NotOnSiteLabel { get; }

    /// <summary>What the dialog says when the ticked items could not be checked against the site.</summary>
    public abstract string CouldNotCheckMessage { get; }

    /// <summary>What the dialog says when adding failed unexpectedly, naming any source made before it.</summary>
    public abstract string FailureMessage(IReadOnlyCollection<string> created);

    /// <summary>The scope a source of <paramref name="item"/> is stored with, under this selection's attachment settings.</summary>
    protected abstract string ScopeJsonFor(TItem item);

    /// <summary>The site's items now, read as its service account.</summary>
    public abstract Task<AtlassianListing<TItem>> ListOnSiteAsync(AtlassianSiteService sites, Guid connectionId, CancellationToken ct);

    /// <summary>Copies what the product adds beyond the attachment settings onto a verified copy.</summary>
    protected virtual void CopyOptionsTo(TSelf copy)
    {
    }

    /// <summary>Checks every field, setting each one's error; returns the first error, or null when all are valid.</summary>
    public string? Validate()
    {
        ItemsError = Selected.Count == 0 ? NoItemChosen : null;
        ValidateMaxAttachmentMb();
        return ItemsError ?? MaxAttachmentMbError;
    }

    /// <summary>
    /// One create request per ticked item not already a source. Items already sourced on the
    /// connection (matched on id) are returned in <paramref name="skipped"/> instead.
    /// </summary>
    public IReadOnlyList<CreateSourceRequest> ToRequests(
        Guid connectionId, string siteHost, IReadOnlySet<string> existingIds, out IReadOnlyList<string> skipped)
    {
        var requests = new List<CreateSourceRequest>();
        var skippedNames = new List<string>();

        foreach (var item in Available.Where(i => Selected.Contains(i.Id)))
        {
            if (existingIds.Contains(item.Id))
            {
                skippedNames.Add(item.Name);
                continue;
            }

            requests.Add(new CreateSourceRequest(SourceName(siteHost, item.Name, NameSuffix), connectionId, ScopeJsonFor(item)));
        }

        skipped = skippedNames;
        return requests;
    }

    /// <summary>
    /// A copy holding only the ticked items the site really has, taken from <paramref name="onSite"/>
    /// (a fresh listing of the connection's own items) rather than from what the dialog showed.
    /// The names of the ticked items the site does not have are returned in <paramref name="refused"/>.
    /// </summary>
    public TSelf VerifiedAgainst(IReadOnlyList<TItem> onSite, out IReadOnlyList<string> refused)
    {
        var verified = new TSelf
        {
            IncludeAttachments = IncludeAttachments,
            MaxAttachmentMb = MaxAttachmentMb,
            Available = onSite.Where(i => Selected.Contains(i.Id)).ToList(),
        };
        CopyOptionsTo(verified);
        verified.Selected.UnionWith(verified.Available.Select(i => i.Id));

        refused = Selected
            .Where(id => !verified.Selected.Contains(id))
            .Select(id => Available.FirstOrDefault(i => i.Id == id)?.Name ?? id)
            .ToList();
        return verified;
    }
}
