using System.Security.Claims;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.GitHub;
using Microsoft.Extensions.Logging;

namespace Connapse.Application.Providers;

/// <summary>Why a GitHub App setup action didn't succeed.</summary>
public enum GitHubAppStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(400, "invalid-github-app", "Invalid GitHub App values")]
    Invalid,

    [Failure(409, "github-app-replace-unconfirmed", "A different GitHub App is stored")]
    ReplaceUnconfirmed,

    [Failure(400, "github-app-refused", "GitHub refused the App's values")]
    Refused,

    [Failure(502, "github-unavailable", "GitHub couldn't be asked")]
    Unavailable,

    [Failure(400, "github-manifest-expired", "The GitHub App creation expired")]
    Expired,

    [Failure(400, "github-manifest-no-code", "GitHub sent no code")]
    NoCode,
}

/// <summary>
/// A GitHub App registered by hand: its id, its private key and, for people to link their GitHub accounts, its client
/// secret. A blank key or secret keeps the stored one when the App id is the stored App's. Replacing a different stored
/// App needs <paramref name="ReplaceExisting"/>.
/// </summary>
public sealed record GitHubAppRequest(long? AppId, string? PrivateKeyPem = null, string? ClientSecret = null, bool ReplaceExisting = false)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"AppId = {AppId}, PrivateKeyPem = {(PrivateKeyPem is null ? "null" : "***")}, "
            + $"ClientSecret = {(ClientSecret is null ? "null" : "***")}, ReplaceExisting = {ReplaceExisting}");
        return true;
    }
}

/// <summary>
/// The App as GitHub described it. <see cref="CanSignIn"/> is false while no client secret is stored, so people can't
/// link their GitHub accounts yet; <see cref="NewApp"/> is true when it isn't the App stored before.
/// </summary>
public sealed record GitHubAppSaved(GitHubAppRegistration App, bool CanSignIn, bool NewApp);

/// <summary>Whether a GitHub App was stored to remove.</summary>
public sealed record GitHubAppRemoved(bool Removed);

/// <summary>
/// Who owns the App being created: an organisation's login, or null for the person creating it. Creating one while an
/// App is stored replaces it, which disconnects every GitHub connection, so that needs <paramref name="ReplaceExisting"/>.
/// </summary>
public sealed record GitHubAppCreationRequest(string? Organisation = null, bool ReplaceExisting = false);

/// <summary>
/// The manifest flow's browser step. A browser signed in to GitHub posts a form with one field, <c>manifest</c>, to
/// <see cref="FormAction"/>; GitHub asks the person to confirm, then sends them back to Connapse, which stores the App.
/// <see cref="SetupPageUrl"/> is the providers page that does the post in one click. Each start is good for an hour,
/// once, and only for the person who started it.
/// </summary>
public sealed record GitHubAppCreation(string FormAction, string Manifest, string SetupPageUrl);

