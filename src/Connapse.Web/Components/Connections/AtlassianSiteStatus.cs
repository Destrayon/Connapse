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

    /// <summary>
    /// The connection's own product: ready when its last check passed or only warned. A connection
    /// reads one product, so the list never names the other.
    /// </summary>
    public static IReadOnlyList<AtlassianProductStatus> ProductsOf(Connection site)
    {
        string product = ProductOf(site);
        string name = AtlassianSiteScopes.DisplayName(product);
        return site.Health switch
        {
            null => [new AtlassianProductStatus(product, name, false, "Not checked yet", "text-muted", "bi-question-circle")],
            { Succeeded: true } or { Warning: true } =>
                [new AtlassianProductStatus(product, name, true, "Ready", "text-success", "bi-check-circle")],
            _ => [new AtlassianProductStatus(product, name, false, "Not available: the last test couldn't read it",
                "text-muted", "bi-dash-circle")],
        };
    }

    /// <summary>The product a connection reads, as <c>confluence</c> or <c>jira</c>; a connection without one is Confluence.</summary>
    public static string ProductOf(Connection site) =>
        AtlassianSite.FromConfigJson(site.ConfigJson)?.Product == AtlassianProduct.Jira
            ? AtlassianSiteScopes.JiraProduct
            : AtlassianSiteScopes.ConfluenceProduct;

    /// <summary>Whether sources of <paramref name="product"/> can be added on the site, as <see cref="ProductsOf"/> shows it.</summary>
    public static bool IsReady(Connection site, string product) =>
        ProductsOf(site).Any(p => p.Ready && string.Equals(p.Product, product, StringComparison.OrdinalIgnoreCase));

    /// <summary>A check time as the page shows it: UTC, to the minute.</summary>
    public static string When(DateTime? utc) =>
        utc is { } at ? at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";
}

/// <summary>One product of a saved site as the Connections page shows it.</summary>
/// <param name="Ready">Sources of this product can be added.</param>
public sealed record AtlassianProductStatus(string Product, string Name, bool Ready, string Text, string CssClass, string Icon);
