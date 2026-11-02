using System.Text.Json;
using Connapse.Application.Connections;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Settings;

namespace Connapse.Web.Services;

/// <summary>How checking a custom source server went: why it was refused, or how many scopes it offers.</summary>
public sealed record SourceServerCheck(SettingsCheck? Refused, int Scopes);

/// <summary>
/// The Connections page's save checks, which connections.create and connections.update call: the form's rules,
/// a GitHub installation the App still has, an S3 bucket's region, a custom source server that accepts its
/// token, and an SFTP host as this deployment can reach it.
/// </summary>
public sealed class ConnectionSettingsRules(
    IConnectionStore connections,
    ICustomProviderStore customProviders,
    CustomSourceSetup customSetup,
    IGitHubAppInstallations gitHubApp,
    IS3Discovery s3Discovery,
    IContainerHostResolver hostResolver,
    IAuditLogger audit) : IConnectionSettingsRules
{
    public async Task<SettingsCheck> CheckAsync(
        string name, ConnectionProvider provider, ConnectionSettings settings, Connection? current, CancellationToken ct = default)
    {
        if (!IsObjectOrBlank(settings.ConfigJson))
            return SettingsCheck.Refuse("config must be a JSON object of the provider's settings.", "config");

        // A secret given without settings replaces the secret and keeps the stored settings.
        string? configJson = settings.ConfigJson ?? current?.ConfigJson;
        bool isNew = current is null;
        var form = ConnectionForm.FromConnection(new Connection(
            current?.Id ?? Guid.Empty, name, provider, configJson, null, DateTime.UtcNow, DateTime.UtcNow));
        form.PrivateKey = settings.PrivateKey;
        form.Passphrase = settings.Passphrase;
        form.CustomToken = settings.Token;

        // The connector records an SFTP server's host key on first connect and checks it after; a change of
        // settings keeps the pinned key, and only forgetting it re-arms trust on first use.
        if (provider == ConnectionProvider.Sftp && current is not null)
        {
            form.HostKeyFingerprint = ConnectionForm.FromConnection(current).HostKeyFingerprint;
            form.ForgetHostKey = false;
        }

        if (form.Validate(isNew) is { } problem)
            return SettingsCheck.Refuse(problem, "config");

        string? note = null;
        bool verified = false;
        switch (provider)
        {
            case ConnectionProvider.Custom when !form.CustomContentOnly:
                if (await customProviders.GetAsync(form.CustomProviderId!.Value, ct) is null)
                    return SettingsCheck.Refuse("No custom provider has that id.", "config");
                break;

            case ConnectionProvider.Custom:
                if ((await CheckSourceServerAsync(form, current, ct)).Refused is { } refused)
                    return refused;
                verified = true;
                break;

            case ConnectionProvider.GitHub:
                long? before = current is null ? null : ConnectionForm.FromConnection(current).GitHubInstallationId;
                if ((isNew || form.GitHubInstallationId != before) && await CheckInstallationAsync(form, ct) is { } gitHubProblem)
                    return gitHubProblem;
                break;

            case ConnectionProvider.S3 when string.IsNullOrWhiteSpace(form.Region):
                // ToConfigJson would store "us-east-1" for a blank region, which is a guess; an S3 client
                // pointed at the wrong region fails as a missing bucket or a permissions problem.
                if (form.FirstAllowedLocation()?.Split('/')[0] is { Length: > 0 } bucket)
                    form.Region = await RegionOfAsync(bucket, form.RoleArn, ct);
                if (string.IsNullOrWhiteSpace(form.Region))
                    return SettingsCheck.Refuse("Connapse could not work out which region this bucket is in. Enter one, or "
                        + "choose the bucket from the list so it can be looked up.", "region");
                break;

            case ConnectionProvider.Sftp:
                // Stored as the address Connapse will dial, so a test and a sync reach the same server.
                var resolved = hostResolver.Resolve(form.Host);
                if (resolved.Rewritten)
                {
                    form.Host = resolved.Host;
                    note = resolved.Reason;
                }
                break;
        }

        return new SettingsCheck(form.ToConfigJson(), form.ToSecretJson(), note) { Verified = verified };
    }

    /// <summary>
    /// Checks a content-only custom connection's source server with the address and token about to be saved (the
    /// stored token when none was given), and keeps the address as normalised. Refused null when it may be saved;
    /// connections.test runs the same check.
    /// </summary>
    public async Task<SourceServerCheck> CheckSourceServerAsync(ConnectionForm form, Connection? current, CancellationToken ct)
    {
        string? stored = CustomConnectionConfig.BaseUrlOf(current?.ConfigJson);
        bool typedToken = !string.IsNullOrWhiteSpace(form.CustomToken);

        // The stored token is only ever sent to the stored address: a new address needs its token given, or
        // anyone who can edit the connection could have the stored token sent to a server of their choosing.
        if (!typedToken && current is not null && !CustomSourceEndpoint.SameAddress(form.CustomBaseUrl, stored))
        {
            await AuditCheckAsync(current, passed: false, "token", checkedUrl: null, ct);
            return new SourceServerCheck(
                SettingsCheck.Refuse($"Enter the token for this address. The saved token is only sent to {stored}.", "secret"), 0);
        }

        string? token = typedToken ? form.CustomToken : null;
        if (!typedToken && current is { HasSecret: true })
        {
            // Read at the version whose address was just compared, so a save in between can't pair it with another.
            try
            {
                token = await connections.GetSecretAtAsync(current.Id, current.UpdatedAt, ct);
            }
            catch (ConnectionChangedException)
            {
                return new SourceServerCheck(SettingsCheck.Refuse(ConnectionMessages.ChangedWhileChecked, refusal: SettingsRefusal.Changed), 0);
            }
        }

        CustomSourceProbe probe = await customSetup.ProbeAsync(form.CustomBaseUrl, token, ct);
        if (probe.Error is not null)
        {
            await AuditCheckAsync(current, passed: false, probe.Field == "secret" ? "token" : "endpoint", probe.BaseUrl, ct);
            return new SourceServerCheck(SettingsCheck.Refuse(probe.Error, probe.Field,
                probe.Unreachable ? SettingsRefusal.Unavailable : SettingsRefusal.Invalid), 0);
        }

        // A content-only connection's address is what its sources read, so it is fixed while any exist.
        if (current is { SourceCount: > 0 } && !string.Equals(probe.BaseUrl, stored, StringComparison.Ordinal))
            return new SourceServerCheck(SettingsCheck.Refuse("Sources read this address, so it cannot change.", "baseUrl"), 0);

        form.CustomBaseUrl = probe.BaseUrl;
        await AuditCheckAsync(current, passed: true, "scopes", probe.BaseUrl, ct);
        return new SourceServerCheck(null, probe.Scopes.Count);
    }

    /// <summary>
    /// Audits a custom connection check with the step it reached, so setup drop-off can be measured. The address is
    /// only ever one that passed normalisation: the raw text given could hold a credential or a pasted token.
    /// </summary>
    private Task AuditCheckAsync(Connection? current, bool passed, string step, string? checkedUrl, CancellationToken ct) =>
        audit.LogAsync(passed ? "connection.test_verified" : "connection.test_step_failed", "connection",
            current?.Id.ToString(), new { Provider = nameof(ConnectionProvider.Custom), BaseUrl = checkedUrl, Step = step }, ct);

    /// <summary>
    /// Why a GitHub connection cannot be saved: no provider App, or an installation the App no longer has. Sets the
    /// account shown beside the connection from the installation, not from what the caller said.
    /// </summary>
    private async Task<SettingsCheck?> CheckInstallationAsync(ConnectionForm form, CancellationToken ct)
    {
        try
        {
            if (!await gitHubApp.IsConfiguredAsync(ct))
                return SettingsCheck.Refuse("Set up the GitHub App on the Providers page first.", "installationId");

            var installation = (await gitHubApp.ListInstallationsAsync(ct)).FirstOrDefault(i => i.Id == form.GitHubInstallationId);
            if (installation is null)
                return SettingsCheck.Refuse("The GitHub App is no longer installed there. Choose another installation.", "installationId");
            form.GitHubAccount = installation.AccountLogin;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return SettingsCheck.Refuse("Could not check the installation. " + GitHubMessages.Describe(ex, form.GitHubAccount),
                "installationId", SettingsRefusal.Unavailable);
        }
    }

    /// <summary>Where AWS says a bucket lives, or null when it could not be asked.</summary>
    private async Task<string?> RegionOfAsync(string bucket, string? roleArn, CancellationToken ct)
    {
        try
        {
            var probe = await s3Discovery.GetBucketRegionAsync(bucket, string.IsNullOrWhiteSpace(roleArn) ? null : roleArn.Trim(), ct);
            return probe is { Succeeded: true, Value: { Length: > 0 } region } ? region : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A region nobody could look up is one somebody can enter.
            return null;
        }
    }

    private static bool IsObjectOrBlank(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
