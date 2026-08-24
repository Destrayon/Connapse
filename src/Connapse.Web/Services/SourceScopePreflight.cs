using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// Answers "would this scope be allowed?" before a source is created, so an operator sees the
/// refusal in the form instead of discovering it as a failed sync five minutes later.
/// <para>
/// <strong>This is not the enforcement point.</strong> <c>ConnectorFactory.Create(Source,
/// Connection)</c> is, and it re-checks on every sync cycle — which matters because a
/// connection's allowlist can be narrowed after a source already exists. This class exists so
/// the common case fails early and legibly; removing it would cost clarity, not safety.
/// </para>
/// <para>
/// It calls the same primitives the factory calls — <see cref="StorageLocationPolicy"/> and
/// <see cref="SourceSecuritySettings.EvaluateRoot"/> — rather than restating the rules, because
/// two implementations of an allowlist will drift and the copy that drifts is the one nobody
/// re-reads.
/// </para>
/// </summary>
public class SourceScopePreflight(IOptionsMonitor<SourceSecuritySettings> sourceSecurity)
{
    /// <summary>
    /// Evaluates the scope against its connection.
    /// <para>
    /// Deliberately does not touch the network. A bucket that is allowed but unreachable is a
    /// credential problem, and reporting it here would block creating a perfectly valid source
    /// whenever the environment happens to be missing its cloud session.
    /// </para>
    /// </summary>
    public ScopePreflightResult Check(Connection connection, string scopeJson)
    {
        JsonObject? credential = Parse(connection.ConfigJson);
        JsonObject? scope = Parse(scopeJson);

        if (scope is null)
            return ScopePreflightResult.Refuse("The scope could not be read as JSON.");

        // Blank and malformed both parse to null, and they must not mean the same thing here.
        // Blank is fine — ConnectorFactory reads it as "{}" — but malformed makes the factory
        // throw on the first sync, while an empty credential looks to the check below like a
        // connection declaring no allowlist, which is merely a warning. Preflight would then
        // wave through a source that cannot possibly work, which is the one thing it exists
        // to prevent.
        if (credential is null && !string.IsNullOrWhiteSpace(connection.ConfigJson))
            return ScopePreflightResult.Refuse(
                $"Connection '{connection.Name}' has configuration that is not valid JSON, so no "
                + "source using it can sync. Re-save the connection to repair it.");

        return connection.Provider switch
        {
            ConnectionProvider.S3 => CheckLocation(credential, scope, "bucketName", "bucket", connection.Name),
            ConnectionProvider.AzureBlob => CheckLocation(credential, scope, "containerName", "blob container", connection.Name),
            ConnectionProvider.Filesystem => CheckRoot(credential, scope, connection.Name),
            _ => ScopePreflightResult.Refuse(
                $"Connection '{connection.Name}' uses a provider this form does not understand.")
        };
    }

    private static ScopePreflightResult CheckLocation(
        JsonObject? credential, JsonObject scope, string scopeKey, string noun, string connectionName)
    {
        string? container = Str(scope, scopeKey);
        if (string.IsNullOrWhiteSpace(container))
            return ScopePreflightResult.Refuse($"A {noun} name is required.");

        var allowed = StorageLocationPolicy.ReadAllowedLocations(credential);
        string? prefix = Str(scope, "prefix");

        return StorageLocationPolicy.Evaluate(allowed, container, prefix) switch
        {
            StorageLocationDecision.Allowed => ScopePreflightResult.Allowed,

            // Accepted, matching the factory rather than second-guessing it: a connection that
            // declares no locations is permitted for one release, because #350 backfilled
            // connections that declare none. Surfaced as a warning because this is the one
            // moment an operator is looking at the connection and could narrow it — the sync-time
            // equivalent is a log line nobody reads.
            StorageLocationDecision.UnrestrictedByConfiguration => ScopePreflightResult.Warn(
                $"Connection '{connectionName}' declares no allowed locations, so a source may name "
                + "any bucket its credential can reach. This becomes an error in a future release."),

            _ => ScopePreflightResult.Refuse(
                $"'{Join(container, prefix)}' is outside the locations connection '{connectionName}' permits.")
        };
    }

    private ScopePreflightResult CheckRoot(JsonObject? credential, JsonObject scope, string connectionName)
    {
        string? allowedRoot = Str(credential, "allowedRoot");
        if (string.IsNullOrWhiteSpace(allowedRoot))
            return ScopePreflightResult.Refuse($"Connection '{connectionName}' has no allowed root configured.");

        var rootDecision = sourceSecurity.CurrentValue.EvaluateRoot(allowedRoot);
        if (rootDecision is FilesystemRootDecision.Denied)
        {
            return ScopePreflightResult.Refuse(
                $"Connection '{connectionName}' names a root outside "
                + $"{SourceSecuritySettings.SectionName}:AllowedFilesystemRoots.");
        }

        string? subPath = Str(scope, "subPath");

        // The same call the factory makes. Resolves links on every segment, so a subPath
        // pointing through a junction is refused here rather than at the first sync.
        if (!string.IsNullOrWhiteSpace(subPath) && PathConfinement.CombineWithin(allowedRoot, subPath) is null)
        {
            return ScopePreflightResult.Refuse(
                $"'{subPath}' resolves outside the root connection '{connectionName}' allows.");
        }

        return rootDecision is FilesystemRootDecision.UnrestrictedByConfiguration
            ? ScopePreflightResult.Warn(
                $"No {SourceSecuritySettings.SectionName}:AllowedFilesystemRoots is configured, so "
                + $"connection '{connectionName}' may use any root. This becomes an error in a future release.")
            : ScopePreflightResult.Allowed;
    }

    private static string Join(string container, string? prefix) =>
        string.IsNullOrWhiteSpace(prefix)
            ? container
            : $"{container.TrimEnd('/')}/{prefix.Trim('/')}";

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try { return JsonNode.Parse(json)?.AsObject(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    // The allowlist reader used to live here, mirroring ConnectorFactory's by hand. It is now
    // StorageLocationPolicy.ReadAllowedLocations, which both call — because the two copies did
    // drift, and drifted the wrong way round: the form refused a malformed allowlist while the
    // sync-time enforcement point quietly permitted it.
}

/// <summary>
/// The outcome of a pre-flight check. <c>Warning</c> is distinct from <c>Error</c> on purpose:
/// the permissive-when-empty allowlists accept the source today and will refuse it later, so an
/// operator needs to see that without being blocked by it.
/// </summary>
public sealed record ScopePreflightResult(string? Error, string? Warning)
{
    public static readonly ScopePreflightResult Allowed = new(null, null);

    public static ScopePreflightResult Refuse(string error) => new(error, null);

    public static ScopePreflightResult Warn(string warning) => new(null, warning);

    public bool IsRefused => Error is not null;
}
