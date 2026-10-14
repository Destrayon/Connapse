using System.Text.Json;
using Connapse.Core;
using Connapse.Web.Services;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// The spaces chosen on the New source dialog for an Atlassian site: one source out per space.
/// Kept as a type of its own, like <c>GitHubRepositoryForm</c>, so the scope keys written here are
/// tested against the ones <c>ConnectorFactory</c> reads.
/// </summary>
public sealed class ConfluenceSpaceSelection : AtlassianSourceSelection<ConfluenceSpaceInfo, ConfluenceSpaceSelection>
{
    /// <summary>Where the setup guide explains every field of this form.</summary>
    public const string GuideUrl = "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md#4-confluence-sources";

    public const string SpacesLabel = "Spaces";
    public const string IncludePersonalLabel = "Include personal spaces";

    /// <summary>The labels the form shows, which the setup guide must name.</summary>
    public static readonly IReadOnlyList<string> FieldLabels =
        [SpacesLabel, IncludePersonalLabel, IncludeAttachmentsLabel, MaxAttachmentMbLabel];

    public const string NoSpaceChosen = "Choose at least one space.";

    /// <summary>Under the Spaces list: what ticking one does, and the usual reason one is missing.</summary>
    public const string SpacesHelp =
        "Each space you tick becomes its own source. If a space is missing, give the service account the Viewer role in it.";

    public const string IncludeAttachmentsShortHelp = "Index files attached to pages, such as PDFs and Word documents.";

    public const string IncludeAttachmentsHelp = IncludeAttachmentsShortHelp + " Comments and blog posts are always included.";

    /// <summary>
    /// What the dialog says when adding spaces failed unexpectedly. The exception goes to the log,
    /// not here; any source made before the failure is named, since trying again skips it.
    /// </summary>
    public static string AddFailedMessage(IReadOnlyCollection<string> created) =>
        "The spaces could not be added. Try again; if it keeps failing, check the server log."
        + (created.Count > 0 ? $" Added before the failure: {string.Join(", ", created)}." : "");

    /// <summary>Why a Confluence source's space is shown but not editable.</summary>
    public const string SpaceFixedNote = "A source covers one space; add a new source for another space.";

    /// <summary>The optional scope a site's credential needs before attachments can be read.</summary>
    public const string AttachmentScope = "read:attachment:confluence";

    /// <summary>Shown when the site's last test did not confirm the attachment scope, so attachments start off.</summary>
    public const string AttachmentScopeUnconfirmedNote =
        "Off by default: the site's last connection test didn't confirm the " + AttachmentScope
        + " permission, which attachments need. Create a credential that includes it, enter it with Replace credentials on the site, then turn this on.";

    /// <summary>
    /// A selection for a new source on <paramref name="site"/>. Attachments start on only when the
    /// site's last passing test confirmed the scope they need; otherwise every attachment read
    /// would be refused.
    /// </summary>
    public static ConfluenceSpaceSelection ForSite(Connection? site)
    {
        bool confirmed = AttachmentScopeConfirmed(site?.Health);
        return new ConfluenceSpaceSelection { IncludeAttachments = confirmed, AttachmentScopeUnconfirmed = !confirmed };
    }

    /// <summary>Whether the site's last passing check confirmed <see cref="AttachmentScope"/>.</summary>
    public static bool AttachmentScopeConfirmed(ConnectionHealth? health) => ScopeConfirmed(health, AttachmentScope);

    public override string Product => Connapse.Storage.ConnectionTesters.AtlassianSiteScopes.ConfluenceProduct;

    public override string IdField => "spaceId";

    public override string RequiredAttachmentScope => AttachmentScope;

    public override string FieldIdPrefix => "confluence";

    public override string SetupGuideUrl => GuideUrl;

    public override string ItemFixedNote => SpaceFixedNote;

    public override string AttachmentsShortHelp => IncludeAttachmentsShortHelp;

    public bool IncludePersonal { get; set; }

    /// <summary>The problem with the space list, shown next to it; null when there is none.</summary>
    public string? SpacesError { get => ItemsError; set => ItemsError = value; }

    protected override string NoItemChosen => NoSpaceChosen;

    public override string NotOnSiteLabel => "Not spaces on this site";

    public override string CouldNotCheckMessage => "Could not check the chosen spaces against this site. Try again.";

    public override string FailureMessage(IReadOnlyCollection<string> created) => AddFailedMessage(created);

    protected override string ScopeJsonFor(ConfluenceSpaceInfo space) => ScopeJson(space, IncludeAttachments, MaxAttachmentMb);

    // Personal spaces too: whatever the dialog listed, a ticked space is checked against all of them.
    public override async Task<AtlassianListing<ConfluenceSpaceInfo>> ListOnSiteAsync(
        AtlassianSiteService sites, Guid connectionId, CancellationToken ct)
    {
        var listed = await sites.ListSpacesAsync(connectionId, includePersonal: true, ct);
        return new(listed.Outcome, listed.Spaces, listed.Error, listed.TroubleshootingUrl);
    }

    protected override void CopyOptionsTo(ConfluenceSpaceSelection copy) => copy.IncludePersonal = IncludePersonal;

    public static string ScopeJson(ConfluenceSpaceInfo space, bool includeAttachments, int maxAttachmentMb) =>
        JsonSerializer.Serialize(new
        {
            kind = "confluence-space",
            spaceId = space.Id,
            spaceKey = space.Key,
            // Shown on the source's edit page; the connector reads the space's current name itself.
            spaceName = space.Name,
            includeAttachments,
            maxAttachmentMb = StorableMaxAttachmentMb(maxAttachmentMb),
        });

    /// <summary>The space ids that already have a source, from the scopes of a connection's sources.</summary>
    public static HashSet<string> SpaceIdsOf(IEnumerable<Source> sources) => IdsOf(sources, "spaceId");
}
