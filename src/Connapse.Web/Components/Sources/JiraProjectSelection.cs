using System.Text.Json;
using Connapse.Core;
using Connapse.Web.Services;

namespace Connapse.Web.Components.Sources;

/// <summary>
/// The Jira projects chosen on the New source dialog for an Atlassian site: one source out per
/// project, as <see cref="ConfluenceSpaceSelection"/> makes one per space, so the scope keys
/// written here are tested against the ones <c>ConnectorFactory</c> reads.
/// </summary>
public sealed class JiraProjectSelection : AtlassianSourceSelection
{
    public const string Kind = "jira-project";

    /// <summary>Where the setup guide explains Atlassian sites; it has no Jira sources section yet.</summary>
    public const string GuideUrl = Connapse.Storage.ConnectionTesters.AtlassianSiteTroubleshooting.GuideUrl;

    public const string ProjectsLabel = "Projects";

    public const string NoProjectChosen = "Choose at least one project.";

    /// <summary>Under the Projects list: what ticking one does, and the usual reason one is missing.</summary>
    public const string ProjectsHelp =
        "Each project you tick becomes its own source. If a project is missing, give the service account the Browse projects permission in it.";

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

    public override string RequiredAttachmentScope => AttachmentScope;

    public override string FieldIdPrefix => "jira";

    public override string ItemFixedNote => ProjectFixedNote;

    public override string AttachmentsShortHelp => IncludeAttachmentsShortHelp;

    /// <summary>The projects the site offers, as last listed.</summary>
    public IReadOnlyList<JiraProjectInfo> Available { get; set; } = [];

    /// <summary>Ids of the projects ticked.</summary>
    public HashSet<string> Selected { get; } = [];

    /// <summary>The problem with the project list, shown next to it; null when there is none.</summary>
    public string? ProjectsError { get; set; }

    /// <summary>Checks every field, setting each one's error; returns the first error, or null when all are valid.</summary>
    public string? Validate()
    {
        ProjectsError = Selected.Count == 0 ? NoProjectChosen : null;
        ValidateMaxAttachmentMb();
        return ProjectsError ?? MaxAttachmentMbError;
    }

    public static string ScopeJson(JiraProjectInfo project, bool includeAttachments, int maxAttachmentMb) =>
        JsonSerializer.Serialize(new
        {
            kind = Kind,
            projectId = project.Id,
            projectKey = project.Key,
            // Shown on the source's edit page; the connector reads the project's current name itself.
            projectName = project.Name,
            includeAttachments,
            maxAttachmentMb = StorableMaxAttachmentMb(maxAttachmentMb),
        });

    /// <summary>
    /// One create request per ticked project not already a source. Projects already sourced on the
    /// connection (matched on id) are returned in <paramref name="skipped"/> instead.
    /// </summary>
    public IReadOnlyList<CreateSourceRequest> ToRequests(
        Guid connectionId, string siteHost, IReadOnlySet<string> existingProjectIds, out IReadOnlyList<string> skipped)
    {
        var requests = new List<CreateSourceRequest>();
        var skippedNames = new List<string>();

        foreach (var project in Available.Where(p => Selected.Contains(p.Id)))
        {
            if (existingProjectIds.Contains(project.Id))
            {
                skippedNames.Add(project.Name);
                continue;
            }

            requests.Add(new CreateSourceRequest(
                SourceName(siteHost, project.Name), connectionId, ScopeJson(project, IncludeAttachments, MaxAttachmentMb)));
        }

        skipped = skippedNames;
        return requests;
    }

    /// <summary>
    /// A copy holding only the ticked projects the site really has, taken from <paramref name="onSite"/>
    /// (a fresh listing as the service account) rather than from what the dialog showed. The names of
    /// the ticked projects the site does not have are returned in <paramref name="refused"/>.
    /// </summary>
    public JiraProjectSelection VerifiedAgainst(IReadOnlyList<JiraProjectInfo> onSite, out IReadOnlyList<string> refused)
    {
        var verified = new JiraProjectSelection
        {
            IncludeAttachments = IncludeAttachments,
            MaxAttachmentMb = MaxAttachmentMb,
            Available = onSite.Where(p => Selected.Contains(p.Id)).ToList(),
        };
        verified.Selected.UnionWith(verified.Available.Select(p => p.Id));

        refused = Selected
            .Where(id => !verified.Selected.Contains(id))
            .Select(id => Available.FirstOrDefault(p => p.Id == id)?.Name ?? id)
            .ToList();
        return verified;
    }

    /// <summary>The project ids that already have a source, from the scopes of a connection's sources.</summary>
    public static HashSet<string> ProjectIdsOf(IEnumerable<Source> sources) => IdsOf(sources, "projectId");
}
