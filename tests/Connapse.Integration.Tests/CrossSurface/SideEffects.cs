using System.Text.Json;
using System.Text.RegularExpressions;
using Connapse.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Runs an action on any surface and reads back what it really changed: the caller projects the
/// resulting state, and audit rows and Hangfire jobs are read straight from the database. Reading
/// the database rather than asking the surface is the point: a surface can't report an audit row
/// it never wrote.
/// </summary>
/// <remarks>
/// Only records written during the action count: audit rows and jobs that existed before it are
/// excluded, so a resource reused across actions doesn't carry earlier effects forward. Within
/// that window, records are matched by resource id: the ids the projection reports, plus the ids
/// the caller already knows (a resource the action deletes no longer appears in any projection),
/// and every document in a container among them, so a job or row aimed at the wrong document of
/// that container still shows up. Every integration test shares one host and one database, and an
/// earlier test's background work can still be running, so matching by time alone would pick up
/// other tests' work.
/// </remarks>
public static partial class SideEffects
{
    public static async Task<Outcome> CaptureAsync(
        SharedWebAppFixture fixture,
        Func<Task<string>> act,
        Func<IServiceProvider, Task<StateProjection>> project,
        IReadOnlyDictionary<string, string>? placeholders = null,
        IReadOnlyCollection<string>? knownIds = null)
    {
        var before = await BoundaryAsync(fixture);
        string result = await act();

        using var scope = fixture.Factory.Services.CreateScope();
        var projection = await project(scope.ServiceProvider);
        var ids = projection.ResourceIds.Concat(knownIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ids.AddRange((await DocumentsInContainersAsync(scope.ServiceProvider, ids)).Where(d => !ids.Contains(d, StringComparer.OrdinalIgnoreCase)));

        var tokens = new Dictionary<string, string>(placeholders ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ids.Count; i++)
            tokens.TryAdd(ids[i], $"{{id{i + 1}}}");

        var audit = await AuditRowsAsync(scope.ServiceProvider, ids, before, tokens);
        var jobs = await JobsAsync(scope.ServiceProvider, ids, before);

        return new Outcome(
            Normalize(result, tokens),
            projection.State.Select(s => Normalize(s, tokens)).ToList(),
            audit.Select(s => Normalize(s, tokens)).ToList(),
            jobs.Select(s => Normalize(s, tokens)).ToList());
    }

    /// <summary>Documents of any container among the ids, oldest first, so their tokens are stable.</summary>
    private static async Task<List<string>> DocumentsInContainersAsync(IServiceProvider services, List<string> ids)
    {
        if (ids.Count == 0)
            return [];
        await using var conn = await OpenHangfireAsync(services);
        await using var cmd = new NpgsqlCommand(
            "SELECT id::text FROM documents WHERE container_id::text = ANY(@ids) ORDER BY created_at, id", conn);
        cmd.Parameters.AddWithValue("ids", ids.Select(i => i.ToLowerInvariant()).ToArray());
        var docs = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            docs.Add(reader.GetString(0));
        return docs;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> ActorTokens = new();
    private static int _actors;

    /// <summary>Where the audit log and the job table ended before the action.</summary>
    private sealed record Boundary(DateTime LastAuditAt, HashSet<Guid> AuditIdsAtLast, long LastJobId);

    private static async Task<Boundary> BoundaryAsync(SharedWebAppFixture fixture)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        DateTime last = await db.AuditLogs.AsNoTracking().MaxAsync(a => (DateTime?)a.CreatedAt) ?? DateTime.MinValue;
        // Rows sharing the last timestamp are excluded by id, so a row written in the same tick still counts.
        var atLast = await db.AuditLogs.AsNoTracking().Where(a => a.CreatedAt == last).Select(a => a.Id).ToListAsync();

        await using var conn = await OpenHangfireAsync(scope.ServiceProvider);
        await using var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(id), 0) FROM hangfire.job", conn);
        long lastJob = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        return new Boundary(last, [.. atLast], lastJob);
    }

