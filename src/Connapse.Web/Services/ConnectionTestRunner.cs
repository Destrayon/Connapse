using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Settings;

namespace Connapse.Web.Services;

/// <summary>
/// The Connections page's Test button, which connections.test calls: each provider's live check, with the settings
/// about to be saved or the stored ones. A stored secret is only used with the stored settings it was saved for: an
/// SFTP key with its own server and user, a custom source server's token with its own address.
/// </summary>
public sealed class ConnectionTestRunner(
    IConnectionStore connections,
    ConnectionSettingsRules rules,
    CustomSourceSetup customSetup,
    ConnapseGitHubApp gitHubApp,
    SftpConnectionTester sftpTester,
    AzureBlobConnectionTester azureTester,
    S3ConnectionTester s3Tester,
    IContainerHostResolver hostResolver,
    AtlassianSiteService atlassianSites,
    IAuditLogger audit) : IConnectionTestRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ConnectionTestRun> TestAsync(ConnectionTestSubject subject, CancellationToken ct = default)
    {
        if (subject.Provider == ConnectionProvider.Atlassian)
            return subject.Stored is { } site
                ? await atlassianSites.TestStoredAsync(site.Id, ct)
                : ConnectionTestRun.Refuse(ConnectionMessages.AtlassianSiteOnly, "provider");

        string? configJson = subject.Settings?.ConfigJson ?? subject.Stored?.ConfigJson;
        var form = ConnectionForm.FromConnection(new Connection(
            subject.Stored?.Id ?? Guid.Empty, string.IsNullOrWhiteSpace(subject.Name) ? "unsaved" : subject.Name, subject.Provider, configJson, null, DateTime.UtcNow, DateTime.UtcNow));
        form.PrivateKey = subject.Settings?.PrivateKey;
        form.Passphrase = subject.Settings?.Passphrase;
        form.CustomToken = subject.Settings?.Token;

        try
        {
            return subject.Provider switch
            {
                ConnectionProvider.Custom => await TestCustomAsync(form, subject.Stored, ct),
                ConnectionProvider.GitHub => await TestGitHubAsync(form, ct),
                ConnectionProvider.Sftp => await TestSftpAsync(form, subject.Stored, ct),
                ConnectionProvider.AzureBlob => await TestAzureAsync(form, subject.ProbeTarget, ct),
                ConnectionProvider.S3 => await TestS3Async(form, subject.ProbeTarget, ct),
                _ => ConnectionTestRun.Refuse(
                    $"A {subject.Provider} connection has nothing to test: its sources read it when they sync.", "provider"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ConnectionTestRun(ConnectionTestResult.CreateFailure($"Test failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// A content-only connection against the address and token given (the stored token, for the stored address
    /// only), one with a provider against the provider's source server.
    /// </summary>
    private async Task<ConnectionTestRun> TestCustomAsync(ConnectionForm form, Connection? stored, CancellationToken ct)
    {
        if (!form.CustomContentOnly)
        {
            if (form.CustomProviderId is not { } providerId)
                return ConnectionTestRun.Refuse("Choose a custom provider, or No provider (content only).", "provider");

            Connection draft = new(stored?.Id ?? Guid.Empty, form.Name, ConnectionProvider.Custom,
                CustomConnectionConfig.ToJson(providerId), null, DateTime.UtcNow, DateTime.UtcNow);
            CustomSourceProbe probe = await customSetup.ProbeConnectionAsync(draft, ct);
            await audit.LogAsync(probe.Error is null ? "connection.test_verified" : "connection.test_step_failed", "connection",
                stored?.Id.ToString(),
                new { Provider = nameof(ConnectionProvider.Custom), BaseUrl = probe.BaseUrl, Step = probe.Error is null ? "scopes" : "provider" }, ct);
            return new ConnectionTestRun(probe.Error is null
                ? ConnectionTestResult.CreateSuccess(
                    $"The provider's source server is reachable, accepts its token, and offers {Scopes(probe.Scopes.Count)}.")
                : ConnectionTestResult.CreateFailure(probe.Error));
        }

        if (string.IsNullOrWhiteSpace(form.CustomBaseUrl))
            return ConnectionTestRun.Refuse("Enter the source server's base URL.", "baseUrl");
        if (stored is null && string.IsNullOrWhiteSpace(form.CustomToken))
            return ConnectionTestRun.Refuse("Enter the token the source server expects.", "secret");

        bool storedToken = string.IsNullOrWhiteSpace(form.CustomToken);
        SourceServerCheck check = await rules.CheckSourceServerAsync(form, stored, ct);
        if (check.Refused is { } refused)
            return ConnectionTestRun.Refuse(refused.Error!, refused.Field, refused.Refusal switch
            {
                SettingsRefusal.Unavailable => ConnectionStatus.Unavailable,
                SettingsRefusal.Changed => ConnectionStatus.Conflict,
                _ => ConnectionStatus.Invalid,
            });

        // The stored token, at the stored address, is the connection as its sources read it.
        ConnectionHealth? health = stored is not null && storedToken
            && CustomSourceEndpoint.SameAddress(form.CustomBaseUrl, CustomConnectionConfig.BaseUrlOf(stored.ConfigJson))
            ? new ConnectionHealth(DateTime.UtcNow, Succeeded: true, Step: null, "Reachable, token accepted.", Origin: "test")
            : null;
        return new ConnectionTestRun(ConnectionTestResult.CreateSuccess(
            $"Reachable, token accepted, and it offers {Scopes(check.Scopes)}. Nothing was saved."), health);
    }

    /// <summary>
    /// Each layer in turn, so a failure says which: the App's key signs in, the installation still exists (a token
    /// is minted for it), and it covers repositories.
    /// </summary>
    private async Task<ConnectionTestRun> TestGitHubAsync(ConnectionForm form, CancellationToken ct)
    {
        if (form.GitHubInstallationId is not { } installationId)
            return ConnectionTestRun.Refuse("Choose an installation to test.", "installationId");

        string account = form.GitHubAccount ?? "the chosen account";
        GitHubInstallationRepositories covered;
        try
        {
            covered = await gitHubApp.ListInstallationRepositoriesAsync(installationId, max: 1, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ConnectionTestRun(ConnectionTestResult.CreateFailure(GitHubMessages.Describe(ex, account),
                new Dictionary<string, object> { ["error"] = ex.Message }));
        }

        GitHubAppInstallation? installation = null;
        try
        {
            installation = (await gitHubApp.ListInstallationsAsync(ct)).FirstOrDefault(i => i.Id == installationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Only the wording and the link below need it; the installation itself just answered.
        }

        if (covered.TotalCount == 0)
        {
            Dictionary<string, object> details = new() { ["warning"] = true };
            if (installation?.HtmlUrl is { Length: > 0 } url)
                details["warningUrl"] = url;
            return new ConnectionTestRun(ConnectionTestResult.CreateSuccess(
                $"Signed in as the App, and the {account} installation is active, but it covers no repositories yet, so there is "
                + "nothing to add as a source. Choose repositories on GitHub.", details));
        }

        return new ConnectionTestRun(ConnectionTestResult.CreateSuccess(
            $"Signed in as the App. The {account} installation is active and covers {covered.TotalCount} "
            + (covered.TotalCount == 1 ? "repository" : "repositories")
            + (installation?.RepositorySelection == "all" ? " (all repositories)." : " (selected repositories).")));
    }

    private async Task<ConnectionTestRun> TestSftpAsync(ConnectionForm form, Connection? stored, CancellationToken ct)
    {
        if (form.Validate(isNew: false) is { } problem)
            return ConnectionTestRun.Refuse(problem, "config");

        // What Connapse will actually dial, which is not always what was given. See IContainerHostResolver:
        // loopback means the container, not the operator's machine.
        var resolved = hostResolver.Resolve(form.Host);
        int port = int.TryParse(form.Port, out int p) ? p : 22;
        string username = form.Username?.Trim() ?? "";

        // A key given wins, so a new one can be checked before it is saved. Otherwise the stored one is used, with
        // the server and user it was saved for and no other: anyone who can test a connection could otherwise have
        // its key presented to a server of their choosing.
        string? key = form.PrivateKey?.Trim();
        string? passphrase = NullIfBlank(form.Passphrase);
        bool usedStoredKey = string.IsNullOrWhiteSpace(key);
        if (usedStoredKey)
        {
            if (stored is null)
                return ConnectionTestRun.Refuse("Paste the private key to test this connection.", "privateKey");

            var saved = ConnectionForm.FromConnection(stored);
            int savedPort = int.TryParse(saved.Port, out int sp) ? sp : 22;
            if (!string.Equals(resolved.Host, hostResolver.Resolve(saved.Host).Host, StringComparison.OrdinalIgnoreCase)
                || port != savedPort
                || !string.Equals(username, saved.Username?.Trim() ?? "", StringComparison.Ordinal))
                return ConnectionTestRun.Refuse(
                    $"Paste the private key for this server. The saved key is only used with {saved.Username}@{saved.Host}:{savedPort}.",
                    "privateKey");

            // Read at the version whose server and user were just compared, so a save in between can't swap either.
            string? secret;
            try
            {
                secret = await connections.GetSecretAtAsync(stored.Id, stored.UpdatedAt, ct);
            }
            catch (ConnectionChangedException)
            {
                return ConnectionTestRun.Refuse(ConnectionMessages.ChangedWhileChecked, refusal: ConnectionStatus.Conflict);
            }
            if (string.IsNullOrWhiteSpace(secret))
                return ConnectionTestRun.Refuse("This connection has no stored key. Paste one to test.", "privateKey");
            var credential = SftpCredential.TryParse(secret);
            if (credential is null || string.IsNullOrWhiteSpace(credential.PrivateKey))
                return ConnectionTestRun.Refuse("The stored key could not be read. Paste a key to replace it.", "privateKey");

            key = credential.PrivateKey;
            // A passphrase given now is preferred: the operator may be correcting one that is wrong.
            passphrase ??= credential.Passphrase;
        }

        var result = await sftpTester.TestConnectionAsync(new SftpConnectionTestSettings
        {
            Host = resolved.Host,
            Port = port,
            Username = username,
            AllowedRoot = form.AllowedRoot?.Trim() ?? "",
            PrivateKey = key!,
            Passphrase = passphrase,

            // Honoured unless it was forgotten, so a test reports a changed host key the same way a sync would.
            PinnedHostKeyFingerprint = form.ForgetHostKey ? null : NullIfBlank(form.HostKeyFingerprint),
        }, Timeout, ct);

        // Which key was used, always: two keys can be in play and they can disagree. The rewrite is reported on
        // success as well, since a test that passes against an address other than the one given is easy to misread.
        string usedKey = usedStoredKey ? "Using the stored key." : "Using the key given.";
        string rewrite = resolved.Rewritten ? $" ({resolved.Reason})" : "";
        return new ConnectionTestRun(result with { Message = $"{usedKey} {result.Message}{rewrite}" });
    }

    private async Task<ConnectionTestRun> TestAzureAsync(ConnectionForm form, string? probeTarget, CancellationToken ct)
    {
        if (form.Validate(isNew: false) is { } problem)
            return ConnectionTestRun.Refuse(problem, "config");

        // Unlike a bucket name, "$root" is not a safe default: Azure storage accounts do not auto-create a root
        // container, so probing it when nobody has chosen one would report a valid account as unreachable.
        if (form.ResolveProbeTarget(probeTarget) is not { } target)
            return ConnectionTestRun.Refuse(
                "Enter a container name (or configure an allowed location) to test this Azure connection.", "probeTarget");

        return new ConnectionTestRun(await azureTester.TestConnectionAsync(new AzureBlobConnectorConfig
        {
            AccountName = form.StorageAccountName?.Trim() ?? "",
            BlobEndpoint = NullIfBlank(form.BlobEndpoint),
            ContainerName = target.Container,
            Prefix = target.Prefix,
        }, Timeout, ct));
    }

    private async Task<ConnectionTestRun> TestS3Async(ConnectionForm form, string? probeTarget, CancellationToken ct)
    {
        if ((NullIfBlank(probeTarget) ?? form.FirstAllowedLocation()) is not { } target)
            return ConnectionTestRun.Refuse("Enter a bucket to test against.", "probeTarget");

        var (bucket, prefix) = ConnectionForm.SplitLocation(target);
        return new ConnectionTestRun(await s3Tester.TestConnectionAsync(new S3ConnectorConfig
        {
            Region = NullIfBlank(form.Region) ?? "us-east-1",
            RoleArn = NullIfBlank(form.RoleArn),
            BucketName = bucket,
            Prefix = prefix,
        }, Timeout, ct));
    }

    private static string Scopes(int count) => $"{count} scope{(count == 1 ? "" : "s")}";

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
