using System.Text.Json;
using Connapse.Core;
using Connapse.Web.Services;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// The spaces chosen on the New source dialog for an Atlassian site: one source out per space.
/// Kept as a type of its own, like <c>GitHubRepositoryForm</c>, so the scope keys written here are
/// tested against the ones <c>ConnectorFactory</c> reads.
/// </summary>
public sealed class ConfluenceSpaceSelection : AtlassianSourceSelection
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

    public const string AttachmentScopeHelp =
        "Attachments need the " + AttachmentScope + " permission on the site's credential.";

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

    public override string RequiredAttachmentScope => AttachmentScope;

    public override string FieldIdPrefix => "confluence";

    public override string ItemFixedNote => SpaceFixedNote;

    public override string AttachmentsShortHelp => IncludeAttachmentsShortHelp;

    /// <summary>The spaces the site offers, as last listed.</summary>
    public IReadOnlyList<ConfluenceSpaceInfo> Available { get; set; } = [];

    /// <summary>Ids of the spaces ticked.</summary>
    public HashSet<string> Selected { get; } = [];

    public bool IncludePersonal { get; set; }

    /// <summary>The problem with the space list, shown next to it; null when there is none.</summary>
    public string? SpacesError { get; set; }

    /// <summary>Checks every field, setting each one's error; returns the first error, or null when all are valid.</summary>
    public string? Validate()
    {
        SpacesError = Selected.Count == 0 ? NoSpaceChosen : null;
        ValidateMaxAttachmentMb();
        return SpacesError ?? MaxAttachmentMbError;
    }

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

    /// <summary>
    /// One create request per ticked space not already a source. Spaces already sourced on the
    /// connection (matched on id) are returned in <paramref name="skipped"/> instead.
    /// </summary>
    public IReadOnlyList<CreateSourceRequest> ToRequests(
        Guid connectionId, string siteHost, IReadOnlySet<string> existingSpaceIds, out IReadOnlyList<string> skipped)
    {
        var requests = new List<CreateSourceRequest>();
        var skippedNames = new List<string>();

        foreach (var space in Available.Where(s => Selected.Contains(s.Id)))
        {
            if (existingSpaceIds.Contains(space.Id))
            {
                skippedNames.Add(space.Name);
                continue;
            }

            requests.Add(new CreateSourceRequest(
                SourceName(siteHost, space.Name), connectionId, ScopeJson(space, IncludeAttachments, MaxAttachmentMb)));
        }

        skipped = skippedNames;
        return requests;
    }

    /// <summary>
    /// A copy holding only the ticked spaces the site really has, taken from <paramref name="onSite"/>
    /// (a fresh listing of the connection's own spaces) rather than from what the dialog showed.
    /// The names of the ticked spaces the site does not have are returned in <paramref name="refused"/>.
    /// </summary>
    public ConfluenceSpaceSelection VerifiedAgainst(IReadOnlyList<ConfluenceSpaceInfo> onSite, out IReadOnlyList<string> refused)
    {
        var verified = new ConfluenceSpaceSelection
        {
            IncludePersonal = IncludePersonal,
            IncludeAttachments = IncludeAttachments,
            MaxAttachmentMb = MaxAttachmentMb,
            Available = onSite.Where(s => Selected.Contains(s.Id)).ToList(),
        };
        verified.Selected.UnionWith(verified.Available.Select(s => s.Id));

        refused = Selected
            .Where(id => !verified.Selected.Contains(id))
            .Select(id => Available.FirstOrDefault(s => s.Id == id)?.Name ?? id)
            .ToList();
        return verified;
    }

    /// <summary>The space ids that already have a source, from the scopes of a connection's sources.</summary>
    public static HashSet<string> SpaceIdsOf(IEnumerable<Source> sources) => IdsOf(sources, "spaceId");
}
