using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connections;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Application.Connections;

/// <summary>
/// What a stored connection is tested with: its stored settings (<see cref="Settings"/> null), or settings about to
/// replace them, checked before they are saved. <see cref="ProbeTarget"/> is the bucket or container an S3 or Azure
/// test reads when the connection's allowed locations name none.
/// </summary>
public sealed record ConnectionTestRequest(ConnectionSettings? Settings = null, string? ProbeTarget = null);

/// <summary>A connection not saved yet, tested before it is created.</summary>
public sealed record DraftConnectionTest(string? Name, ConnectionProvider? Provider, ConnectionSettings Settings, string? ProbeTarget = null);

/// <summary>What the provider's service said, and the connection's health when the test was of its stored settings.</summary>
public sealed record ConnectionTested(ConnectionTestResult Result, ConnectionHealth? Health);

/// <summary>
/// Tests a connection against its provider's live service, as the Connections page's Test button does, and saves
/// nothing but the outcome as the connection's health. Administrators only. A stored secret is only ever used with
/// the stored settings it was saved for; an Atlassian site is tested with its stored credential alone.
/// </summary>
[Operation("connections.test", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class TestConnection(IConnectionStore connections, IConnectionTestRunner tests, IOperationAuthorizer authorizer)
{
    private const string Forbidden = "You don't have permission to test connections.";

    public async Task<Attempt<ConnectionTested, ConnectionStatus>> RunAsync(
        OperationContext context, ConnectionRef key, ConnectionTestRequest? request = null, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Fail(ConnectionStatus.Forbidden, Forbidden);

        var current = await ConnectionRules.FindAsync(connections, key, ct);
        if (current is null)
            return ConnectionRules.NotFound<ConnectionTested>(key);

        if (current.Provider == ConnectionProvider.Atlassian && request?.Settings is not null)
            return Fail(ConnectionStatus.Invalid,
                "An Atlassian site is tested with its stored credential. Replace the credential to check a new one.", "settings");

        var run = await tests.TestAsync(
            new ConnectionTestSubject(current.Name, current.Provider, request?.Settings, current, request?.ProbeTarget), ct);
        return await FinishAsync(current.Id, run, ct);
    }

    /// <summary>Tests a connection before it is created. An Atlassian site is checked by adding it instead.</summary>
    public async Task<Attempt<ConnectionTested, ConnectionStatus>> RunDraftAsync(
        OperationContext context, DraftConnectionTest draft, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Fail(ConnectionStatus.Forbidden, Forbidden);
        if (draft.Provider is not { } provider)
            return Fail(ConnectionStatus.Invalid, "provider is required.", "provider");
        if (provider == ConnectionProvider.Atlassian)
            return Fail(ConnectionStatus.Invalid, ConnectionMessages.AtlassianSiteOnly, "provider");

        var run = await tests.TestAsync(new ConnectionTestSubject(draft.Name ?? "", provider, draft.Settings, null, draft.ProbeTarget), ct);
        return await FinishAsync(null, run, ct);
    }

    private async Task<Attempt<ConnectionTested, ConnectionStatus>> FinishAsync(Guid? id, ConnectionTestRun run, CancellationToken ct)
    {
        if (run.Result is null)
            return Fail(run.Refusal, run.Problem ?? "The connection could not be tested.", run.Field);

        ConnectionHealth? health = id is { } connectionId && run.Health is not null
            ? await connections.RecordHealthAsync(connectionId, run.Health, ct)
            : null;
        return Attempt<ConnectionTested, ConnectionStatus>.Succeed(new ConnectionTested(run.Result, health));
    }

    private static Attempt<ConnectionTested, ConnectionStatus> Fail(ConnectionStatus status, string detail, string? field = null) =>
        Attempt<ConnectionTested, ConnectionStatus>.Fail(status, detail, field);
}

/// <summary>
/// Forgets the host key an SFTP connection pinned on first connect, so the next connection trusts whatever key the
/// server offers and pins that one. Administrators only; nothing else about the connection changes. Forgetting a key
/// that was never pinned does nothing. Audited as <c>connection.host_key_forgotten</c>.
/// </summary>
[Operation("connections.forgetHostKey", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ForgetConnectionHostKey(
    IConnectionStore connections, ISourceStore sources, IAuditLogger audit, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ConnectionView, ConnectionStatus>> RunAsync(OperationContext context, ConnectionRef key, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Fail(ConnectionStatus.Forbidden, "You don't have permission to change connections.");

        var current = await ConnectionRules.FindAsync(connections, key, ct);
        if (current is null)
            return ConnectionRules.NotFound<ConnectionView>(key);
        if (current.Provider != ConnectionProvider.Sftp)
            return Fail(ConnectionStatus.Invalid, $"Only an SFTP connection pins a host key; this is a {current.Provider} connection.");

        Connection updated = current;
        if (ConfigOf(current) is { } config && config.Remove(ConnectionSshHostKeyStore.FingerprintProperty))
        {
            Connection? saved;
            try
            {
                saved = await connections.UpdateAsync(current.Id,
                    new UpdateConnectionRequest(ConfigJson: config.ToJsonString(), ExpectedUpdatedAt: current.UpdatedAt), ct);
            }
            // A save in between could have pinned a key or changed the host; forgetting over it would undo that unseen.
            catch (ConnectionChangedException)
            {
                return Fail(ConnectionStatus.Conflict, "The connection changed while its host key was being forgotten. Reload it and try again.");
            }
            if (saved is null)
                return ConnectionRules.NotFound<ConnectionView>(key);

            updated = saved;
            await audit.LogAsync("connection.host_key_forgotten", "connection", current.Id.ToString(), new { current.Name }, ct);
        }

        return Attempt<ConnectionView, ConnectionStatus>.Succeed(
            new ConnectionView(updated, await ConnectionRules.IsPausedAsync(sources, updated.Id, ct)));
    }

    private static JsonObject? ConfigOf(Connection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.ConfigJson)) return null;
        try
        {
            return JsonNode.Parse(connection.ConfigJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Attempt<ConnectionView, ConnectionStatus> Fail(ConnectionStatus status, string detail) =>
        Attempt<ConnectionView, ConnectionStatus>.Fail(status, detail);
}

/// <summary>An Atlassian Cloud site's address as Atlassian knows it, and its cloud id.</summary>
public sealed record ResolvedAtlassianSite(string SiteUrl, string CloudId);

/// <summary>
/// Looks up the Atlassian Cloud site at an address before it is added: the address normalised, and the cloud id
/// Atlassian gives it. Administrators only. Nothing is stored.
/// </summary>
[Operation("atlassian.sites.resolve", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class ResolveAtlassianSite(IHttpClientFactory httpClients, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<ResolvedAtlassianSite, ConnectionStatus>> RunAsync(
        OperationContext context, string? siteUrl, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.SourcesAdmin))
            return Fail(ConnectionStatus.Forbidden, "You don't have permission to add Atlassian sites.");

        string? normalized = AtlassianSiteResolver.NormalizeSiteUrl(siteUrl);
        if (normalized is null)
            return Fail(ConnectionStatus.Invalid, "The site address must look like https://your-site.atlassian.net.", "siteUrl");

        using var http = httpClients.CreateClient(AtlassianSiteResolver.HttpClientName);
        string? cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(http, normalized, ct);
        return cloudId is null
            ? Fail(ConnectionStatus.NotFound,
                $"Couldn't find an Atlassian Cloud site at {normalized}. Check the spelling against your browser's address bar.", "siteUrl")
            : Attempt<ResolvedAtlassianSite, ConnectionStatus>.Succeed(new ResolvedAtlassianSite(normalized, cloudId));
    }

    private static Attempt<ResolvedAtlassianSite, ConnectionStatus> Fail(ConnectionStatus status, string detail, string? field = null) =>
        Attempt<ResolvedAtlassianSite, ConnectionStatus>.Fail(status, detail, field);
}

/// <summary>Tests an Atlassian site with its stored credential: <see cref="TestConnection"/>.</summary>
[Operation("atlassian.sites.test", Scope = "admin:sources", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class TestAtlassianSite(TestConnection test)
{
    public Task<Attempt<ConnectionTested, ConnectionStatus>> RunAsync(OperationContext context, Guid connectionId, CancellationToken ct = default) =>
        test.RunAsync(context, ConnectionRef.AtlassianSite(connectionId), null, ct);
}

/// <summary>The Atlassian site checks in one place, for the form that offers both.</summary>
public sealed class AtlassianSiteChecks(ResolveAtlassianSite resolve, TestAtlassianSite test)
{
    public ResolveAtlassianSite Resolve { get; } = resolve;

    public TestAtlassianSite Test { get; } = test;
}
