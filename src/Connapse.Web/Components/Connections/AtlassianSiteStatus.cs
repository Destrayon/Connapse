using System.Globalization;
using Connapse.Core;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors.Atlassian;

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

    /// <summary>The product a connection reads, as <c>confluence</c> or <c>jira</c>; a connection without one is Confluence.</summary>
    public static string ProductOf(Connection site) =>
        AtlassianSite.FromConfigJson(site.ConfigJson)?.Product == AtlassianProduct.Jira
            ? AtlassianSiteScopes.JiraProduct
            : AtlassianSiteScopes.ConfluenceProduct;

    /// <summary>The product a connection reads, as the page names it: Confluence or Jira.</summary>
    public static string ProductName(Connection site) => AtlassianSiteScopes.DisplayName(ProductOf(site));

    /// <summary>A connection as lists show it, its product first: <c>Confluence · acme.atlassian.net</c>.</summary>
    public static string Label(Connection site, string name) => $"{ProductName(site)} · {name}";

    /// <summary>
    /// Whether sources of <paramref name="product"/> can be added on the site: it reads that product,
    /// and its last check passed or only warned.
    /// </summary>
    public static bool IsReady(Connection site, string product) =>
        string.Equals(ProductOf(site), product, StringComparison.OrdinalIgnoreCase)
        && site.Health is { Succeeded: true } or { Warning: true };

    /// <summary>A check time as the page shows it: UTC, to the minute.</summary>
    public static string When(DateTime? utc) =>
        utc is { } at ? at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
}
