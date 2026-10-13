using System.Diagnostics;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Sources;

namespace Connapse.Web.Services;

/// <summary>
/// The audit events that make adding Confluence and Jira sources measurable, as a site test's are:
/// the New source dialog opened for an Atlassian site, its spaces or projects listed (how many, how
/// long), the sources created (how many), and any step that failed. Every event of one dialog carries
/// the selection's attempt id, its product (<c>confluence</c> or <c>jira</c>) and the time since it
/// opened, so drop-off between steps can be counted per product. They carry the connection id and
/// counts only: no space or project names, no secret.
/// </summary>
public static class AtlassianSourceSetupEvents
{
    public const string Started = "source.setup_started";
    public const string SpacesListed = "source.setup_spaces_listed";
    public const string ProjectsListed = "source.setup_projects_listed";
    public const string Completed = "source.setup_completed";
    public const string StepFailed = "source.setup_step_failed";

    public const string SpacesStep = "spaces";
    public const string ProjectsStep = "projects";
    public const string CreateStep = "create";

    /// <summary>The step that lists the selection's items: <see cref="SpacesStep"/> or <see cref="ProjectsStep"/>.</summary>
    public static string ListStepOf(AtlassianSourceSelection selection) =>
        selection is ConfluenceSpaceSelection ? SpacesStep : ProjectsStep;

    public static Task StartedAsync(IAuditLogger audit, AtlassianSourceSelection selection, Guid connectionId) =>
        audit.LogAsync(Started, "connection", connectionId.ToString(),
            new { Provider = "Atlassian", selection.Attempt, selection.Product });

    /// <summary>The site's spaces or projects were listed; a Confluence event also says whether personal spaces were.</summary>
    public static Task ListedAsync(
        IAuditLogger audit, AtlassianSourceSelection selection, Guid connectionId, int count, long durationMs) =>
        selection is ConfluenceSpaceSelection confluence
            ? audit.LogAsync(SpacesListed, "connection", connectionId.ToString(), new
            {
                Provider = "Atlassian",
                selection.Attempt,
                selection.Product,
                Count = count,
                confluence.IncludePersonal,
                DurationMs = durationMs,
                TotalMs = TotalMs(selection),
            })
            : audit.LogAsync(ProjectsListed, "connection", connectionId.ToString(), new
            {
                Provider = "Atlassian",
                selection.Attempt,
                selection.Product,
                Count = count,
                DurationMs = durationMs,
                TotalMs = TotalMs(selection),
            });

    public static Task CompletedAsync(
        IAuditLogger audit, AtlassianSourceSelection selection, Guid connectionId, int created, int skipped, int notAdded) =>
        audit.LogAsync(Completed, "connection", connectionId.ToString(), new
        {
            Provider = "Atlassian",
            selection.Attempt,
            selection.Product,
            Created = created,
            Skipped = skipped,
            NotAdded = notAdded,
            TotalMs = TotalMs(selection),
        });

    /// <param name="reason">A short fixed code, such as <c>unavailable</c> or <c>nothing_added</c>: never exception text.</param>
    public static Task StepFailedAsync(
        IAuditLogger audit, AtlassianSourceSelection selection, Guid connectionId, string step, string reason) =>
        audit.LogAsync(StepFailed, "connection", connectionId.ToString(), new
        {
            Provider = "Atlassian",
            selection.Attempt,
            selection.Product,
            Step = step,
            Reason = reason,
            TotalMs = TotalMs(selection),
        });

    private static long TotalMs(AtlassianSourceSelection selection) =>
        (long)Stopwatch.GetElapsedTime(selection.StartedTimestamp).TotalMilliseconds;
}