/// <summary>
/// Saves a GitHub App registered by hand. GitHub is asked who the App id and key belong to, and whether a given client
/// secret is the App's, before anything is stored, so a wrong value never replaces a working App.
/// </summary>
[Operation("providers.github.app", Scope = "admin:settings", Risk = RiskTier.Credential, Mcp = McpExposure.Generic)]
public sealed class SetGitHubApp(
    IProviderCredentialStore credentials, ConnapseGitHubApp gitHub, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<GitHubAppSaved, GitHubAppStatus>> RunAsync(
        OperationContext context, GitHubAppRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden, GitHubRules.Refused);

        if (request.AppId is not > 0 || request.AppId is not { } appId)
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Invalid,
                "Enter the App ID: the number at the top of the App's page on GitHub.", "appId");

        // A blank key or secret on the stored App keeps what is stored: rotating one value never asks for the other.
        GitHubAppCredentialMaterial? stored = await credentials.GetGitHubAppMaterialAsync(ct);
        bool sameApp = stored?.App.AppId == appId;
        string? key = GitHubRules.Blank(request.PrivateKeyPem) ?? (sameApp ? stored!.PrivateKeyPem : null);
        string? givenSecret = GitHubRules.Blank(request.ClientSecret);
        string? secret = givenSecret ?? (sameApp ? stored!.ClientSecret : null);

        if (key is null)
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Invalid, "Paste the App's private key.", "privateKeyPem");

        if (stored is not null && !sameApp && !request.ReplaceExisting)
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.ReplaceUnconfirmed,
                $"App ID {appId} is a different App from {stored.App.Slug}. Send replaceExisting true to replace it, or use App ID {stored.App.AppId}.",
                "replaceExisting");

        GitHubAppRegistration app;
        try
        {
            // GitHub answers "who is this App" only for a matching id and key, so this is the check.
            app = await gitHub.VerifyAsync(appId, key, ct);
            if (givenSecret is not null
                && (app.ClientId is not { Length: > 0 } clientId || !await gitHub.ClientSecretMatchesAsync(clientId, givenSecret, ct)))
                return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Refused,
                    $"GitHub did not accept that client secret for {app.Slug}. Generate a new one on the App's page and send it.", "clientSecret");
        }
        catch (GitHubAppException ex) when (ex.StatusCode is 401 or 404)
        {
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Refused,
                "GitHub did not accept that App ID and private key together. Check the App ID, and that the key was generated "
                + $"for that App. GitHub said: {ex.Message}", "privateKeyPem");
        }
        catch (GitHubAppException ex) when (ex is { StatusCode: null, InnerException: System.Security.Cryptography.CryptographicException or ArgumentException })
        {
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Invalid,
                "That private key is not a readable .pem file. Paste the whole file, including the BEGIN and END lines.", "privateKeyPem");
        }
        catch (Exception ex) when (GitHubRules.IsUnavailable(ex, ct))
        {
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.Unavailable, GitHubRules.Unavailable(ex));
        }

        // Asking GitHub takes a moment; a save that landed meanwhile is not overwritten with values read before it.
        GitHubAppCredentialMaterial? current = await credentials.GetGitHubAppMaterialAsync(ct);
        if (current?.App.AppId != stored?.App.AppId || current?.PrivateKeyPem != stored?.PrivateKeyPem || current?.ClientSecret != stored?.ClientSecret)
            return Attempt<GitHubAppSaved, GitHubAppStatus>.Fail(GitHubAppStatus.ReplaceUnconfirmed,
                "The stored GitHub App changed while GitHub was checking these values, so nothing was saved. Read it again and resend.",
                "replaceExisting");

        await credentials.SaveGitHubAppAsync(app, key, secret, GitHubRules.UserId(context), ct);
        gitHub.ClearCache();
        await audit.LogAsync("provider.credential.saved", "provider", GitHubRules.Provider,
            new { app.AppId, app.Slug, app.OwnerLogin, Via = "manual", Replaced = stored is not null && !sameApp }, ct);

        return Attempt<GitHubAppSaved, GitHubAppStatus>.Succeed(new GitHubAppSaved(app, CanSignIn: secret is not null, NewApp: !sameApp));
    }
}

/// <summary>The stored GitHub App, and when it was saved and last worked; both null when none is stored.</summary>
public sealed record GitHubAppView(GitHubAppRegistration? App, ProviderCredentialStatus? Status);

/// <summary>Reads the stored GitHub App. Its private key and client secret are never returned.</summary>
[Operation("providers.github.app.get", Scope = "admin:settings", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetGitHubApp(IProviderCredentialStore credentials, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<GitHubAppView, GitHubAppStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<GitHubAppView, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden, GitHubRules.Refused);

        GitHubAppRegistration? app = await credentials.GetGitHubAppAsync(ct);
        ProviderCredentialStatus? status = app is null ? null : await credentials.GetStatusAsync(GitHubRules.Provider, ct);
        return Attempt<GitHubAppView, GitHubAppStatus>.Succeed(new GitHubAppView(app, status));
    }
}

