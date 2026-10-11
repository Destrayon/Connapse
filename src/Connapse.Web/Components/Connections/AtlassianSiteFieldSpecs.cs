using Connapse.Storage.ConnectionTesters;
using Connapse.Web.Components.Providers;

namespace Connapse.Web.Components.Connections;

/// <summary>
/// The Atlassian site connection's fields, in the order an administrator gathers them: the site's
/// address, then the service account's OAuth 2.0 credential. The REST API takes the same values
/// under <see cref="ApiNames"/>, and a test checks the setup guide names every label.
/// </summary>
public static class AtlassianSiteFieldSpecs
{
    public const string ServiceAccountSection = AtlassianSiteTroubleshooting.GuideUrl + "#2-service-account";
    public const string SiteConnectionSection = AtlassianSiteTroubleshooting.GuideUrl + "#3-site-connection";

    private const string SiteGroup = "Site";
    private const string CredentialGroup = "Service account credential";
    private const string WhereToFind = "Where to find this";

    private const string ClientIdHelp =
        "On the service account in Atlassian Administration, under its OAuth 2.0 credential.";

    private const string ClientSecretHelp =
        "Shown once, when the credential is created. Stored encrypted and never shown again.";

    public static readonly ProviderFieldSpec SiteUrl = new(
        "atlassian-site-url", "Site URL", ProviderFieldKind.Text, Required: true,
        "The address in your browser when you open Confluence, for example https://acme.atlassian.net.",
        SiteGroup, SiteConnectionSection, WhereToFind,
        Placeholder: "https://your-site.atlassian.net");

    public static readonly ProviderFieldSpec ClientId = new(
        "atlassian-client-id", "Client ID", ProviderFieldKind.Text, Required: true,
        ClientIdHelp, CredentialGroup, ServiceAccountSection, WhereToFind);

    public static readonly ProviderFieldSpec ClientSecret = new(
        "atlassian-client-secret", "Client secret", ProviderFieldKind.Secret, Required: true,
        ClientSecretHelp, CredentialGroup, ServiceAccountSection, WhereToFind);

    public static readonly ProviderFieldSpec ReplaceClientId = ClientId with { Id = "atlassian-replace-client-id" };

    public static readonly ProviderFieldSpec ReplaceClientSecret = ClientSecret with { Id = "atlassian-replace-client-secret" };

    /// <summary>The fields of a new site, in the order they are rendered.</summary>
    public static IReadOnlyList<ProviderFieldSpec> NewSite { get; } = [SiteUrl, ClientId, ClientSecret];

    /// <summary>Every field, for the check that the setup guide documents each one.</summary>
    public static IReadOnlyList<ProviderFieldSpec> All { get; } = [SiteUrl, ClientId, ClientSecret, ReplaceClientId, ReplaceClientSecret];

    /// <summary>The REST API's name for each label, identical across the create and replace routes.</summary>
    public static IReadOnlyDictionary<string, string> ApiNames { get; } = new Dictionary<string, string>
    {
        ["Site URL"] = "siteUrl",
        ["Client ID"] = "clientId",
        ["Client secret"] = "clientSecret",
    };

    /// <summary>
    /// Every label the setup guide must contain: the fields, and the Cloud ID the page shows after
    /// looking up a site.
    /// </summary>
    public static IReadOnlyList<string> GuideLabels { get; } = ["Site URL", "Client ID", "Client secret", "Cloud ID"];

    /// <summary>Every anchor of the setup guide this page links to.</summary>
    public static IReadOnlyList<string> GuideAnchors { get; } =
        ["2-service-account", "3-site-connection", .. AtlassianSiteTroubleshooting.All];
}
