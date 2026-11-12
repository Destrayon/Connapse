using System.Diagnostics;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-332's speed bar (<c>eval/pass-bars.md</c>): on a space of 5,000 facts, over 200 saves, the check a save runs
/// takes at most 50 ms at p95 and a whole save at most 150 ms at p95. Half the saves repeat a key already held, half
/// are new. The first saves warm the pool and the JIT and are not timed.
/// </summary>
[Trait("Category", "Gate")]
[Collection("Integration Tests")]
public class MemorySaveMatchGateTests(SharedWebAppFixture fixture, ITestOutputHelper output)
{
    private const int Facts = 5_000;
    private const int Saves = 200;
    private static readonly TimeSpan CheckP95Limit = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SaveP95Limit = TimeSpan.FromMilliseconds(150);
    private static readonly string[] Attributes =
        ["status", "owner", "database", "deploy target", "tech lead", "runtime", "repo", "on-call rotation", "region", "kind"];

    [Fact]
    public async Task Save_OnFiveThousandFacts_ChecksAndSavesWithinTheBar()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var service = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        var caller = new MemoryCaller(Guid.NewGuid(), null, null);
        Guid space = Guid.NewGuid();
        await using (KnowledgeDbContext db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = space, Name = $"match-gate-{space:N}", Kind = ContainerKind.Memory });
            await db.SaveChangesAsync();
        }
        for (int i = 0; i < Facts; i++)
            await store.AddAsync(space, Fact($"Service {i / Attributes.Length}", Attributes[i % Attributes.Length], $"value {i}"), caller);

        for (int i = 0; i < 10; i++)
            await service.RememberAsync(space, Fact($"Warm {i}", "status", "x"), caller);

        List<TimeSpan> saves = [], checks = [];
        int shown = 0;
        for (int i = 0; i < Saves; i++)
        {
            NewMemoryFact fact = i % 2 == 0
                ? Fact($"Service {i * 2}", Attributes[i % Attributes.Length], $"changed {i}")
                : Fact($"Newcomer {i}", Attributes[i % Attributes.Length], $"value {i}");

            var timer = Stopwatch.StartNew();
            MemoryWriteResult written = await service.RememberAsync(space, fact, caller);
            saves.Add(timer.Elapsed);
            written.Fact.Should().NotBeNull();
            shown += written.Matches.Count > 0 ? 1 : 0;

            timer.Restart();
            MemoryMatchSearch search = await store.MatchCandidatesAsync(written.Fact!, caller, MemoryService.MaxMatchCandidates);
            MemoryFactMatcher.Match(written.Fact!, search.Saved, search.Candidates);
            checks.Add(timer.Elapsed);
        }

        TimeSpan saveP95 = P95(saves), checkP95 = P95(checks);
        output.WriteLine($"{Facts} facts, {Saves} saves: check p95 {checkP95.TotalMilliseconds:F1} ms (median {Median(checks):F1}), "
            + $"save p95 {saveP95.TotalMilliseconds:F1} ms (median {Median(saves):F1}); saves showing a match {shown}");

        shown.Should().BeGreaterThanOrEqualTo(Saves / 2, "every save repeating a held key shows it");
        checkP95.Should().BeLessThanOrEqualTo(CheckP95Limit);
        saveP95.Should().BeLessThanOrEqualTo(SaveP95Limit);
    }

    private static NewMemoryFact Fact(string subject, string attribute, string value) =>
        new(subject, attribute, value, null, null, [], null, MemoryScope.Team);

    private static TimeSpan P95(List<TimeSpan> times) => times.OrderBy(t => t).ElementAt((int)Math.Ceiling(times.Count * 0.95) - 1);

    private static double Median(List<TimeSpan> times) => times.OrderBy(t => t).ElementAt(times.Count / 2).TotalMilliseconds;
}
