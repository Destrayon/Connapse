using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static Connapse.Integration.Tests.Memory.MemorySaveMatchCases;

namespace Connapse.Integration.Tests.Memory;

/// <summary>
/// CON-332's pass bar (<c>eval/pass-bars.md</c>) on its labelled cases: each case's space is seeded, one fact is saved,
/// and the real search plus <see cref="MemoryFactMatcher"/> pick what the save shows. Visibility is covered by the
/// service's unit tests; every fact here is a team fact.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class MemorySaveMatchTests(SharedWebAppFixture fixture, ITestOutputHelper output) : IAsyncDisposable
{
    private static readonly MemoryCaller Saver = new(Guid.NewGuid(), null, null);

    private readonly AsyncServiceScope _scope = fixture.Factory.Services.CreateAsyncScope();

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    [Fact]
    public async Task Save_TheTicketStatusSavedTwice_ShowsTheEarlierStatusFirst()
    {
        Outcome outcome = await RunAsync(All.Single(c => c.Name == "ticket status saved twice"));

        outcome.Shown.Should().NotBeEmpty();
        outcome.Shown[0].Should().Be(outcome.Seeded[0]);
    }

    [Theory]
    [InlineData("status saved as current status")]
    [InlineData("status saved as state")]
    public async Task Save_TheStatusUnderAnotherAttribute_ShowsTheEarlierStatus(string name)
    {
        Outcome outcome = await RunAsync(All.Single(c => c.Name == name));

        outcome.Shown.Should().Contain(outcome.Seeded[0]);
    }

    [Fact]
    public async Task Save_EveryLabelledCase_MeetsThePassBar()
    {
        List<(MatchCase Case, Outcome Outcome)> results = [];
        foreach (MatchCase c in All)
            results.Add((c, await RunAsync(c)));

        foreach (Family family in Enum.GetValues<Family>())
        {
            var cases = results.Where(r => r.Case.Family == family).ToList();
            output.WriteLine($"{family}: {cases.Count(r => r.Outcome.Passed)}/{cases.Count}");
            foreach ((MatchCase c, Outcome o) in cases.Where(r => !r.Outcome.Passed))
                output.WriteLine($"  miss: {c.Name} -> showed [{string.Join("; ", o.ShownText)}]");
        }

        results.Should().OnlyContain(r => r.Outcome.Shown.Count <= MemoryFactMatcher.MaxMatches);
        results.Should().OnlyContain(r => r.Outcome.SeededUnchanged, "a match never changes a fact");
        results.Where(r => r.Case.Family == Family.Same).Should()
            .OnlyContain(r => r.Outcome.Shown.Count > 0 && r.Outcome.Shown[0] == r.Outcome.Seeded[r.Case.Expected[0]], "a same-key fact is shown first");
        Rate(results, Family.Same, Family.RewordedAttribute, Family.SimilarSubject).Should().BeGreaterThanOrEqualTo(0.95);
        Rate(results, Family.Unrelated).Should().BeGreaterThanOrEqualTo(0.90);
    }

    private static double Rate(List<(MatchCase Case, Outcome Outcome)> results, params Family[] families)
    {
        var cases = results.Where(r => families.Contains(r.Case.Family)).ToList();
        return cases.Count(r => r.Outcome.Passed) / (double)cases.Count;
    }

    private sealed record Outcome(IReadOnlyList<Guid> Seeded, IReadOnlyList<Guid> Shown, IReadOnlyList<string> ShownText, bool Passed, bool SeededUnchanged);

    private async Task<Outcome> RunAsync(MatchCase c)
    {
        var factory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid space = Guid.NewGuid();
        await using (KnowledgeDbContext db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = space, Name = $"match-{space:N}", Kind = ContainerKind.Memory });
            await db.SaveChangesAsync();
        }
        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        foreach (Fact f in MemorySaveMatchCases.Background)
            await AddAsync(store, space, f);
        List<Guid> seeded = [];
        foreach (Fact f in c.Seeded)
            seeded.Add((await AddAsync(store, space, f)).Id);

        MemoryFact saved = await AddAsync(store, space, c.Saved);
        MemoryMatchSearch search = await store.MatchCandidatesAsync(saved, Saver, MemoryService.MaxMatchCandidates);
        IReadOnlyList<MemoryFactMatch> matches = MemoryFactMatcher.Match(saved, search.Saved, search.Candidates);

        List<Guid> shown = [.. matches.Select(m => m.Fact.Id)];
        bool passed = c.Expected.Length == 0
            ? shown.Count == 0
            : c.Expected.All(i => shown.Contains(seeded[i]));
        bool unchanged = true;
        foreach (Guid id in seeded)
            unchanged &= (await store.GetAsync(id)) is { } fact && fact.SupersededAt is null;
        return new Outcome(seeded, shown, [.. matches.Select(m => $"{m.Fact.Subject}/{m.Fact.Attribute}={m.Fact.Value} ({m.Kind})")], passed, unchanged);
    }

    private static Task<MemoryFact> AddAsync(PostgresMemoryStore store, Guid space, Fact f) =>
        store.AddAsync(space, new NewMemoryFact(f.Subject, f.Attribute, f.Value, null, null, [], null, MemoryScope.Team), Saver);
}
