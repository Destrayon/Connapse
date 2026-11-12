using System.Security.Claims;
using System.Text.Json;
using Connapse.Application.Containers;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Connapse.Storage.Tables;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Tables;

/// <summary>
/// The answer of a saved query (CON-311). <see cref="Trusted"/> says the query was written and saved by an admin, not
/// by the agent, the way Databricks Genie and Snowflake Cortex Analyst mark answers from verified queries. The cells
/// in <see cref="Result"/> are still data from uploaded files, not instructions.
/// </summary>
public sealed record SavedQueryAnswer(string Query, string Description, string SavedBy, DateTime SavedAt, TableQueryResult Result)
{
    public bool Trusted => true;
}

/// <summary>What saved queries share (CON-311): who saved one, the caller's login values, and the tables one reads.</summary>
public static class SavedQueryRules
{
    /// <summary>
    /// The caller's login values. The user id is the user the request is for (for an agent's key, the user it acts
    /// for). The email is a person's own; an agent's key has none, so a query that needs it refuses agents' keys.
    /// </summary>
    public static SavedQueryLogin Login(ClaimsPrincipal user)
    {
        bool agent = user.FindFirst(SearchPrincipal.ActorTypeClaim)?.Value == "agent";
        string? email = agent ? null : user.FindFirst(ClaimTypes.Email)?.Value;
        return new SavedQueryLogin(
            SearchPrincipal.Resolve(user)?.ToString(),
            string.IsNullOrWhiteSpace(email) ? null : email);
    }

    /// <summary>Who saved a query, as shown beside its answers.</summary>
    public static string Saver(ClaimsPrincipal user)
    {
        string? name = user.Identity?.Name;
        string who = string.IsNullOrWhiteSpace(name) ? SearchPrincipal.Resolve(user)?.ToString() ?? "unknown" : name;
        return who.Length <= 256 ? who : who[..256];
    }

    /// <summary>The tables <paramref name="definition"/> reads: its table and the one it joins, if any.</summary>
    public static IReadOnlyList<Guid> Tables(SavedQueryDefinition definition)
    {
        BoundSavedQuery sample = SavedQueryBinder.Validate(definition);
        return ReadTable.TableIds(sample.TableId, sample.Join);
    }

    internal static string NotFound(string name) =>
        $"This container has no saved query named \"{(name.Length <= 64 ? name : name[..64] + "...")}\". List its saved queries to find their names.";
}

/// <summary>
/// Saves a query for agents over a container's tables (CON-311), replacing the container's query of the same name. The
/// query is a <c>table_read</c> or <c>table_aggregate</c> request whose filter values may be typed parameters, some
/// filled from the caller's login. It is checked before it is saved: every parameter declared and used, defaults of
/// the right type, and tables that exist in the container. Admins only.
/// </summary>
[Operation("tables.queries.save", Scope = "admin:settings", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class SaveTableQuery(
    IContainerStore containers, ITableStore tables, ISavedQueryStore saved, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<SavedQuery, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, SavedQueryDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<SavedQuery, TableStatus>.Fail(TableStatus.Forbidden, "Only admins can save queries for agents.");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return Attempt<SavedQuery, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (container.Kind == ContainerKind.Memory)
            return Attempt<SavedQuery, TableStatus>.Fail(TableStatus.MemoryContainer, ContainerRules.MemoryOnly(container));

        IReadOnlyList<Guid> tableIds;
        try
        {
            tableIds = SavedQueryRules.Tables(definition with { Parameters = definition.Parameters ?? [] });
        }
        catch (TableQueryException ex)
        {
            return Attempt<SavedQuery, TableStatus>.Fail(TableStatus.InvalidRequest, ex.Message);
        }
        foreach (Guid id in tableIds)
        {
            if (await tables.GetAsync(containerId, id, ct) is null)
                return Attempt<SavedQuery, TableStatus>.Fail(TableStatus.TableNotFound,
                    $"There is no table with id {id} in this container. List the container's tables to find their ids.");
        }

        SavedQuery query = new(containerId, definition with { Parameters = definition.Parameters ?? [] },
            SavedQueryRules.Saver(context.User), DateTime.UtcNow);
        return Attempt<SavedQuery, TableStatus>.Succeed(await saved.SaveAsync(query, ct));
    }
}

/// <summary>
/// Deletes a saved query (CON-311). Agents can no longer list or run it. Admins only.
/// </summary>
[Operation("tables.queries.delete", Scope = "admin:settings", Risk = RiskTier.Destructive, Mcp = McpExposure.Generic)]
public sealed class DeleteTableQuery(IContainerStore containers, ISavedQueryStore saved, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<bool, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, string name, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<bool, TableStatus>.Fail(TableStatus.Forbidden, "Only admins can delete saved queries.");
        if (await containers.GetAsync(containerId, ct) is null)
            return Attempt<bool, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        return SavedQueryBinder.IsName(name) && await saved.DeleteAsync(containerId, name, ct)
            ? Attempt<bool, TableStatus>.Succeed(true)
            : Attempt<bool, TableStatus>.Fail(TableStatus.SavedQueryNotFound, SavedQueryRules.NotFound(name ?? ""));
    }
}

