using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Connapse.Application;
using Connapse.Application.Memory;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Data;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-228's pass bar: one recall across the ten project containers of the memory dataset answers the held-out
/// questions as well as a recall of the question's own container (Recall@3 no more than 0.02 lower), with a p95 no more
/// than twice the one-container p95 and under 150 ms. Each project's notes and memory operations go in a container of
/// their own; the across recall names all ten, which is the path a recall naming no container takes over these ten.
/// Timing is as in <see cref="MemoryGateTests"/>: warm calls first, then the best of three per question.
/// </summary>
[Trait("Category", "Gate")]
[Collection("Integration Tests")]
public class MemoryAcrossContainersGateTests(SharedWebAppFixture fixture, ITestOutputHelper output)
{
    private const int K = 3;
    private const double MaxRecallDrop = 0.02;
    private static readonly TimeSpan RecallP95Limit = TimeSpan.FromMilliseconds(150);

    private sealed record Question(string Id, string Code, string Kind, string Text, JsonObject Answer, string[] Evidence);

    private static string DatasetRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Connapse.slnx not found"),
            "eval", "datasets", "memory-questions");
    }

    private static IEnumerable<JsonObject> Lines(string path) =>
        File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject());

    private static DateTimeOffset[] Dates(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text, @"\d{4}-\d{2}-\d{2}").Select(m => DateTimeOffset.ParseExact(m.Value, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal))];

    /// <summary>The call an agent makes for each kind, as the memory eval makes it; a window names the project, as an agent asking across projects would.</summary>
    private static MemoryQuery QueryFor(Question q, string subject) => q.Kind switch
    {
        "current" => new MemoryQuery(Guid.Empty, q.Text, Limit: K),
        "as_of" => new MemoryQuery(Guid.Empty, q.Text, AsOf: Dates(q.Text)[0], Limit: K),
        "changed" => new MemoryQuery(Guid.Empty, Subject: subject, ChangedFrom: Dates(q.Text)[0], ChangedTo: Dates(q.Text)[1], Limit: 100),
        _ => new MemoryQuery(Guid.Empty, q.Text, History: true, Limit: K),
    };

    /// <summary>Recall@3: every change in a window needs one of its evidence notes cited; otherwise any evidence note does.</summary>
    private static double Recall(Question q, IReadOnlyList<RecalledFact> recalled, IReadOnlyDictionary<string, string> noteOf)
    {
        IEnumerable<MemoryFact> facts = recalled.Select(f => f.Fact);
        if (q.Kind == "changed")
        {
            DateTimeOffset[] window = Dates(q.Text);
            facts = facts.Where(f => f.SupersededAt is null && f.ValidFrom is { } from && from >= window[0] && from <= window[1])
                .OrderBy(f => f.ValidFrom);
        }
        HashSet<string> cited = [.. facts.Take(K).SelectMany(f => f.Sources).Select(s => noteOf.GetValueOrDefault(s.Ref) ?? s.Ref)];
        if (q.Kind == "changed")
            return q.Answer["changes"]!.AsArray().All(c => c!["evidence"]!.AsArray().Any(e => cited.Contains((string)e!))) ? 1 : 0;
        return q.Evidence.Any(cited.Contains) ? 1 : 0;
    }

    private static async Task<(TimeSpan Best, T Result)> BestOfThreeAsync<T>(Func<Task<T>> call)
    {
        (TimeSpan Best, T Result) best = (TimeSpan.MaxValue, default!);
        for (int i = 0; i < 3; i++)
        {
            var timer = Stopwatch.StartNew();
            T result = await call();
            if (timer.Elapsed < best.Best)
                best = (timer.Elapsed, result);
        }
        return best;
    }

    private static TimeSpan P95(List<TimeSpan> times) => times.Order().ElementAt((int)Math.Ceiling(times.Count * 0.95) - 1);

    [Fact]
    public async Task RecallAcrossTenContainers_AnswersAsWellAsOneContainer_WithoutABigSlowdown()
    {
        string root = DatasetRoot();
        List<Question> questions = [.. Lines(Path.Combine(root, "questions.jsonl")).Where(q => (string)q["split"]! == "heldout")
            .Select(q => new Question((string)q["id"]!, (string)q["code"]!, (string)q["kind"]!, (string)q["question"]!,
                q["answer"]!.AsObject(), [.. q["evidence"]!.AsArray().Select(e => (string)e!)]))
            .Where(q => q.Evidence is not ["(none)"])];
        List<JsonObject> notes = [.. Lines(Path.Combine(root, "notes.jsonl"))];
        string[] codes = [.. questions.Select(q => q.Code).Distinct().Order(StringComparer.Ordinal)];
        codes.Should().HaveCountGreaterThanOrEqualTo(2);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        Guid user = Guid.NewGuid();
        await using (var identity = await sp.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>().CreateDbContextAsync())
        {
            identity.Users.Add(new Connapse.Identity.Data.Entities.ConnapseUser
            {
                Id = user,
                UserName = $"x-{user:N}@example.com",
                Email = $"x-{user:N}@example.com",
                CreatedAt = DateTime.UtcNow,
            });
            await identity.SaveChangesAsync();
        }

        // One container per project, holding a document per note.
        Dictionary<string, Guid> containerOf = [];
        Dictionary<string, string> documentOf = [];
        await using (var db = await sp.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync())
        {
            foreach (string code in codes)
            {
                var container = new ContainerEntity { Id = Guid.NewGuid(), Name = $"across-{code.ToLowerInvariant()}-{Guid.NewGuid():N}", Kind = ContainerKind.Memory };
                db.Containers.Add(container);
                containerOf[code] = container.Id;
                foreach (JsonObject note in notes.Where(n => (string)n["code"]! == code))
                {
                    var document = new DocumentEntity
                    {
                        Id = Guid.NewGuid(),
                        ContainerId = container.Id,
                        FileName = $"{(string)note["note"]!}.md",
                        Path = $"/{(string)note["note"]!}.md",
                        ContentHash = Guid.NewGuid().ToString("N"),
                        IngestionStatus = DocumentStatus.Ready,
                        CreatedAt = DateTime.UtcNow,
                        Metadata = [],
                    };
                    db.Documents.Add(document);
                    documentOf[(string)note["note"]!] = document.Id.ToString();
                }
            }
            await db.SaveChangesAsync();
        }
        Dictionary<string, string> noteOf = documentOf.ToDictionary(p => p.Value, p => p.Key);

        // Each project's memory operations, replayed as the memory eval replays them.
        IMemoryService memory = sp.GetRequiredService<IMemoryService>();
        var caller = new MemoryCaller(user, null, "across-gate");
        Dictionary<string, string> subjectOf = [];
        foreach (string code in codes)
        {
            Dictionary<string, Guid> factOf = [];
            Dictionary<string, List<MemorySource>> sourcesOf = [];
            foreach (JsonObject op in Lines(Path.Combine(root, "memory-ops", $"{code}.jsonl")))
            {
                string? from = (string?)op["validFrom"];
                // A team correction keeps every source the old fact cites, so the new value is never shown more widely.
                List<MemorySource> sources = [new MemorySource(MemorySourceKind.Document, documentOf[(string)op["session"]!])];
                if ((string)op["op"]! != "remember")
                    sources = [.. sourcesOf[(string)op["target"]!].Union(sources)];
                var fact = new NewMemoryFact((string?)op["subject"] ?? "", (string?)op["attribute"] ?? "", (string)op["value"]!,
                    from is null ? null : Dates(from)[0], null, sources, (string?)op["reason"]);
                MemoryWriteResult result = (string)op["op"]! == "remember"
                    ? await memory.RememberAsync(containerOf[code], fact, caller)
                    : await memory.CorrectAsync(factOf[(string)op["target"]!],
                        (string)op["kind"]! == "wrong" ? MemoryCorrection.Wrong : MemoryCorrection.Changed, fact, caller, teamWriter: true);
                result.Fact.Should().NotBeNull(result.Error);
                factOf[(string)op["id"]!] = result.Fact!.Id;
                sourcesOf[(string)op["id"]!] = sources;
                subjectOf.TryAdd(code, result.Fact.Subject);
            }
        }

        var context = new OperationContext(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim("actor_type", "agent"),
            new Claim("on_behalf_of", user.ToString()),
            new Claim(ClaimTypes.Role, "Agent"),
            new Claim("scope", "knowledge:read"),
        ], "test")), OperationSurface.Mcp);
        var recall = sp.GetRequiredService<RecallMemory>();
        string[] all = [.. codes.Select(c => containerOf[c].ToString())];

        async Task<IReadOnlyList<RecalledFact>> One(Question q)
        {
            var recalled = await recall.RunAsync(context, containerOf[q.Code].ToString(), QueryFor(q, subjectOf[q.Code]));
            recalled.Succeeded.Should().BeTrue(recalled.Detail);
            return recalled.Result!;
        }
        async Task<IReadOnlyList<RecalledFact>> Across(Question q)
        {
            var recalled = await recall.AcrossAsync(context, all, QueryFor(q, subjectOf[q.Code]));
            recalled.Succeeded.Should().BeTrue(recalled.Detail);
            recalled.Result!.Facts.Should().OnlyContain(f => recalled.Result.Spaces.ContainsKey(f.Fact.ContainerId));
            return recalled.Result.Facts;
        }

        foreach (Question q in questions.Take(10))
        {
            await One(q);
            await Across(q);
        }

        List<(Question Q, double One, double Across)> scored = [];
        List<TimeSpan> oneTimes = [], acrossTimes = [];
        foreach (Question q in questions)
        {
            (TimeSpan oneTime, IReadOnlyList<RecalledFact> one) = await BestOfThreeAsync(() => One(q));
            (TimeSpan acrossTime, IReadOnlyList<RecalledFact> across) = await BestOfThreeAsync(() => Across(q));
            oneTimes.Add(oneTime);
            acrossTimes.Add(acrossTime);
            scored.Add((q, Recall(q, one, noteOf), Recall(q, across, noteOf)));
        }

        foreach (IGrouping<string, (Question Q, double One, double Across)> kind in scored.GroupBy(s => s.Q.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            output.WriteLine($"{kind.Key}: {kind.Count()} questions, one container Recall@{K} {kind.Average(s => s.One):F3}, across {kind.Average(s => s.Across):F3}");
        double oneRecall = scored.Average(s => s.One), acrossRecall = scored.Average(s => s.Across);
        TimeSpan oneP95 = P95(oneTimes), acrossP95 = P95(acrossTimes);
        output.WriteLine($"all: {scored.Count} questions, one container Recall@{K} {oneRecall:F3}, across {acrossRecall:F3}; "
            + $"p95 one container {oneP95.TotalMilliseconds:F1} ms, across {acrossP95.TotalMilliseconds:F1} ms");

        acrossRecall.Should().BeGreaterThanOrEqualTo(oneRecall - MaxRecallDrop);
        acrossP95.Should().BeLessThanOrEqualTo(oneP95 * 2);
        acrossP95.Should().BeLessThan(RecallP95Limit);
    }
}
