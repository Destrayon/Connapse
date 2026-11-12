using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using Connapse.Storage.Memory;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests.Memory;

// A recall of one space, ranked by best match: rare words, partial words, month names and a subject's other facts.
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class MemorySearchTests(SharedWebAppFixture fixture) : IAsyncDisposable
{
    private static readonly MemoryCaller Nobody = new(null, null, null);

    private readonly AsyncServiceScope _scope = fixture.Factory.Services.CreateAsyncScope();

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    [Fact]
    public async Task Search_ARareWord_OutranksWordsEveryFactShares()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [.. Enumerable.Range(0, 10).Select(i => ("Halcyon", $"setting {i}", $"value {i}", $"HAL-{i + 10} done"))]);

        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "What did HAL-17 change in Halcyon?");

        found[0].Reason.Should().Be("HAL-17 done");
    }

    [Fact]
    public async Task Search_TheStartOfAWord_FindsTheWholeWord()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [("Halcyon", "database", "PostgreSQL 15", null), ("Halcyon", "cache", "Redis 7", null)]);

        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "Which Postgres version?");

        found.Should().NotBeEmpty();
        found[0].Value.Should().Be("PostgreSQL 15");
    }

    [Fact]
    public async Task Search_ALongerSpellingOfAWord_FindsItByTrigrams()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [("Halcyon", "database", "Postgres 15", null), ("Halcyon", "cache", "Redis 7", null)]);

        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "PostgreSQL");

        found.Should().NotBeEmpty();
        found[0].Value.Should().Be("Postgres 15");
    }

    [Fact]
    public async Task Search_AMonthName_FindsADateInThatMonth()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [("Halcyon", "launch date", "2027-01-19", null), ("Halcyon", "freeze date", "2027-03-02", null)]);

        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "launches in January");

        found[0].Value.Should().Be("2027-01-19");
        (await SearchAsync(store, space, "Mar 2027")).Select(f => f.Value).Should().Contain("2027-03-02");
    }

    [Fact]
    public async Task Search_WhatASubjectIs_BringsItsOtherFactsBelowTheMatch()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
        [
            ("Wicket", "kind", "SSO gateway", null),
            ("Wicket", "owner", "Dana Whitlock", null),
            ("Wicket", "deploy target", "eu-west-1", null),
            ("Halcyon", "owner", "Ravi Banerjee", null),
        ]);

        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "Who owns the SSO gateway?");

        found[0].Value.Should().Be("SSO gateway");
        found.Select(f => f.Value).Should().Contain(["Dana Whitlock", "eu-west-1"], "they are Wicket's other current facts");
        List<string> values = [.. found.Select(f => f.Value)];
        values.IndexOf("Ravi Banerjee").Should().BeGreaterThan(values.IndexOf("eu-west-1"),
            "Halcyon's owner matches \"owns\" alone, below everything Wicket's description brought");
    }

    [Fact]
    public async Task Coverage_ShownFactsHoldingOnlyNearWords_IsPartial_AndNamesTheMissingWord()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [("Hollis", "owner", "Dana Whitlock", null), ("Hollis", "database", "PostgreSQL 15", null)]);
        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "Hollis backup owner");

        MemoryWordCoverage coverage = await store.CoverageAsync(space, "Who is the Hollis backup owner?", [.. found.Select(f => f.Id)]);

        found.Should().NotBeEmpty("ranking still shows Hollis's facts");
        coverage.Partial.Should().BeTrue();
        coverage.Missing.Should().Equal("backup");
        coverage.Words.Should().NotContain(["who", "is", "the"], "stop words don't count");
    }

    [Fact]
    public async Task Coverage_OneShownFactHoldingEveryWordByStemOrPrefix_IsWhole()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [("Copperline", "code reviewer", "Ana Ruiz", null), ("Copperline", "database", "PostgreSQL 15", null)]);
        IReadOnlyList<MemoryFact> found = await SearchAsync(store, space, "Copperline code reviews Postgres");

        MemoryWordCoverage coverage = await store.CoverageAsync(space, "Copperline code reviews", [.. found.Select(f => f.Id)]);

        coverage.Partial.Should().BeFalse("\"reviews\" and \"reviewer\" share a stem");
        (await store.CoverageAsync(space, "Copperline Postgres", [.. found.Select(f => f.Id)])).Partial
            .Should().BeFalse("\"Postgres\" is the start of \"PostgreSQL\"");
    }

    [Fact]
    public async Task Coverage_WordsSpreadAcrossFacts_OrHeldOnlyByAFactNotShown_IsPartial()
    {
        (PostgresMemoryStore store, Guid space) = await SpaceAsync(
            [("Copperline", "owner", "Ana Ruiz", null), ("Lantern", "code reviewer", "Ravi Banerjee", null)]);
        IReadOnlyList<MemoryFact> all = await SearchAsync(store, space, "Copperline reviewer");

        MemoryWordCoverage spread = await store.CoverageAsync(space, "Copperline reviewer", [.. all.Select(f => f.Id)]);
        MemoryWordCoverage unshown = await store.CoverageAsync(space, "Copperline reviewer",
            [.. all.Where(f => f.Subject == "Copperline").Select(f => f.Id)]);

        spread.Partial.Should().BeTrue();
        spread.Missing.Should().BeEmpty();
        unshown.Missing.Should().Equal("reviewer");
    }

    private async Task<(PostgresMemoryStore Store, Guid Space)> SpaceAsync(
        IReadOnlyList<(string Subject, string Attribute, string Value, string? Reason)> facts)
    {
        var factory = _scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        Guid space = Guid.NewGuid();
        await using (KnowledgeDbContext db = await factory.CreateDbContextAsync())
        {
            db.Containers.Add(new ContainerEntity { Id = space, Name = $"search-{space:N}" });
            await db.SaveChangesAsync();
        }
        var store = new PostgresMemoryStore(factory, TimeProvider.System);
        foreach ((string subject, string attribute, string value, string? reason) in facts)
            await store.AddAsync(space, new NewMemoryFact(subject, attribute, value, null, null, [], reason, MemoryScope.Team), Nobody);
        return (store, space);
    }

    private static async Task<IReadOnlyList<MemoryFact>> SearchAsync(PostgresMemoryStore store, Guid space, string text) =>
        [.. (await store.PageAsync(space, new MemoryFactFilter(Text: text), MemoryFactSort.Relevance, null, 10,
            new MemoryViewer(Nobody, new HashSet<MemorySource>()))).Select(f => f.Fact)];
}
