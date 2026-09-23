using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.Connectors.GitHub;

/// <summary>One place the App is installed — an organisation or a personal account.</summary>
/// <param name="Id">The installation id tokens are minted for.</param>
/// <param name="AccountLogin">The organisation or user it is installed on.</param>
/// <param name="AccountType"><c>Organization</c> or <c>User</c>.</param>
/// <param name="RepositorySelection"><c>all</c> or <c>selected</c>.</param>
/// <param name="HtmlUrl">Where its settings live on GitHub.</param>
public sealed record GitHubAppInstallation(
    long Id, string AccountLogin, string AccountType, string RepositorySelection, string HtmlUrl);

/// <summary>A short-lived installation access token.</summary>
public sealed record GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>What GitHub hands back when a manifest is converted into an App: everything, once.</summary>
public sealed record GitHubAppManifestResult(GitHubAppRegistration App, string PrivateKeyPem, string? ClientSecret);

/// <summary>What a GitHub login names.</summary>
public enum GitHubAccountKind { None, User, Organization }

/// <summary>GitHub refused a call made as the App, or answered in a way that cannot be used.</summary>
public sealed class GitHubAppException(string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// The GitHub App Connapse acts as: signs the App's JWT, lists its installations, and mints the
/// hour-long installation tokens every GitHub read uses.
/// <para>
/// A singleton, like <c>ConnapseAwsCredentials</c>, because the tokens it caches are what keeps a
/// sync from minting a new one per request — GitHub counts those. The App's key lives in the scoped
/// provider credential store, so it is read through a scope and kept for a few minutes;
/// <see cref="ClearCache"/> drops everything after a save or reset.
/// </para>
/// </summary>
public sealed class ConnapseGitHubApp(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    ILogger<ConnapseGitHubApp> logger,
    TimeProvider? clock = null)
{
    /// <summary>How long a read of the stored App is trusted before the store is asked again.</summary>
    private static readonly TimeSpan MaterialLifetime = TimeSpan.FromMinutes(5);

    /// <summary>A token this close to expiry is replaced rather than handed out.</summary>
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, GitHubInstallationToken> _tokens = new();
    private readonly SemaphoreSlim _materialGate = new(1, 1);
    private (GitHubAppCredentialMaterial? Material, DateTimeOffset LoadedAt)? _material;

    /// <summary>
    /// Bumped by <see cref="ClearCache"/>. A load or mint that began before a clear does not publish
    /// what it read, so a removed or replaced App cannot be put back by a request already in flight.
    /// </summary>
    private long _generation;
    private readonly Lock _cacheLock = new();

    /// <summary>The REST API every call goes to. Only tests change it.</summary>
    internal string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>Forgets the stored App and every cached token. Call after the App is saved or removed.</summary>
    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _generation++;
            _material = null;
            _tokens.Clear();
        }
    }

    private long Generation
    {
        get { lock (_cacheLock) return _generation; }
    }

    /// <summary>True when an App is stored. Reads the store, not GitHub.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        await LoadAsync(ct) is not null;

    // ── Calls made as the App ──────────────────────────────────────────────

    /// <summary>
    /// Asks GitHub who an App id and key belong to — the check a hand-entered App passes before it
    /// is saved. Nothing is stored or cached.
    /// </summary>
    public async Task<GitHubAppRegistration> VerifyAsync(long appId, string privateKeyPem, CancellationToken ct = default)
    {
        string jwt = CreateJwt(appId, privateKeyPem, _clock.GetUtcNow());
        using var response = await SendAsync(HttpMethod.Get, "app", Bearer(jwt), content: null, ct);
        var app = await ReadAsync<AppPayload>(response, "reading the App", ct);

        return new GitHubAppRegistration(app.Id, app.Slug, app.ClientId, app.Owner?.Login ?? "", app.HtmlUrl);
    }

    public async Task<IReadOnlyList<GitHubAppInstallation>> ListInstallationsAsync(CancellationToken ct = default)
    {
        var material = await RequireAsync(ct);
        var installations = new List<GitHubAppInstallation>();

        // Paged, though an App with over a hundred installations would be unusual for one Connapse.
        for (int page = 1; ; page++)
        {
            string jwt = CreateJwt(material.App.AppId, material.PrivateKeyPem, _clock.GetUtcNow());
            using var response = await SendAsync(
                HttpMethod.Get, $"app/installations?per_page=100&page={page}", Bearer(jwt), content: null, ct);
            var batch = await ReadAsync<List<InstallationPayload>>(response, "listing installations", ct);

            installations.AddRange(batch.Select(i => new GitHubAppInstallation(
                i.Id, i.Account?.Login ?? "", i.Account?.Type ?? "", i.RepositorySelection ?? "", i.HtmlUrl ?? "")));

            if (batch.Count < 100) break;
        }

        return installations;
    }

    /// <summary>
    /// A token for one installation, reused until five minutes before it expires. Tokens are minted
    /// with the App's full permissions for every repository the installation covers.
    /// </summary>
    public async Task<GitHubInstallationToken> GetInstallationTokenAsync(long installationId, CancellationToken ct = default)
    {
        if (_tokens.TryGetValue(installationId, out var cached)
            && cached.ExpiresAt - _clock.GetUtcNow() > TokenRefreshMargin)
            return cached;

        long generation = Generation;
        var material = await RequireAsync(ct);
        string jwt = CreateJwt(material.App.AppId, material.PrivateKeyPem, _clock.GetUtcNow());

        using var response = await SendAsync(
            HttpMethod.Post, $"app/installations/{installationId}/access_tokens", Bearer(jwt), content: null, ct);
        var payload = await ReadAsync<TokenPayload>(response, $"minting a token for installation {installationId}", ct);

        var token = new GitHubInstallationToken(payload.Token, payload.ExpiresAt);
        lock (_cacheLock)
        {
            if (_generation == generation)
                _tokens[installationId] = token;
        }

        return token;
    }

    /// <summary>Forgets one installation's token, after GitHub refused it.</summary>
    public void Forget(long installationId) => _tokens.TryRemove(installationId, out _);

    /// <summary>
    /// Trades the one-time code GitHub sent back from the manifest flow for the new App — its id,
    /// key, and client secret, which GitHub returns this once and never again.
    /// </summary>
    public async Task<GitHubAppManifestResult> ConvertManifestAsync(string code, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        using var response = await SendAsync(
            HttpMethod.Post, $"app-manifests/{Uri.EscapeDataString(code)}/conversions", auth: null, content: null, ct);
        var app = await ReadAsync<ManifestPayload>(response, "creating the App from its manifest", ct);

        if (string.IsNullOrWhiteSpace(app.Pem))
            throw new GitHubAppException("GitHub created the App but returned no private key.");

        return new GitHubAppManifestResult(
            new GitHubAppRegistration(app.Id, app.Slug, app.ClientId, app.Owner?.Login ?? "", app.HtmlUrl),
            app.Pem,
            app.ClientSecret);
    }

    /// <summary>
    /// Whether <paramref name="login"/> is a GitHub organisation, a personal account, or nobody —
    /// asked anonymously, since it is checked before any App exists.
    /// </summary>
    public async Task<GitHubAccountKind> GetAccountKindAsync(string login, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        using var response = await SendAsync(
            HttpMethod.Get, $"users/{Uri.EscapeDataString(login.Trim())}", auth: null, content: null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return GitHubAccountKind.None;

        var account = await ReadAsync<Account>(response, "looking up the account", ct);
        return string.Equals(account.Type, "Organization", StringComparison.OrdinalIgnoreCase)
            ? GitHubAccountKind.Organization
            : GitHubAccountKind.User;
    }

    // ── JWT ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The App's own credential: an RS256 JWT, issued a minute in the past to absorb clock drift and
    /// valid for nine of GitHub's ten-minute maximum. Built by hand — the signature is one
    /// <see cref="RSA.SignData(byte[], HashAlgorithmName, RSASignaturePadding)"/> call, not worth a
    /// token library in this project.
    /// </summary>
    internal static string CreateJwt(long appId, string privateKeyPem, DateTimeOffset now)
    {
        string header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8);
        string payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.AddMinutes(9).ToUnixTimeSeconds(),
            iss = appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })));

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(privateKeyPem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new GitHubAppException("The GitHub App private key is not a readable RSA key in PEM form.", inner: ex);
        }

        byte[] signature = rsa.SignData(
            Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return header + "." + payload + "." + Base64Url(signature);
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Plumbing ───────────────────────────────────────────────────────────

    private async Task<GitHubAppCredentialMaterial?> LoadAsync(CancellationToken ct)
    {
        if (_material is { } cached && _clock.GetUtcNow() - cached.LoadedAt < MaterialLifetime)
            return cached.Material;

        await _materialGate.WaitAsync(ct);
        try
        {
            if (_material is { } again && _clock.GetUtcNow() - again.LoadedAt < MaterialLifetime)
                return again.Material;

            long generation = Generation;
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IProviderCredentialStore>();
            var material = await store.GetGitHubAppMaterialAsync(ct);

            lock (_cacheLock)
            {
                if (_generation == generation)
                    _material = (material, _clock.GetUtcNow());
            }

            return material;
        }
        finally
        {
            _materialGate.Release();
        }
    }

    private async Task<GitHubAppCredentialMaterial> RequireAsync(CancellationToken ct) =>
        await LoadAsync(ct)
        ?? throw new GitHubAppException("No GitHub App is set up. Create one on the Providers page.");

    private static AuthenticationHeaderValue Bearer(string value) => new("Bearer", value);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, AuthenticationHeaderValue? auth, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiBaseUrl.TrimEnd('/') + "/" + path) { Content = content };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Connapse", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = auth;

        var http = httpClients.CreateClient(ConnectorFactory.GitHubHttpClientName);
        return await http.SendAsync(request, ct);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            string detail = TryMessage(body) ?? response.ReasonPhrase ?? "no detail";
            logger.LogWarning("GitHub refused {What}: {Status} {Detail}",
                what, (int)response.StatusCode, Core.Utilities.LogSanitizer.Sanitize(detail));

            throw new GitHubAppException(
                $"GitHub refused {what} ({(int)response.StatusCode}): {detail}", (int)response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new GitHubAppException($"GitHub returned an empty answer when {what}.");
    }

    private static string? TryMessage(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Account(string Login, string? Type);

    private sealed record AppPayload(long Id, string Slug, string? ClientId, Account? Owner, string HtmlUrl);

    private sealed record ManifestPayload(
        long Id, string Slug, string? ClientId, string? ClientSecret, string? Pem, Account? Owner, string HtmlUrl);

    private sealed record InstallationPayload(long Id, Account? Account, string? RepositorySelection, string? HtmlUrl);

    private sealed record TokenPayload(string Token, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
