using Connapse.Core;
using Connapse.Web.Services;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// The Jira projects chosen on the New source dialog for an Atlassian site: one source out per
/// project, as <see cref="ConfluenceSpaceSelection"/> makes one per space, so the scope keys
/// written here are tested against the ones <c>ConnectorFactory</c> reads.
/// </summary>
public sealed class JiraProjectSelection : AtlassianSourceSelection<JiraProjectInfo, JiraProjectSelection>
{
    public const string Kind = "jira-project";

    /// <summary>Where the setup guide explains every field of this form.</summary>
    public const string GuideUrl = "https://github.com/Destrayon/Connapse/blob/main/docs/sources/atlassian-setup.md#5-jira-sources";

    public const string ProjectsLabel = "Projects";

    /// <summary>The labels the form shows, which the setup guide must name.</summary>
    public static readonly IReadOnlyList<string> FieldLabels = [ProjectsLabel, IncludeAttachmentsLabel, MaxAttachmentMbLabel];

    public const string NoProjectChosen = "Choose at least one project.";

    /// <summary>Under the Projects list: what ticking one does, and the usual reason one is missing.</summary>
    public const string ProjectsHelp =
        "Each project you tick becomes its own source. If a project is missing, let the service account see it: "
        + "on a private team-managed space (project), add it as Viewer under Space settings (Project settings) → Access; "
        + "on a company-managed space (project), grant it Browse Spaces (Browse Projects) in the permission scheme.";

    public const string IncludeAttachmentsShortHelp = "Index files attached to issues, such as PDFs and Word documents.";

    public const string IncludeAttachmentsHelp = IncludeAttachmentsShortHelp + " Comments are always included.";

    /// <summary>What the dialog says when adding projects failed unexpectedly; as for spaces, sources made before the failure are named.</summary>
    public static string AddFailedMessage(IReadOnlyCollection<string> created) =>
        "The projects could not be added. Try again; if it keeps failing, check the server log."
        + (created.Count > 0 ? $" Added before the failure: {string.Join(", ", created)}." : "");

    /// <summary>Why a Jira source's project is shown but not editable.</summary>
    public const string ProjectFixedNote = "A source covers one project; add a new source for another project.";

    /// <summary>The optional scope a site's credential needs before Jira attachments can be read.</summary>
    public const string AttachmentScope = Connapse.Storage.ConnectionTesters.AtlassianSiteScopes.JiraAttachment;

    /// <summary>Shown when the site's last test did not confirm the attachment scope, so attachments start off.</summary>
    public const string AttachmentScopeUnconfirmedNote =
        "Off by default: the site's last connection test didn't confirm the " + AttachmentScope
        + " permission, which attachments need. Create a credential that includes it, enter it with Replace credentials on the site, then turn this on.";

    /// <summary>A selection for a new source on <paramref name="site"/>; attachments start on only when their scope was confirmed.</summary>
    public static JiraProjectSelection ForSite(Connection? site)
    {
        bool confirmed = AttachmentScopeConfirmed(site?.Health);
        return new JiraProjectSelection { IncludeAttachments = confirmed, AttachmentScopeUnconfirmed = !confirmed };
    }

    /// <summary>Whether the site's last passing check confirmed <see cref="AttachmentScope"/>.</summary>
    public static bool AttachmentScopeConfirmed(ConnectionHealth? health) => ScopeConfirmed(health, AttachmentScope);

    public override string Product => Connapse.Storage.ConnectionTesters.AtlassianSiteScopes.JiraProduct;

    /// <summary>The end of a Jira source's default name, as the site's Jira connection is named.</summary>
    public const string SourceNameSuffix = " (Jira)";

    public override string NameSuffix => SourceNameSuffix;

    public override string IdField => "projectId";

    public override string RequiredAttachmentScope => AttachmentScope;

    public override string FieldIdPrefix => "jira";

    public override string SetupGuideUrl => GuideUrl;

    public override string ItemFixedNote => ProjectFixedNote;

    public override string AttachmentsShortHelp => IncludeAttachmentsShortHelp;

    /// <summary>The problem with the project list, shown next to it; null when there is none.</summary>
    public string? ProjectsError { get => ItemsError; set => ItemsError = value; }

    protected override string NoItemChosen => NoProjectChosen;

    public override string NotOnSiteLabel => "Not projects the service account can browse";

    public override string CouldNotCheckMessage => "Could not check the chosen projects against this site. Try again.";

    public override string FailureMessage(IReadOnlyCollection<string> created) => AddFailedMessage(created);

    protected override string ScopeJsonFor(JiraProjectInfo project) => ScopeJson(project, IncludeAttachments, MaxAttachmentMb);

    public override async Task<AtlassianListing<JiraProjectInfo>> ListOnSiteAsync(
        AtlassianSiteService sites, Guid connectionId, CancellationToken ct)
    {
        var listed = await sites.ListProjectsAsync(connectionId, ct);
        return new(listed.Outcome, listed.Projects, listed.Error, listed.TroubleshootingUrl);
    }

    public static string ScopeJson(JiraProjectInfo project, bool includeAttachments, int maxAttachmentMb) =>
        SourceScopeCodec.Write(new JiraProjectScope(
            project.Id,
            project.Key,
            // Shown on the source's edit page; the connector reads the project's current name itself.
            project.Name,
            includeAttachments,
            StorableMaxAttachmentMb(maxAttachmentMb)));

    /// <summary>The project ids that already have a source, from the scopes of a connection's sources.</summary>
    public static HashSet<string> ProjectIdsOf(IEnumerable<Source> sources) => IdsOf(sources, "projectId");
}