/// <summary>
/// Removes the stored GitHub App's key and secret. Every GitHub connection and source stops syncing, and linked GitHub
/// accounts no longer unlock private repositories. The App itself stays on GitHub.
/// </summary>
[Operation("providers.github.app.remove", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class RemoveGitHubApp(
    IProviderCredentialStore credentials, ConnapseGitHubApp gitHub, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<GitHubAppRemoved, GitHubAppStatus>> RunAsync(OperationContext context, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<GitHubAppRemoved, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden, GitHubRules.Refused);

        bool removed = await credentials.DeleteAsync(GitHubRules.Provider, ct);
        gitHub.ClearCache();
        if (removed)
            await audit.LogAsync("provider.credential.deleted", "provider", GitHubRules.Provider, cancellationToken: ct);
        return Attempt<GitHubAppRemoved, GitHubAppStatus>.Succeed(new GitHubAppRemoved(removed));
    }
}

/// <summary>
/// Starts creating the GitHub App through GitHub's manifest flow, for the person calling. An organisation owner is
/// checked with GitHub first: a wrong name would create an App that organisation can't install.
/// </summary>
[Operation("providers.github.createApp", Scope = "admin:settings", Risk = RiskTier.Write)]
public sealed class StartGitHubAppCreation(
    GitHubManifestRequests requests, ConnapseGitHubApp gitHub, IProviderCredentialStore credentials, IOperationAuthorizer authorizer)
{
    /// <param name="baseUrl">The address GitHub sends the person back to: the one they reached Connapse on.</param>
    public async Task<Attempt<GitHubAppCreation, GitHubAppStatus>> RunAsync(
        OperationContext context, string baseUrl, GitHubAppCreationRequest request, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin))
            return Attempt<GitHubAppCreation, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden, GitHubRules.Refused);

        if (GitHubRules.UserId(context) is not { } userId)
            return Attempt<GitHubAppCreation, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden,
                "Only a person can create the GitHub App: GitHub sends the browser back to whoever started it.");

        string? owner = GitHubRules.Blank(request.Organisation);
        if (owner is not null)
        {
            if (GitHubAppManifest.FormAction(owner, "check") is null)
                return Attempt<GitHubAppCreation, GitHubAppStatus>.Fail(GitHubAppStatus.Invalid,
                    "That is not a valid GitHub organisation name.", "organisation");

            GitHubAccountKind kind;
            try
            {
                kind = await gitHub.GetAccountKindAsync(owner, ct);
            }
            catch (Exception ex) when (GitHubRules.IsUnavailable(ex, ct))
            {
                return Attempt<GitHubAppCreation, GitHubAppStatus>.Fail(GitHubAppStatus.Unavailable, GitHubRules.Unavailable(ex));
            }

            if (kind != GitHubAccountKind.Organization)
                return Attempt<GitHubAppCreation, GitHubAppStatus>.Fail(GitHubAppStatus.Invalid, kind == GitHubAccountKind.User
                    ? $"{owner} is a personal account, not an organisation. Leave the organisation out to use your own account."
                    : $"GitHub has no organisation called {owner}.", "organisation");
        }

        GitHubAppRegistration? stored = await credentials.GetGitHubAppAsync(ct);
        if (stored is not null && !request.ReplaceExisting)
            return Attempt<GitHubAppCreation, GitHubAppStatus>.Fail(GitHubAppStatus.ReplaceUnconfirmed,
                $"{stored.Slug} is stored, and a new App replaces it. Send replaceExisting true to replace it.", "replaceExisting");

        string home = baseUrl.TrimEnd('/') + "/";
        return Attempt<GitHubAppCreation, GitHubAppStatus>.Succeed(new GitHubAppCreation(
            GitHubAppManifest.FormAction(owner, requests.Start(userId, stored?.AppId))!,
            GitHubAppManifest.Build(home, isPublic: true),
            home + GitHubRules.ProviderPage.TrimStart('/')));
    }
}

