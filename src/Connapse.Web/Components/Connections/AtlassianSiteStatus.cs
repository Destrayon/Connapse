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

    /// <summary>
    /// Each product's state: ready when the last check that passed or only warned could read it.
    /// A check saved before Jira was tested lists no products and tested only Confluence.
    /// </summary>
    public static IReadOnlyList<AtlassianProductStatus> ProductsOf(Connection site)
    {
        ConnectionHealth? health = site.Health;
        IReadOnlyList<string> ready = health?.Products
            ?? (health is { Succeeded: true } or { Warning: true } ? [AtlassianSiteScopes.ConfluenceProduct] : []);
        bool current = health is { Succeeded: true } or { Warning: true };

        return [.. new[] { AtlassianSiteScopes.ConfluenceProduct, AtlassianSiteScopes.JiraProduct }.Select(product =>
        {
            string name = AtlassianSiteScopes.DisplayName(product);
            if (health is null)
                return new AtlassianProductStatus(product, name, false, "Not checked yet", "text-muted", "bi-question-circle");
            return ready.Contains(product, StringComparer.OrdinalIgnoreCase)
                ? new AtlassianProductStatus(product, name, true, current ? "Ready" : "Ready at the last successful check",
                    "text-success", "bi-check-circle")
                : new AtlassianProductStatus(product, name, false, "Not available: the last test couldn't read it",
                    "text-muted", "bi-dash-circle");
        })];
    }

    /// <summary>A check time as the page shows it: UTC, to the minute.</summary>
    public static string When(DateTime? utc) =>
        utc is { } at ? at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
}

/// <summary>One product of a saved site as the Connections page shows it.</summary>
/// <param name="Ready">Sources of this product can be added.</param>
public sealed record AtlassianProductStatus(string Product, string Name, bool Ready, string Text, string CssClass, string Icon);
