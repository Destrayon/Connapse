using System.Text.Json;
using Connapse.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Runs an action on any surface and reads back what it really changed: the caller projects the
/// resulting state, and audit rows and Hangfire jobs are read straight from the database by the
/// projected resource ids. Reading the database rather than asking the surface is the point: a
/// surface can't report an audit row it never wrote.
/// </summary>
public static class SideEffects
{
    public static async Task<Outcome> CaptureAsync(
        SharedWebAppFixture fixture,
        Func<Task<string>> act,
        Func<IServiceProvider, Task<StateProjection>> project,
        IReadOnlyDictionary<string, string>? placeholders = null)
    {
        string result = await act();

        using var scope = fixture.Factory.Services.CreateScope();
        var projection = await project(scope.ServiceProvider);
        var ids = projection.ResourceIds.ToList();

        var audit = await AuditRowsAsync(scope.ServiceProvider, ids);
        var jobs = await JobsAsync(scope.ServiceProvider, ids);

        var tokens = new Dictionary<string, string>(placeholders ?? new Dictionary<string, string>());
        for (int i = 0; i < ids.Count; i++)
            tokens.TryAdd(ids[i], $"{{id{i + 1}}}");

        return new Outcome(
            Normalize(result, tokens),
            projection.State.Select(s => Normalize(s, tokens)).ToList(),
            audit.Select(s => Normalize(s, tokens)).ToList(),
            jobs.Select(s => Normalize(s, tokens)).ToList());
    }

    private static async Task<List<string>> AuditRowsAsync(IServiceProvider services, List<string> ids)
    {
        if (ids.Count == 0)
            return [];
        var db = services.GetRequiredService<ConnapseIdentityDbContext>();
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.ResourceId != null && ids.Contains(a.ResourceId))
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Action, a.ResourceType, a.ResourceId, a.UserId })
            .ToListAsync();
        return rows.Select(a => $"{a.Action} {a.ResourceType} {a.ResourceId} actor={(a.UserId is null ? "none" : "user")}").ToList();
    }

    /// <summary>Jobs whose arguments mention a resource id, as Interface.Method, oldest first.</summary>
    private static async Task<List<string>> JobsAsync(IServiceProvider services, List<string> ids)
    {
        if (ids.Count == 0)
            return [];
        string connection = services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT invocationdata::text FROM hangfire.job WHERE arguments::text ILIKE ANY(@patterns) ORDER BY id", conn);
        cmd.Parameters.AddWithValue("patterns", ids.Select(id => $"%{id}%").ToArray());

        var jobs = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            using var invocation = JsonDocument.Parse(reader.GetString(0));
            string type = invocation.RootElement.GetProperty("Type").GetString() ?? "?";
            string method = invocation.RootElement.GetProperty("Method").GetString() ?? "?";
            string shortType = type.Split(',')[0].Split('.')[^1];
            jobs.Add($"{shortType}.{method}");
        }
        return jobs;
    }

    private static string Normalize(string text, Dictionary<string, string> tokens)
    {
        foreach (var (value, token) in tokens.OrderByDescending(t => t.Key.Length))
            text = text.Replace(value, token, StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
