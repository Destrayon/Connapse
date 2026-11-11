using System.ComponentModel;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Containers;
using Connapse.Application.Tables;
using Connapse.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace Connapse.Web.Mcp;

public partial class McpTools
{
    private static readonly JsonSerializerOptions TableJson = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "table_sql", ReadOnly = true, Idempotent = true),
     Authorize(Policy = "Scope:KnowledgeRead"), McpToolRisk(RiskTier.Read),
     Description("Run one read-only PostgreSQL SELECT over a container's tables (the tables loaded from its CSV and spreadsheet files). "
        + "Name tables and columns by their generated names: tables t_..., columns c_1, c_2, ... in header order. Send a query you aren't sure of "
        + "and a refusal lists every table you can query with its original name, row count and column headers. Tables with history have "
        + "_seen_from and _seen_until columns; current rows have _seen_until = 'infinity'. Plain SELECT, WITH, joins, aggregates and window "
        + "functions work; settings, catalog reads and writes don't. At most 1000 rows and about 64 KB come back, within 5 seconds. "
        + "Off unless an administrator turned on the container's Tables.AgentSql setting.")]
    public static async Task<string> TableSql(
        GetContainer getContainer,
        RunTableSql run,
        IHttpContextAccessor http,
        [Description("Container ID or name")] string containerId,
        [Description("One SELECT statement")] string sql,
        CancellationToken ct = default)
    {
        var (id, error) = await ContainerIdAsync(getContainer, http, containerId, ct);
        if (id is null)
            return error!;

        var ran = await run.RunAsync(Mcp(http), id.Value, sql, ct);
        if (!ran.Succeeded)
            return Refused(ran);
        return JsonSerializer.Serialize(TableSqlResponse.From(ran.Result!), TableJson);
    }
}
