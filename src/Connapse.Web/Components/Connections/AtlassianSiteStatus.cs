using System.Globalization;
using Connapse.Core;
using Connapse.Storage.ConnectionTesters;

namespace Connapse.Web.Components.Connections;

/// <summary>A site's health in the words of the Connections page: a status, its colour and icon.</summary>
/// <param name="NeedsNewCredentials">Atlassian refused the stored credential; only replacing it helps.</param>
public sealed record AtlassianSiteStatus(string Text, string CssClass, string Icon, bool NeedsNewCredentials)
{
    public static AtlassianSiteStatus Of(Connection site) => site switch
    {
        { HasSecret: false } =>
            new("Needs new credentials: no secret is stored", "text-danger", "bi-exclamation-triangle", true),
        { Health: null } =>
            new("Not checked yet: choose Test connection", "text-muted", "bi-question-circle", false),
        { Health.CredentialRejected: true } =>
            new("Needs new credentials", "text-danger", "bi-exclamation-triangle", true),
        { Health.Succeeded: true } =>
            new("Working", "text-success", "bi-check-circle", false),
        { Health.Warning: true } =>
            new("Saved; administrator access not confirmed yet", "text-warning-emphasis", "bi-exclamation-triangle", false),
        { Health: { } h } =>
            new($"Last check failed: {AtlassianConnectionTester.StepLabel(h.Step)}", "text-danger", "bi-x-circle", false),
    };

    /// <summary>A check time as the page shows it: UTC, to the minute.</summary>
    public static string When(DateTime? utc) =>
        utc is { } at ? at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
}
