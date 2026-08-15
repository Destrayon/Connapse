namespace Connapse.Core;

/// <summary>
/// The credential-and-endpoint identity of a connection, extracted from a legacy
/// container's connector config. DedupKey is what decides whether two migrated
/// containers share one connection: it covers the credential and endpoint only,
/// never the scope (bucket, blob container, subpath).
/// </summary>
public record ConnectionIdentity(
    ConnectionProvider Provider,
    string DedupKey,
    string Name,
    string? ConfigJson);

public record BackfillPlanItem(
    Guid ContainerId,
    string ContainerName,
    ConnectorType ConnectorType,
    ConnectionIdentity Connection,
    string ScopeJson);

public record BackfillReport(
    int ContainersMigrated,
    int ConnectionsCreated,
    int DocumentsRepointed,
    int FoldersDeleted,
    IReadOnlyList<string> Failures);
