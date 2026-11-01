using Connapse.Application.Links;

namespace Connapse.Web.Endpoints;

/// <summary>
/// One provider as <c>GET /api/v1/auth/links</c> shows it: whether the caller linked an account there, which
/// one, since when, and whether this server offers linking. <see cref="Linked"/> is null when a custom
/// provider's source server couldn't say. Search hides what only an unlinked account may read.
/// </summary>
public sealed record AccountLinkResponse(
    string Provider,
    Guid? ProviderId,
    string Name,
    bool? Linked,
    string? Account,
    DateTime? LinkedAt,
    bool CanLink);

/// <summary>The caller's account links, and the page where a person links one in their browser.</summary>
public sealed record AccountLinksResponse(IReadOnlyList<AccountLinkResponse> Links, string LinkPage)
{
    public const string ProfilePage = "/profile/integrations";

    public static AccountLinksResponse From(AccountLinks links) => new(
    [
        new("aws", null, "AWS", links.Aws is not null, links.Aws?.DirectoryUserName, links.Aws?.ConnectedAt, links.AwsAvailable),
        new("azure", null, "Microsoft Entra ID", links.Azure is not null, links.Azure?.DisplayName, links.Azure?.ConnectedAt, links.AzureAvailable),
        new("github", null, "GitHub", links.GitHub is not null, links.GitHub?.Login, links.GitHub?.ConnectedAt, links.GitHubAvailable),
        new("atlassian", null, "Atlassian", links.Atlassian is not null, links.Atlassian?.AccountId, links.Atlassian?.ConnectedAt, links.AtlassianAvailable),
        // A custom provider is listed once it has a link secret, so its users can link to it.
        .. links.Custom.Select(c => new AccountLinkResponse("custom", c.Provider.Id, c.Provider.Name, c.Linked, c.DisplayName, null, true)),
    ], ProfilePage);
}
