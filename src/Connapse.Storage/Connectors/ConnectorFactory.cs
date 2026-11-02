using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Connectors;

/// <summary>
/// Creates IConnector instances from ContainerEntity configuration.
/// </summary>
public class ConnectorFactory(
    IOptionsMonitor<SourceSecuritySettings> sourceSecurity,
    IOptionsMonitor<GitHubSourceSettings> gitHubSettings,
    ISshHostKeyStore hostKeyStore,
    CloudScope.ConnapseAwsCredentials awsCredentials,
    CloudScope.ConnapseAzureCredentials azureCredentials,
    IHttpClientFactory httpClientFactory,
    ILogger<ConnectorFactory> logger,
    GitHub.GitHubCredentialPool? gitHubPool = null,
    Atlassian.AtlassianTokenSource? atlassianTokens = null,
    IOptionsMonitor<AtlassianSourceSettings>? atlassianSettings = null,
    ILoggerFactory? loggerFactory = null,
    IFileTypeValidator? fileTypes = null,
    IOptionsMonitor<CustomSourceSettings>? customSourceSettings = null) : IConnectorFactory
{
    /// <summary>The named client GitHub sources and the GitHub App read the REST API through.</summary>
    public const string GitHubHttpClientName = "GitHub";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Scopes already warned about running without an allowlist.
    /// <para>
    /// A connector is built on every sync cycle, so warning unconditionally would emit one
    /// line per source every five minutes — burying the very message an operator needs to
    /// act on before these become deny-by-default. Keyed on the scope as well as the
    /// connection, so changing a root or bucket warns again rather than staying silent.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _warnedUnrestricted = [];

    public IConnector Create(Source source, Connection connection, string? secret = null)
    {
        if (source.ConnectionId != connection.Id)
            throw new ArgumentException(
                $"Connection '{connection.Id}' does not own source '{source.Id}'.", nameof(connection));

        using var credential = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(connection.ConfigJson) ? "{}" : connection.ConfigJson);
        using var scope = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(source.ScopeJson) ? "{}" : source.ScopeJson);

        // Refused here rather than left to the first reader that touches it. A non-object
        // configuration — "[]", a bare string — does reach an exception today, but only because
        // JsonElement.TryGetProperty happens to throw on a non-object, which is an accident of
        // which field is read first and says nothing useful to an operator. Making it explicit
        // means the guard cannot be lost by reordering an object initialiser.
        RequireJsonObject(credential, $"Connection '{connection.Name}' has configuration that");
        RequireJsonObject(scope, $"Source '{source.Name}' has a scope that");

        // The storage providers' scopes, read as SourceScopeCodec reads them (#859). Each is asked for
        // where its fields used to be read, so a missing connection setting is still reported first.
        SourceScope? typed = SourceScopeCodec.Read(connection.Provider, source.ScopeJson);
        S3BucketScope S3Scope() => typed as S3BucketScope
            ?? throw new InvalidOperationException($"Source '{source.Name}' has no bucketName in its scope.");
        AzureBlobContainerScope AzureScope() => typed as AzureBlobContainerScope
            ?? throw new InvalidOperationException($"Source '{source.Name}' has no containerName in its scope.");

        return connection.Provider switch
        {
            ConnectionProvider.S3 => new S3Connector(new S3ConnectorConfig
            {
                Region = Str(credential, "region") ?? "us-east-1",
                RoleArn = Str(credential, "roleArn"),
                BucketName = RequirePermittedLocation(
                    StorageLocationPolicy.ReadAllowedLocations(credential.RootElement),
                    S3Scope().BucketName,
                    S3Scope().Prefix,
                    connection.Name, source.Name),
                Prefix = S3Scope().Prefix,
            },
            // The identity an administrator configured, so a sync runs as whatever the
            // Providers page reports rather than as whatever the container is carrying.
            awsCredentials),

            ConnectionProvider.AzureBlob => new AzureBlobConnector(new AzureBlobConnectorConfig
            {
                AccountName = Str(credential, "accountName")
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no accountName."),
                BlobEndpoint = Str(credential, "blobEndpoint"),
                ContainerName = RequirePermittedLocation(
                    StorageLocationPolicy.ReadAllowedLocations(credential.RootElement),
                    AzureScope().ContainerName,
                    AzureScope().Prefix,
                    connection.Name, source.Name),
                Prefix = AzureScope().Prefix,
            }, azureCredentials),

            ConnectionProvider.Filesystem => new FilesystemConnector(new FilesystemConnectorConfig
            {
                RootPath = CombineUnderRoot(
                    RequirePermittedRoot(
                        Str(credential, "allowedRoot")
                            ?? throw new InvalidOperationException(
                                $"Connection '{connection.Name}' has no allowedRoot."),
                        connection.Name),
                    ((FilesystemFolderScope)typed!).SubPath,
                    source.Name),
                IncludePatterns = ((FilesystemFolderScope)typed).IncludePatterns ?? [],
                ExcludePatterns = ((FilesystemFolderScope)typed).ExcludePatterns ?? [],
            }),

            ConnectionProvider.Sftp => new SftpConnector(new SftpConnectorConfig
            {
                Host = Str(credential, "host")
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no host."),
                Port = Int(credential, "port") ?? 22,
                Username = Str(credential, "username")
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no username."),

                // Unlike the Filesystem provider, the root is not resolved here. It names a
                // directory on another machine, so the only place it can be resolved is the
                // server — the connector does that on connect, through SSH_FXP_REALPATH.
                // Resolving it locally is exactly the mistake SftpPathConfinement exists to
                // avoid, and it would fail open rather than closed.
                AllowedRoot = Str(credential, "allowedRoot")
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no allowedRoot."),

                PinnedHostKeyFingerprint = Str(credential, "hostKeyFingerprint"),
                Credential = SftpCredential.TryParse(secret)
                    ?? throw new InvalidOperationException(
                        $"Connection '{connection.Name}' has no usable SSH private key stored."),
                ConnectionId = connection.Id,

                SubPath = ((SftpFolderScope)typed!).SubPath,
                IncludePatterns = ((SftpFolderScope)typed).IncludePatterns ?? [],
                ExcludePatterns = ((SftpFolderScope)typed).ExcludePatterns ?? [],
            }, hostKeyStore),

            ConnectionProvider.GitHub => CreateGitHub(source, scope, connection, credential),

            ConnectionProvider.Atlassian => CreateAtlassian(source, scope, connection, secret),

            // The same connector a content-only custom source uses, with its own token; the custom
            // connection only adds the URIs and keys its documents are checked by.
            // Only a connection that names a custom provider checks permissions; one without reads content only.
            ConnectionProvider.Custom => CreateCustomSource(source, secret,
                permissionsFor: Custom.CustomSourceCredentials.ChecksPermissions(connection) ? source.Id : null,
                connectionBaseUrl: CustomConnectionConfig.BaseUrlOf(connection.ConfigJson)),

            _ => throw new NotSupportedException($"Unknown connection provider: {connection.Provider}")
        };
    }

    public IConnector Create(Source source, string? secret = null)
    {
        if (source.Provider is null)
            throw new ArgumentException(
                "a connection-less source must have a Provider", nameof(source));

        using var scope = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(source.ScopeJson) ? "{}" : source.ScopeJson);

        RequireJsonObject(scope, $"Source '{source.Name}' has a scope that");

        return source.Provider.Value switch
        {
            // Anonymous reads are gone: 60 requests an hour per IP cannot carry a real
            // deployment, and every GitHub source now reads as an installation of the App.
            ConnectionProvider.GitHub => throw new InvalidOperationException(
                $"Source '{source.Name}' has no connection. GitHub sources read as a GitHub App "
                + "installation: set the App up on the Providers page, add an installation on the "
                + "Connections page, and recreate this source on that connection."),

            ConnectionProvider.Custom => CreateCustomSource(source, secret),

            _ => throw new NotSupportedException(
                $"Provider {source.Provider} is not supported for connection-less sources")
        };
    }

    /// <summary>
    /// A content-only custom source. The URL rules are checked again here, not only when the source is
    /// saved, so a tightened <c>CustomSources:AllowedHttpHosts</c> stops an existing source from syncing.
    /// </summary>
    private Custom.CustomSourceConnector CreateCustomSource(
        Source source, string? secret, Guid? permissionsFor = null, string? connectionBaseUrl = null)
    {
        CustomSourceScope scope = CustomSourceScope.TryParse(source.ScopeJson)
            ?? throw new InvalidOperationException(
                $"Source '{source.Name}' has a scope that does not name a source server URL and scope.");

        // A connection's token is only sent to the middle man the connection names.
        if (connectionBaseUrl is not null && !CustomSourceEndpoint.SameAddress(scope.BaseUrl, connectionBaseUrl))
            throw new InvalidOperationException(
                $"Source '{source.Name}' cannot sync: it reads {scope.BaseUrl}, but its connection's source server is {connectionBaseUrl}.");

        IReadOnlyCollection<string> allowedHttpHosts = customSourceSettings?.CurrentValue.AllowedHttpHosts ?? [];
        if (!CustomSourceEndpoint.TryNormalize(scope.BaseUrl, allowedHttpHosts, out string baseUrl, out string? error))
            throw new InvalidOperationException($"Source '{source.Name}' cannot sync: {error}");

        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(
                $"Source '{source.Name}' cannot sync: its connection holds no token. Enter one on the connection or its custom provider.");

        Custom.CustomSourceClient client = new(
            httpClientFactory.CreateClient(Custom.CustomSourceClient.HttpClientName), baseUrl, secret);
        return new Custom.CustomSourceConnector(client, scope with { BaseUrl = baseUrl }, permissionsFor);
    }

    /// <summary>
    /// A GitHub source on an App installation. The connection names the installation its reads
    /// prefer; the shared pool may stand in another for a public repository when that one is
    /// spent. The HTTP client comes from the factory rather than being held here: this factory is a
    /// singleton, and a held client would pin its handler past DNS changes.
    /// </summary>
    private GitHubConnector CreateGitHub(Source source, JsonDocument scope, Connection connection, JsonDocument credential)
    {
        long installationId = Long(credential, "installationId")
            ?? throw new InvalidOperationException($"Connection '{connection.Name}' names no GitHub App installation.");

        if (gitHubPool is null)
            throw new InvalidOperationException("GitHub sources need the GitHub App credential pool, which is not registered.");

        var config = GitHubConfig(source, scope) with { InstallationId = installationId };

        // A private repository's documents are filtered by an address built from its id. Without
        // the id they would carry no address — and a document without one is shown to everyone.
        if (config.IsPrivate && config.RepoId is null)
            throw new InvalidOperationException(
                $"Source '{source.Name}' is a private GitHub repository without a repository id, so its documents could not be filtered. Add it again from New source.");

        // A private repository is read only as the installation that covers it — never a borrowed
        // one, which would read through a different organisation's grant.
        var access = config.IsPrivate
            ? GitHub.GitHubAccess.Pinned(installationId)
            : GitHub.GitHubAccess.Public(installationId);
        var auth = new GitHub.GitHubAuth(gitHubPool, access);

        return new GitHubConnector(config, httpClientFactory.CreateClient(GitHubHttpClientName), logger, auth);
    }

    /// <summary>
    /// Reads a GitHub source's scope. Owner and repo are checked against GitHub's own naming
    /// rules because both are spliced into the fetch URL, and the host is refused outright
    /// unless it is github.com: reads carry an installation token, and a scope that could name
    /// another host would send that token wherever it said.
    /// </summary>
    private GitHubConnectorConfig GitHubConfig(Source source, JsonDocument scope)
    {
        string owner = Str(scope, "owner") ?? "";
        if (!GitHubConnectorConfig.IsValidOwner(owner))
            throw new InvalidOperationException(
                $"Source '{source.Name}' has no valid GitHub owner in its scope.");

        string repo = Str(scope, "repo") ?? "";
        if (!GitHubConnectorConfig.IsValidRepo(repo))
            throw new InvalidOperationException(
                $"Source '{source.Name}' has no valid GitHub repo in its scope.");

        string host = Str(scope, "host") ?? GitHubConnectorConfig.PublicHost;
        if (!string.Equals(host, GitHubConnectorConfig.PublicHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Source '{source.Name}' names GitHub host '{LogSanitizer.Sanitize(host)}'; GitHub sources "
                + $"can only read {GitHubConnectorConfig.PublicHost}.");

        string kindName = Str(scope, "kind") ?? nameof(GitHubContentKind.Docs);
        if (!Enum.TryParse(kindName, ignoreCase: true, out GitHubContentKind kind)
            || !Enum.IsDefined(kind))
            throw new InvalidOperationException(
                $"Source '{source.Name}' has unknown GitHub content kind '{LogSanitizer.Sanitize(kindName)}'.");

        IReadOnlyList<string> include = Arr(scope, "includePatterns");

        return new GitHubConnectorConfig
        {
            Owner = owner,
            Repo = repo,
            RepoId = Long(scope, "repoId"),
            IsPrivate = Bool(scope, "private") ?? false,
            RequirePublic = !(Bool(scope, "private") ?? false),
            Kind = kind,
            IncludePatterns = include.Count > 0 ? include : GitHubConnectorConfig.DefaultDocPatterns,
            ExcludePatterns = Arr(scope, "excludePatterns"),
            IncludeComments = Bool(scope, "includeComments") ?? true,
            IncludeCommentAuthors = Arr(scope, "includeCommentAuthors"),
            ExcludeCommentAuthors = Arr(scope, "excludeCommentAuthors"),

            // Keyed on the source id, not owner/repo: a rename must not orphan the mirror, and
            // two sources for one repository must not share a fetch target.
            MirrorPath = Path.GetFullPath(Path.Combine(
                gitHubSettings.CurrentValue.MirrorDirectory, source.Id.ToString("N"))),
        };
    }

    private ISyncCursorConnector CreateAtlassian(Source source, JsonDocument scope, Connection connection, string? secret)
    {
        string kind = Str(scope, "kind") ?? "";
        return kind switch
        {
            "confluence-space" => CreateConfluence(source, scope, connection, secret),
            "jira-project" => CreateJira(source, scope, connection, secret),
            _ => throw new InvalidOperationException(
                $"Source '{source.Name}' has unknown Atlassian content kind '{LogSanitizer.Sanitize(kind)}'."),
        };
    }

    /// <summary>
    /// A Confluence space on an Atlassian site connection. The space id and the site's cloud id
    /// both end up in request URLs and in every document's address, so each is checked here; the
    /// connection's secret is the service account's, and nothing reads without it.
    /// </summary>
    private Atlassian.ConfluenceSpaceConnector CreateConfluence(
        Source source, JsonDocument scope, Connection connection, string? secret)
    {
        string? spaceId = ScopeId(scope, "spaceId");
        if (!Atlassian.ConfluencePageStateStore.IsContentId(spaceId))
            throw new InvalidOperationException($"Source '{source.Name}' has no valid Confluence spaceId in its scope.");

        string? spaceKey = Str(scope, "spaceKey")?.Trim();
        if (string.IsNullOrEmpty(spaceKey))
            throw new InvalidOperationException($"Source '{source.Name}' has no Confluence spaceKey in its scope.");

        (Atlassian.AtlassianSite site, string statePath, int maxAttachmentMb, Atlassian.AtlassianApiClient api) = AtlassianCommon(
            source, scope, connection, secret, Atlassian.AtlassianProduct.Confluence, Atlassian.ConfluenceSpaceConfig.MaxAttachmentMbCeiling);

        return new Atlassian.ConfluenceSpaceConnector(
            new Atlassian.ConfluenceSpaceConfig(
                site, spaceId!, spaceKey, statePath,
                IncludeAttachments: Bool(scope, "includeAttachments") ?? true,
                MaxAttachmentMb: maxAttachmentMb),
            api,
            loggerFactory?.CreateLogger<Atlassian.ConfluenceSpaceConnector>(),
            // The parsers Ingestion registered, reached through Core's interface: an attachment
            // without one is skipped and counted rather than ingested to fail.
            fileTypes: fileTypes);
    }

    /// <summary>
    /// A Jira project on an Atlassian site connection, checked as a Confluence space is: the
    /// project id ends up in every query and request URL, so it must be digits.
    /// </summary>
    private Atlassian.JiraProjectConnector CreateJira(
        Source source, JsonDocument scope, Connection connection, string? secret)
    {
        string? projectId = ScopeId(scope, "projectId");
        if (!Atlassian.JiraIssueStateStore.IsId(projectId))
            throw new InvalidOperationException($"Source '{source.Name}' has no valid Jira projectId in its scope.");

        string? projectKey = Str(scope, "projectKey")?.Trim();
        if (string.IsNullOrEmpty(projectKey))
            throw new InvalidOperationException($"Source '{source.Name}' has no Jira projectKey in its scope.");

        (Atlassian.AtlassianSite site, string statePath, int maxAttachmentMb, Atlassian.AtlassianApiClient api) = AtlassianCommon(
            source, scope, connection, secret, Atlassian.AtlassianProduct.Jira, Atlassian.JiraProjectConfig.MaxAttachmentMbCeiling);

        return new Atlassian.JiraProjectConnector(
            new Atlassian.JiraProjectConfig(
                site, projectId!, projectKey, statePath,
                IncludeAttachments: Bool(scope, "includeAttachments") ?? true,
                MaxAttachmentMb: maxAttachmentMb),
            api,
            loggerFactory?.CreateLogger<Atlassian.JiraProjectConnector>(),
            fileTypes: fileTypes);
    }

    /// <summary>A scope id given as a JSON string or number; anything else is null.</summary>
    private static string? ScopeId(JsonDocument scope, string name) =>
        scope.RootElement.TryGetProperty(name, out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>
    /// What every Atlassian source needs whatever it reads: the site, checked; the service
    /// account's secret; the attachment cap, clamped to <paramref name="maxAttachmentMbCeiling"/>; its
    /// own state directory; and a client pinned to <paramref name="product"/>.
    /// </summary>
    private (Atlassian.AtlassianSite Site, string StatePath, int MaxAttachmentMb, Atlassian.AtlassianApiClient Api) AtlassianCommon(
        Source source, JsonDocument scope, Connection connection, string? secret, Atlassian.AtlassianProduct product,
        int maxAttachmentMbCeiling)
    {
        var site = Atlassian.AtlassianSite.FromConfigJson(connection.ConfigJson)
            ?? throw new InvalidOperationException(
                $"Source '{source.Name}' is on connection '{connection.Name}', which is not a well-formed Atlassian site.");

        // A connection's credential was granted for its own product alone, so a source of the other
        // product never reads through it.
        if (site.Product != product)
            throw new InvalidOperationException(
                $"Source '{source.Name}' reads {product} but is on connection '{connection.Name}', which is a {site.Product} "
                + $"connection. Recreate the source on a {product} connection for this site.");

        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(
                $"Source '{source.Name}' is on connection '{connection.Name}', which has no service-account secret stored.");

        int maxAttachmentMb = Int(scope, "maxAttachmentMb") ?? 25;
        if (maxAttachmentMb <= 0)
            throw new InvalidOperationException($"Source '{source.Name}' has a maxAttachmentMb that is not a positive number.");
        // Clamped rather than refused, so a source saved with a larger cap still syncs.
        maxAttachmentMb = Math.Min(maxAttachmentMb, maxAttachmentMbCeiling);

        if (atlassianTokens is null || atlassianSettings is null)
            throw new InvalidOperationException(
                $"Source '{source.Name}' needs the Atlassian token source and source settings, which are not registered.");

        // Keyed on the source id, like the GitHub mirror, so two sources never share a store.
        string statePath = Path.GetFullPath(Path.Combine(
            atlassianSettings.CurrentValue.StateDirectory, source.Id.ToString("N")));

        return (site, statePath, maxAttachmentMb, new Atlassian.AtlassianApiClient(
            httpClientFactory.CreateClient(Atlassian.AtlassianApiClient.HttpClientName), atlassianTokens, site, secret,
            downloads: httpClientFactory.CreateClient(Atlassian.AtlassianApiClient.DownloadHttpClientName),
            product: product));
    }

    private static void RequireJsonObject(JsonDocument doc, string subject)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"{subject} is not a JSON object, so nothing in it can be read.");
    }

    private static int? Int(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out int i)
            ? i
            : null;

    private static bool? Bool(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static long? Long(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt64(out long l)
            ? l
            : null;

    private static string? Str(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Reads a plain string array, dropping anything that is not a string.
    /// </summary>
    /// <remarks>
    /// Only for include and exclude patterns, where the loose rule is right: a pattern is a
    /// filter, and a malformed one that is ignored simply does not filter. Deliberately not
    /// used for the allowlist — see <see cref="StorageLocationPolicy.ReadAllowedLocations"/>,
    /// where dropping a malformed entry collapses a declared control into an absent one and
    /// fails open.
    /// </remarks>
    private static IReadOnlyList<string> Arr(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
            : [];

    /// <summary>
    /// True the first time a given scope is seen, false afterwards. The factory is a
    /// singleton, so this is shared across every sync cycle for the process's lifetime.
    /// </summary>
    private bool ShouldWarn(string key)
    {
        lock (_warnedUnrestricted)
        {
            return _warnedUnrestricted.Add(key);
        }
    }

    /// <summary>
    /// Checks that a source's bucket or blob container falls inside the locations its
    /// connection permits, and returns it.
    /// <para>
    /// The cloud counterpart of <see cref="RequirePermittedRoot"/>. IAM cannot make this
    /// distinction on its own: one connection role is shared by every source that uses the
    /// connection, so as far as AWS or Azure is concerned each of those sources is the same
    /// principal.
    /// </para>
    /// </summary>
    private string RequirePermittedLocation(
        IReadOnlyList<string>? allowedLocations, string container, string? prefix,
        string connectionName, string sourceName)
    {
        switch (StorageLocationPolicy.Evaluate(allowedLocations, container, prefix))
        {
            case StorageLocationDecision.Allowed:
                return container;

            case StorageLocationDecision.UnrestrictedByConfiguration:
                // Warned rather than refused, matching the filesystem root allowlist: #350
                // backfilled existing S3 and Azure containers into connections and none of
                // them declare locations, so enforcing now would break every upgrade.
                if (ShouldWarn($"location:{connectionName}:{container}"))
                {
                    logger.LogWarning(
                        "Connection {ConnectionName} declares no allowedLocations, so source "
                        + "{SourceName} may name any container its credential can reach. It "
                        + "currently names {Container}.",
                        LogSanitizer.Sanitize(connectionName),
                        LogSanitizer.Sanitize(sourceName),
                        LogSanitizer.Sanitize(container));
                }
                return container;

            default:
                throw new InvalidOperationException(
                    $"Source '{sourceName}' names storage location '{container}/{prefix}', which is not "
                    + $"within the allowedLocations declared by connection '{connectionName}'.");
        }
    }

    /// <summary>
    /// Checks a connection's allowed root against the deployment's configured allowlist.
    /// <para>
    /// The confinement check below stops a source escaping its root; this stops the root
    /// itself being somewhere it should never be. They are independent: an allowlist does not
    /// stop a symlink, and link resolution does not stop <c>allowedRoot: "/"</c>.
    /// </para>
    /// </summary>
    private string RequirePermittedRoot(string allowedRoot, string connectionName)
    {
        switch (sourceSecurity.CurrentValue.EvaluateRoot(allowedRoot))
        {
            case FilesystemRootDecision.Allowed:
                return allowedRoot;

            case FilesystemRootDecision.UnrestrictedByConfiguration:
                // Warned rather than refused, for one release: #350 backfilled existing
                // filesystem containers into connections, so enforcing immediately would
                // break every upgrade until an operator edited configuration. The warning
                // names the root so they know what to add.
                if (ShouldWarn($"root:{connectionName}:{allowedRoot}"))
                {
                    logger.LogWarning(
                        "Connection {ConnectionName} uses filesystem root {Root} with no "
                        + "{SectionName}:AllowedFilesystemRoots configured. Any root is currently "
                        + "accepted; configure the allowlist to bound this.",
                        LogSanitizer.Sanitize(connectionName),
                        LogSanitizer.Sanitize(allowedRoot),
                        SourceSecuritySettings.SectionName);
                }
                return allowedRoot;

            default:
                throw new InvalidOperationException(
                    $"Connection '{connectionName}' names filesystem root '{allowedRoot}', which is not "
                    + $"within any entry of {SourceSecuritySettings.SectionName}:AllowedFilesystemRoots.");
        }
    }

    /// <summary>
    /// Resolves a source's subPath beneath its connection's allowed root, and verifies the
    /// result stays inside it. The allowed root is the boundary an admin configured, so a
    /// scope containing "../" must not be able to reach past it.
    /// </summary>
    private static string CombineUnderRoot(string allowedRoot, string? subPath, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(subPath))
            return Path.GetFullPath(allowedRoot);

        // Delegated rather than compared here (#365). The previous check was Path.GetFullPath
        // plus StartsWith, which is purely lexical: a junction inside the allowed root passed
        // it, and the connector then walked straight through to the target. PathConfinement
        // resolves links on every path segment before comparing.
        return PathConfinement.CombineWithin(allowedRoot, subPath)
            ?? throw new InvalidOperationException(
                $"Source '{sourceName}' resolves to a path outside its connection's allowed root '{allowedRoot}'.");
    }
}