    /// <summary>
    /// "action type resource actor=... details=...", oldest first. The seeded admin is {admin} and
    /// any other user a numbered {userN}, numbered once for the whole test run, so the same account
    /// is the same token in every capture and an event attributed to the wrong user doesn't compare
    /// equal; details are kept because they record what the action decided.
    /// </summary>
    private static async Task<List<string>> AuditRowsAsync(IServiceProvider services, List<string> ids, Boundary before,
        Dictionary<string, string> tokens)
    {
        if (ids.Count == 0)
            return [];
        var db = services.GetRequiredService<ConnapseIdentityDbContext>();
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.CreatedAt >= before.LastAuditAt && !before.AuditIdsAtLast.Contains(a.Id))
            .Where(a => a.ResourceId != null && ids.Contains(a.ResourceId))
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Action, a.ResourceType, a.ResourceId, a.UserId, a.Details })
            .ToListAsync();

        var admin = await db.Users.AsNoTracking()
            .Where(u => u.Email == SharedWebAppFixture.AdminEmail).Select(u => (Guid?)u.Id).FirstOrDefaultAsync();
        if (admin is not null)
            tokens.TryAdd(admin.Value.ToString(), "{admin}");
        foreach (var user in rows.Select(r => r.UserId).OfType<Guid>().Distinct())
            tokens.TryAdd(user.ToString(), ActorTokens.GetOrAdd(user, _ => $"{{user{Interlocked.Increment(ref _actors)}}}"));

        return rows.Select(a =>
            $"{a.Action} {a.ResourceType} {a.ResourceId} actor={(a.UserId is null ? "none" : a.UserId.ToString())}"
            + $" details={a.Details?.RootElement.GetRawText() ?? "none"}").ToList();
    }

    /// <summary>
    /// Jobs queued during the action whose arguments mention a resource id, as
    /// Interface.Method(arguments), oldest first. Arguments are kept because they decide what the
    /// job does (an ingestion's document and options); job state is not, since a worker may pick
    /// the job up at any moment after it is queued.
    /// </summary>
    private static async Task<List<string>> JobsAsync(IServiceProvider services, List<string> ids, Boundary before)
    {
        if (ids.Count == 0)
            return [];
        await using var conn = await OpenHangfireAsync(services);
        await using var cmd = new NpgsqlCommand(
            "SELECT invocationdata::text, arguments::text FROM hangfire.job WHERE id > @last AND arguments::text ILIKE ANY(@patterns) ORDER BY id", conn);
        cmd.Parameters.AddWithValue("last", before.LastJobId);
        cmd.Parameters.AddWithValue("patterns", ids.Select(id => $"%{id}%").ToArray());

        var jobs = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            using var invocation = JsonDocument.Parse(reader.GetString(0));
            string type = invocation.RootElement.GetProperty("Type").GetString() ?? "?";
            string method = invocation.RootElement.GetProperty("Method").GetString() ?? "?";
            string shortType = type.Split(',')[0].Split('.')[^1];
            jobs.Add($"{shortType}.{method}({Arguments(reader.GetString(1))})");
        }
        return jobs;
    }

    /// <summary>Hangfire stores each argument as its own JSON string; unwrap them into one readable list.</summary>
    private static string Arguments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.String)
            return Arguments(doc.RootElement.GetString()!);
        return string.Join(", ", doc.RootElement.EnumerateArray().Select(a =>
            a.ValueKind == JsonValueKind.String ? a.GetString() : a.GetRawText()));
    }

    private static async Task<NpgsqlConnection> OpenHangfireAsync(IServiceProvider services)
    {
        string connection = services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;
        var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        return conn;
    }

    private static string Normalize(string text, Dictionary<string, string> tokens)
    {
        foreach (var (value, token) in tokens.OrderByDescending(t => t.Key.Length))
            text = text.Replace(value, token, StringComparison.OrdinalIgnoreCase);
        return Timestamp().Replace(text, "{time}");
    }

    /// <summary>An ISO 8601 timestamp, such as an upload's UploadedAt: different on every run.</summary>
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?")]
    private static partial Regex Timestamp();
}