/// <summary>
/// Finishes creating the GitHub App when GitHub sends the person back: spends GitHub's one-time code for the App's key
/// and secret, and stores them. The state must name a creation this person started within the hour; anything else — a
/// replayed, planted or expired redirect — is refused before the code is spent.
/// </summary>
[Operation("providers.github.createApp.callback", Scope = "admin:settings", Risk = RiskTier.Credential)]
public sealed class CompleteGitHubAppCreation(
    GitHubManifestRequests pending, ConnapseGitHubApp gitHub, IProviderCredentialStore credentials, IAuditLogger audit,
    IOperationAuthorizer authorizer, ILogger<CompleteGitHubAppCreation> logger)
{
    public async Task<Attempt<GitHubAppRegistration, GitHubAppStatus>> RunAsync(
        OperationContext context, string? code, string? state, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SettingsAdmin) || GitHubRules.UserId(context) is not { } userId)
            return Attempt<GitHubAppRegistration, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden, GitHubRules.Refused);

        if (!pending.TryComplete(state, userId, out long? replacing))
            return Attempt<GitHubAppRegistration, GitHubAppStatus>.Fail(GitHubAppStatus.Expired,
                "That GitHub App creation was started too long ago or by someone else. Start it again.");

        if (string.IsNullOrWhiteSpace(code))
            return Attempt<GitHubAppRegistration, GitHubAppStatus>.Fail(GitHubAppStatus.NoCode,
                "GitHub did not send back a code, so no App was stored. Start again.");

        // Only the App stored when the person started, if any, may be replaced: an App stored since is kept, and the
        // code is left unspent.
        if (await credentials.GetGitHubAppAsync(ct) is { } stored && stored.AppId != replacing)
            return Attempt<GitHubAppRegistration, GitHubAppStatus>.Fail(GitHubAppStatus.ReplaceUnconfirmed,
                $"{stored.Slug} was stored after this creation started, so the new App wasn't stored over it.", "replaceExisting");

        GitHubAppManifestResult created;
        try
        {
            created = await gitHub.ConvertManifestAsync(code, ct);
            await credentials.SaveGitHubAppAsync(created.App, created.PrivateKeyPem, created.ClientSecret, userId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged, not returned: the code isn't logged either — until it is spent it is a credential for the new App.
            logger.LogError(ex, "Creating the GitHub App from its manifest failed");
            return Attempt<GitHubAppRegistration, GitHubAppStatus>.Fail(GitHubAppStatus.Unavailable,
                "GitHub didn't hand over the new App, so nothing was stored. Start again.");
        }

        // Stored and in use from here: a failure below must not tell the person to start again.
        gitHub.ClearCache();
        try
        {
            await audit.LogAsync("provider.credential.saved", "provider", GitHubRules.Provider,
                new { created.App.AppId, created.App.Slug, created.App.OwnerLogin, Via = "manifest", Replaced = replacing is not null }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "The GitHub App {AppId} was stored, but its audit entry was not", created.App.AppId);
        }

        return Attempt<GitHubAppRegistration, GitHubAppStatus>.Succeed(created.App);
    }
}

/// <summary>
/// Records that GitHub installed the App (or that an organisation's owners were asked to), and says where the person
/// goes next: installing is a connection's business, so it is always the Connections page. The installation id is only
/// a hint there; that page selects it only if the App's own list includes it.
/// </summary>
[Operation("providers.github.installed", Scope = "admin:settings", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class RecordGitHubAppInstalled(IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<string, GitHubAppStatus>> RunAsync(
        OperationContext context, long? installationId, string? setupAction, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<string, GitHubAppStatus>.Fail(GitHubAppStatus.Forbidden, GitHubRules.Refused);

        // Recorded so each setup step (App stored, installed, connection, source, account linked) has a time, and how
        // long setup takes and where it stops can be measured.
        await audit.LogAsync("provider.github.installed", "provider", GitHubRules.Provider,
            new { InstallationId = installationId, SetupAction = setupAction is "install" or "update" or "request" ? setupAction : "other" }, ct);

        if (setupAction == "request")
            return Attempt<string, GitHubAppStatus>.Succeed("/connections?github_install=requested");

        // An existing installation whose repositories were changed on GitHub: its connection already exists, so this
        // is news, not a new connection to create.
        if (setupAction == "update" && installationId is > 0)
            return Attempt<string, GitHubAppStatus>.Succeed($"/connections?github_updated={installationId}");

        return Attempt<string, GitHubAppStatus>.Succeed(installationId is > 0
            ? $"/connections?github_installation={installationId}"
            : "/connections?new=github");
    }
}

/// <summary>The GitHub App operations the providers page uses, in one place.</summary>
public sealed class GitHubAppOperations(SetGitHubApp app, RemoveGitHubApp remove, StartGitHubAppCreation create, GetGitHubApp read)
{
    public GetGitHubApp Read { get; } = read;

    public SetGitHubApp App { get; } = app;

    public RemoveGitHubApp Remove { get; } = remove;

    public StartGitHubAppCreation Create { get; } = create;
}

/// <summary>The rules the GitHub App operations share.</summary>
internal static class GitHubRules
{
    public const string Provider = "github";

    public const string Refused = AwsRules.Refused;

    /// <summary>Where the administrator sets the App up.</summary>
    public const string ProviderPage = "/admin/providers/github";

    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static Guid? UserId(OperationContext context) =>
        Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : null;

    /// <summary>GitHub couldn't be asked, or answered in a way that can't be used: anything but the caller giving up.</summary>
    public static bool IsUnavailable(Exception ex, CancellationToken ct) =>
        ex is GitHubAppException or HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    public static string Unavailable(Exception ex) => ex is GitHubAppException { StatusCode: null } app
        ? $"Nothing was changed: {app.Message}"
        : "GitHub could not be reached or refused the request, so nothing was changed. Check that this server can reach api.github.com, then try again.";
}
