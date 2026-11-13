using System.Text.Json;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Answers a GraphQL query over a container's tables (CON-336). The schema is built per request from the tables in the
/// request's <see cref="TableAccess"/> and their visible columns, so a table or column the caller may not read is
/// unknown, not refused. Every read and aggregate goes through <see cref="ITableQueryService"/>, which checks access
/// again and applies its limits. Throws <see cref="TableQueryException"/> for a query it won't run, with a message that
/// names what to fix.
/// </summary>
public interface ITableGraphQLService
{
    Task<TableGraphQLResult> RunAsync(Guid containerId, TableAccess access, string query, CancellationToken ct = default);
}

/// <summary>
/// A GraphQL answer: <see cref="Data"/> holds one entry per root field, keyed by its alias or name.
/// <see cref="Truncated"/> says a list was cut at a row or size cap, and <see cref="TruncatedReason"/> says which.
/// </summary>
public sealed record TableGraphQLResult(JsonElement Data, bool Truncated, string? TruncatedReason)
{
    public string Notice => TableQueryResult.UntrustedDataNotice;
}
