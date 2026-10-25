using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Connapse.Integration.Tests.Spec;

/// <summary>
/// Every /api response the integration suite's host sends, checked against docs/api/openapi.json
/// (epic #783, P3, CON-8). The test host runs <see cref="StartupFilter"/>, which records each response's
/// operation, status and any way it doesn't conform. At the end of a run <see cref="Finish"/> writes the
/// record for the CI step that joins every shard's (IntegrationSuite_EveryResponse_ConformsToCommittedSpec),
/// and fails the run on a violation known-spec-violations.json doesn't list.
/// </summary>
internal static class SpecConformance
{
    private static readonly Lazy<CommittedSpec> Spec = new(() => CommittedSpec.Load(Path.Combine(RepoRoot, "docs", "api", "openapi.json")));

    private static readonly ConcurrentDictionary<string, string> Violations = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Seen = new(StringComparer.Ordinal);

    public static string KnownPath => Path.Combine(RepoRoot, "tests", "Connapse.Integration.Tests", "Spec", "known-spec-violations.json");

    /// <summary>Records one response; never throws, so checking can't change what a test sees.</summary>
    public static void Record(string operationKey, int status, string? contentType, byte[] body)
    {
        Seen.TryAdd($"{operationKey} {status}", 0);
        string? reason;
        string? detail;
        try
        {
            reason = Spec.Value.Check(operationKey, status, contentType, body, out detail);
        }
        catch (Exception ex)
        {
            (reason, detail) = ("the check failed", ex.Message);
        }
        if (reason is not null)
            Violations.TryAdd($"{operationKey} {status}: {reason}", detail ?? "");
    }

    /// <summary>Writes this run's record and fails on a violation the known list doesn't have.</summary>
    public static void Finish()
    {
        if (Seen.IsEmpty)
            return;

        string results = Path.Combine(RepoRoot, "tests", "Connapse.Integration.Tests", "TestResults");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, $"spec-conformance-{Guid.NewGuid():N}.json"), JsonSerializer.Serialize(new
        {
            seen = Seen.Keys.Order(StringComparer.Ordinal),
            violations = new SortedDictionary<string, string>(Violations, StringComparer.Ordinal),
        }, new JsonSerializerOptions { WriteIndented = true }));

        var known = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(KnownPath))!;
        var added = Violations.Keys.Where(k => !known.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        if (added.Count > 0)
            throw new InvalidOperationException(
                $"{added.Count} response(s) don't match docs/api/openapi.json. Fix the endpoint or its spec, or list them in "
                + "tests/Connapse.Integration.Tests/Spec/known-spec-violations.json with the reason:" + Environment.NewLine
                + string.Join(Environment.NewLine, added.Select(k => $"  \"{k}\": \"\",   // {Violations[k]}")));
    }

    /// <summary>Runs first in the test host's pipeline, so it sees every /api response as sent.</summary>
    internal sealed class StartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, call) =>
            {
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    await call(context);
                    return;
                }

                var original = context.Response.Body;
                using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await call(context);
                }
                finally
                {
                    context.Response.Body = original;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(original);
                }

                // Only routes the app maps: an unknown path has no operation to compare with. Not the
                // authorization matrix's allowed probes either: AuthzProbe answered those, not the endpoint.
                if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route }
                    && !context.Response.Headers.ContainsKey(Authorization.AuthzProbe.AllowedHeader))
                    Record(CommittedSpec.Key(context.Request.Method, route), context.Response.StatusCode, context.Response.ContentType, buffer.ToArray());
            });
            next(app);
        };
    }

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
                    return dir.FullName;
            throw new InvalidOperationException("Connapse.slnx not found above the test output.");
        }
    }
}