/// <summary>
/// Lists the queries admins saved for a container's tables (CON-311) that the caller can run: a query over a table the
/// caller can't read isn't listed, so the list never shows that such a table is there. Open to viewers and reading
/// agent keys; when access can't be checked, nothing is listed.
/// </summary>
[Operation("tables.queries.list", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ListTableQueries(
    IContainerStore containers,
    ITableStore tables,
    ISavedQueryStore saved,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<SavedQuery>, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<IReadOnlyList<SavedQuery>, TableStatus>.Fail(TableStatus.Forbidden, "You don't have permission to view tables.");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return Attempt<IReadOnlyList<SavedQuery>, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (container.Kind == ContainerKind.Memory)
            return Attempt<IReadOnlyList<SavedQuery>, TableStatus>.Succeed([]);

        IReadOnlyList<SavedQuery> all = await saved.ListAsync(containerId, ct);
        if (all.Count == 0)
            return Attempt<IReadOnlyList<SavedQuery>, TableStatus>.Succeed([]);
        Dictionary<Guid, TableInfo> present = (await tables.ListAsync(containerId, ct)).ToDictionary(t => t.Id);
        TableAccess readable = await TableRules.AccessAsync(documents, access, context.User, containerId, present.Values, ct);
        if (!readable.Checked)
            return Attempt<IReadOnlyList<SavedQuery>, TableStatus>.Fail(TableStatus.AccessUnchecked, TableAccess.UncheckedMessage);

        List<SavedQuery> runnable = [];
        foreach (SavedQuery query in all)
        {
            IReadOnlyList<Guid> used;
            try
            {
                used = SavedQueryRules.Tables(query.Definition);
            }
            catch (TableQueryException)
            {
                continue; // Saved before a rule it now breaks; it can't run, so it isn't offered.
            }
            if (used.All(id => present.TryGetValue(id, out TableInfo? table) && readable.CanRead(table)))
                runnable.Add(query);
        }
        return Attempt<IReadOnlyList<SavedQuery>, TableStatus>.Succeed(runnable);
    }
}

/// <summary>
/// Runs a saved query (CON-311) with the caller's arguments and login values. The query runs like an agent's own
/// <c>tables.read</c> or <c>tables.aggregate</c>: only rows from documents the caller may read, and hidden columns
/// don't exist. A query over a table the caller can't read fails the same way as one that doesn't exist. A query that
/// needs a login value the caller doesn't have (such as an email address for an agent's key) is refused.
/// </summary>
[Operation("tables.queries.run", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class RunTableQuery(
    IContainerStore containers,
    ITableStore tables,
    ITableQueryService queries,
    ISavedQueryStore saved,
    IDocumentStore documents,
    IDocumentAccess access,
    IOperationAuthorizer authorizer,
    IMemoryService memory)
{
    public async Task<Attempt<SavedQueryAnswer, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, string name, JsonElement? arguments, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return Attempt<SavedQueryAnswer, TableStatus>.Fail(TableStatus.Forbidden, "You don't have permission to read tables.");
        if (await containers.GetAsync(containerId, ct) is null)
            return Attempt<SavedQueryAnswer, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (!SavedQueryBinder.IsName(name) || await saved.GetAsync(containerId, name, ct) is not { } query)
            return Attempt<SavedQueryAnswer, TableStatus>.Fail(TableStatus.SavedQueryNotFound, SavedQueryRules.NotFound(name ?? ""));

        IReadOnlyList<Guid> tableIds;
        try
        {
            tableIds = SavedQueryRules.Tables(query.Definition);
        }
        catch (TableQueryException)
        {
            return Attempt<SavedQueryAnswer, TableStatus>.Fail(TableStatus.SavedQueryNotFound, SavedQueryRules.NotFound(name));
        }

        Attempt<SavedQueryAnswer, TableStatus> answer = await new TableQueryGate(containers, tables, documents, access, authorizer, memory)
            .AttemptAsync(context, containerId, tableIds, async (_, readable) =>
            {
                SavedQueryLogin login = SavedQueryRules.Login(context.User);
                if (SavedQueryBinder.MissingLogin(query.Definition, login) is { Count: > 0 } missing)
                    return Attempt<SavedQueryAnswer, TableStatus>.Fail(TableStatus.LoginValueMissing,
                        $"\"{query.Name}\" is filled from your login's {string.Join(" and ", missing.Select(Field))}, which your login doesn't have.");
                BoundSavedQuery bound = SavedQueryBinder.Bind(query.Definition, arguments, login);
                TableQueryResult result = bound.Read is { } read
                    ? await queries.ReadAsync(containerId, readable, read, ct)
                    : await queries.AggregateAsync(containerId, readable, bound.Aggregate!, ct);
                return Attempt<SavedQueryAnswer, TableStatus>.Succeed(
                    new SavedQueryAnswer(query.Name, query.Definition.Description, query.SavedBy, query.SavedAt, result));
            }, ct);
        return answer.Status == TableStatus.TableNotFound
            ? Attempt<SavedQueryAnswer, TableStatus>.Fail(TableStatus.SavedQueryNotFound, SavedQueryRules.NotFound(name))
            : answer;
    }

    private static string Field(SavedQueryLoginField field) =>
        field == SavedQueryLoginField.Email ? "email address" : "user id";
}
