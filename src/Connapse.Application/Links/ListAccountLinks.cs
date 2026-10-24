using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Services;
using Connapse.Storage.Connectors.Custom;
using Connapse.Storage.Connectors.GitHub;
using Microsoft.Extensions.Options;

namespace Connapse.Application.Links;

/// <summary>Why reading account links didn't succeed.</summary>
public enum AccountLinkReadStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,
}

/// <summary>
/// What to read. <see cref="CustomStatus"/> asks each custom provider's source server whether the user
/// is linked; the profile page leaves it off because each provider's card asks for itself.
/// </summary>
public sealed record ListAccountLinksQuery(bool CustomStatus = true);

/// <summary>A GitHub account the user linked.</summary>
public sealed record GitHubAccountLink(string Login, DateTime ConnectedAt);

/// <summary>An Atlassian account the user linked.</summary>
public sealed record AtlassianAccountLink(string AccountId, DateTime ConnectedAt);

/// <summary>
/// A custom provider and the user's link to it. <see cref="Linked"/> is null when its status wasn't asked
/// for or its source server couldn't say.
/// </summary>
public sealed record CustomAccountLink(CustomProvider Provider, bool? Linked, string? DisplayName);

/// <summary>
/// The signed-in user's linked accounts, and whether this server offers linking for each provider. A
/// provider that isn't linked hides the documents only that account may read from this user's search.
/// </summary>
public sealed record AccountLinks(
    AwsIdentityLinkDto? Aws,
    bool AwsAvailable,
    AzureIdentityLinkDto? Azure,
    bool AzureAvailable,
    GitHubAccountLink? GitHub,
    bool GitHubAvailable,
    AtlassianAccountLink? Atlassian,
    bool AtlassianAvailable,
    IReadOnlyList<CustomAccountLink> Custom)
{
    /// <summary>Nothing linked and nothing offered: what a page shows when the read failed.</summary>
    public static AccountLinks None { get; } = new(null, false, null, false, null, false, null, false, []);
}

/// <summary>
/// Lists the caller's own account links. Only ever the caller's: there is no user to name, so one person
/// can't read another's. An agent key acts as no person and is refused.
/// </summary>
[Operation("links.list", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class ListAccountLinks(
    IAwsIdentityLinkService aws,
    IAzureIdentityLinkService azure,
    GitHubIdentityLinkStore gitHub,
    AtlassianIdentityLinkStore atlassian,
    ICustomAccountLinks custom,
    ConnapseGitHubApp gitHubApp,
    IProviderCredentialStore credentials,
    IOptionsMonitor<SamlSignInSettings> saml,
    IOptionsMonitor<AzureAdSignInSettings> azureAd,
    IOperationAuthorizer authorizer)
{
    public async Task<Attempt<AccountLinks, AccountLinkReadStatus>> RunAsync(
        OperationContext context, ListAccountLinksQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Account)
            || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid userId))
            return Attempt<AccountLinks, AccountLinkReadStatus>.Fail(AccountLinkReadStatus.Forbidden, "Only a person has linked accounts.");

        var gitHubLink = await gitHub.GetAsync(userId, ct);
        var atlassianLink = await atlassian.GetAsync(userId, ct);
        // An App whose stored client secret no longer matches GitHub's can't finish a sign-in.
        bool gitHubAvailable = await gitHubApp.CanSignInUsersAsync(ct) && await gitHubApp.StoredClientSecretMatchesAsync(ct) != false;

        var providers = await custom.LinkableAsync(ct);
        var customLinks = query.CustomStatus
            ? await Task.WhenAll(providers.Select(p => CustomLinkAsync(p, userId, ct)))
            : [.. providers.Select(p => new CustomAccountLink(p, null, null))];

        return Attempt<AccountLinks, AccountLinkReadStatus>.Succeed(new AccountLinks(
            await aws.GetAsync(userId, ct),
            saml.CurrentValue.IsConfigured,
            await azure.GetAsync(userId, ct),
            azureAd.CurrentValue.IsConfigured,
            gitHubLink is null ? null : new GitHubAccountLink(gitHubLink.Login, gitHubLink.ConnectedAt),
            gitHubAvailable,
            atlassianLink is null ? null : new AtlassianAccountLink(atlassianLink.AccountId, atlassianLink.ConnectedAt),
            await credentials.GetAtlassianLinkAppAsync(ct) is { SignInEnabled: true },
            customLinks));
    }

    /// <summary>
    /// One provider's link, or unknown when it can't be read: its source server is slow or down, or its
    /// stored token can't be decrypted. One broken provider never fails the whole list.
    /// </summary>
    private async Task<CustomAccountLink> CustomLinkAsync(CustomProvider provider, Guid userId, CancellationToken ct)
    {
        try
        {
            CustomLinkStatus status = await custom.StatusAsync(provider.Id, userId, ct);
            return new CustomAccountLink(provider, status.Linked, status.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new CustomAccountLink(provider, null, null);
        }
    }
}
