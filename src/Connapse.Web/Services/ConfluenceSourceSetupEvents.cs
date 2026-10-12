using System.Diagnostics;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Sources;

namespace Connapse.Web.Services;

/// <summary>
/// The audit events that make adding Confluence sources measurable, as a site test's are: the New
/// source dialog opened for an Atlassian site, its spaces listed (how many, how long), the sources
/// created (how many), and any step that failed. Every event of one dialog carries the selection's
/// attempt id and the time since it opened, so drop-off between steps can be counted. They carry
/// the connection id and counts only: no space names, no secret.
/// </summary>
public static class ConfluenceSourceSetupEvents
{
    public const string Started = "source.setup_started";
    public const string SpacesListed = "source.setup_spaces_listed";
    public const string Completed = "source.setup_completed";
    public const string StepFailed = "source.setup_step_failed";

    public const string SpacesStep = "spaces";
    public const string CreateStep = "create";

    public static Task StartedAsync(IAuditLogger audit, ConfluenceSpaceSelection selection, Guid connectionId) =>
        audit.LogAsync(Started, "connection", connectionId.ToString(),
            new { Provider = "Atlassian", selection.Attempt });

    public static Task SpacesListedAsync(
        IAuditLogger audit, ConfluenceSpaceSelection selection, Guid connectionId, int count, long durationMs) =>
        audit.LogAsync(SpacesListed, "connection", connectionId.ToString(), new
        {
            Provider = "Atlassian",
            selection.Attempt,
            Count = count,
            selection.IncludePersonal,
            DurationMs = durationMs,
            TotalMs = TotalMs(selection),
        });

    public static Task CompletedAsync(
        IAuditLogger audit, ConfluenceSpaceSelection selection, Guid connectionId, int created, int skipped, int notAdded) =>
        audit.LogAsync(Completed, "connection", connectionId.ToString(), new
        {
            Provider = "Atlassian",
            selection.Attempt,
            Created = created,
            Skipped = skipped,
            NotAdded = notAdded,
            TotalMs = TotalMs(selection),
        });

    /// <param name="reason">A short fixed code, such as <c>unavailable</c> or <c>nothing_added</c>: never exception text.</param>
    public static Task StepFailedAsync(
        IAuditLogger audit, ConfluenceSpaceSelection selection, Guid connectionId, string step, string reason) =>
        audit.LogAsync(StepFailed, "connection", connectionId.ToString(), new
        {
            Provider = "Atlassian",
            selection.Attempt,
            Step = step,
            Reason = reason,
            TotalMs = TotalMs(selection),
        });

    private static long TotalMs(ConfluenceSpaceSelection selection) =>
        (long)Stopwatch.GetElapsedTime(selection.StartedTimestamp).TotalMilliseconds;
}
