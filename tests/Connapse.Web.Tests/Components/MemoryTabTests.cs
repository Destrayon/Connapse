using System.Security.Claims;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Memory;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The container page's Memory tab, through the real memory operations over a fake memory service: what it lists,
/// what a fact's panel shows, and what correcting and deleting send.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryTabTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly DateTimeOffset Jan = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly MemorySource Runbook = new(MemorySourceKind.Document, Guid.NewGuid().ToString());

    private readonly BunitContext ctx = new();
    private readonly FakeMemory memory = new();

    public MemoryTabTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization().SetAuthorized("pat");
        var containers = ContainerStoreFakes.Create();
        containers.GetAsync(Container, Arg.Any<CancellationToken>())
            .Returns(new Container(Container.ToString(), "halcyon", null, DateTime.UtcNow, DateTime.UtcNow));
        var authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(default!, default!).ReturnsForAnyArgs(true);
        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton<IMemoryService>(memory);
        ctx.Services.AddApplication();
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<MemoryTab> Render(bool canEdit = true) =>
        ctx.Render<MemoryTab>(p => p.Add(t => t.ContainerId, Container.ToString()).Add(t => t.CanEdit, canEdit));

    [Fact]
    public void Render_WithFacts_ListsEachFactWithItsSourceAndDates()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15", from: Jan, sources: [Runbook]);
        memory.Add("Halcyon", "on-call", "Dana", sources: []);

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));
        cut.Markup.Should().Contain("PostgreSQL 15").And.Contain("Ops runbook").And.Contain("Jan 5, 2026");
        cut.Find("[data-fact-id] .only-you").TextContent.Should().Contain("Only you");
        memory.Queries.Should().ContainSingle().Which.History.Should().BeFalse();
    }

    [Fact]
    public void Render_NoFacts_ExplainsHowMemoryFills()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("#memory-empty").TextContent.Should().Contain("memory tools"));
    }

    [Fact]
    public void Search_NothingMatches_SaysSoAndClearingShowsEverythingAgain()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));

        cut.Find("#memory-search").Change("kafka");
        cut.Find("form.memory-search").Submit();

        cut.WaitForAssertion(() => cut.Find("#memory-no-matches").TextContent.Should().Contain("kafka"));
        memory.Queries[^1].Text.Should().Be("kafka");
        cut.Find("#memory-no-matches button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
    }

    [Fact]
    public void AllVersions_ShowsEndedAndCorrectedFactsLabelled()
    {
        var first = memory.Add("Halcyon", "database", "MySQL");
        memory.Correct(first, MemoryCorrection.Wrong, "PostgreSQL 14");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));

        cut.Find("#memory-filter-all").Click();

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));
        memory.Queries[^1].History.Should().BeTrue();
        cut.Find(".memory-row-old .fact-status").TextContent.Should().Be("Corrected");
    }

    [Fact]
    public void Select_CorrectedFact_ShowsItsHistoryWithWhatEachVersionReplaced()
    {
        var first = memory.Add("Halcyon", "database", "MySQL");
        var second = memory.Correct(first, MemoryCorrection.Wrong, "PostgreSQL 14", reason: "MySQL was only the prototype");
        memory.Correct(second, MemoryCorrection.Changed, "PostgreSQL 15", from: Jan.AddMonths(3));
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));

        cut.Find(".memory-row").Click();

        cut.WaitForAssertion(() => cut.FindAll("#memory-history .history-item").Should().HaveCount(3));
        memory.HistoryReads[^1].Should().NotBe(first.Id, "the history is read from the version the row shows");
        var items = cut.FindAll("#memory-history .history-item");
        items[0].TextContent.Should().Contain("PostgreSQL 15").And.Contain("Replaced “PostgreSQL 14”, which had ended");
        items[1].TextContent.Should().Contain("Replaced “MySQL”, which was wrong: MySQL was only the prototype");
        items[2].TextContent.Should().Contain("MySQL").And.NotContain("Replaced");
    }

    [Fact]
    public void Correct_ItChanged_KeepsTheOldSourcesAndStartsOnTheChosenDay()
    {
        var old = memory.Add("Halcyon", "database", "PostgreSQL 14", from: Jan, sources: [Runbook]);
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-correct"));

        cut.Find("#memory-correct").Click();
        cut.Find("#memory-edit-value").Input("PostgreSQL 15");
        cut.Find("#memory-edit-date").Change("2026-04-01");
        cut.Find("#memory-edit-reason").Change("Upgraded in April");
        cut.Find("#memory-edit-save").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-notice").TextContent.Should().Contain("kept in its history"));
        var (factId, kind, replacement) = memory.Corrections.Should().ContainSingle().Subject;
        factId.Should().Be(old.Id);
        kind.Should().Be(MemoryCorrection.Changed);
        replacement.Should().BeEquivalentTo(new NewMemoryFact("Halcyon", "database", "PostgreSQL 15",
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), null, [Runbook], "Upgraded in April"));
        cut.Find(".fact-hero-value").TextContent.Should().Be("PostgreSQL 15");
        cut.FindAll("#memory-history .history-item").Should().HaveCount(2);
    }

    [Fact]
    public void Correct_Refused_ShowsWhyAndStaysInTheEditor()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 14", from: Jan);
        memory.RefuseCorrections = "the new value must start after the old one (2026-01-05).";
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-correct"));

        cut.Find("#memory-correct").Click();
        cut.Find("#memory-edit-value").Input("PostgreSQL 15");
        cut.Find("#memory-edit-save").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-edit-error").TextContent.Should().Contain("must start after"));
        cut.Find("#memory-edit-value").Should().NotBeNull();
    }

    [Fact]
    public void Delete_ConfirmedNamesTheFactAndItsVersions_ThenForgetsTheChain()
    {
        var first = memory.Add("Halcyon", "database", "MySQL");
        memory.Correct(first, MemoryCorrection.Wrong, "PostgreSQL 14");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.FindAll("#memory-history .history-item").Should().HaveCount(2));

        cut.Find("#memory-delete").Click();

        var body = cut.Find("#memory-delete-body").TextContent;
        body.Should().Contain("PostgreSQL 14").And.Contain("its 1 earlier version").And.Contain("can't be undone")
            .And.Contain("activity log");
        cut.Find("#memory-delete-confirm").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-empty"));
        memory.Forgotten.Should().ContainSingle();
        cut.Find("#memory-notice").TextContent.Should().Contain("all 2 versions");
    }

    [Fact]
    public void Viewer_SeesFactsButNoWayToChangeThem()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        var cut = Render(canEdit: false);
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));

        cut.Find(".memory-row").Click();

        cut.WaitForAssertion(() => cut.Find(".fact-hero-value"));
        cut.FindAll("#memory-correct").Should().BeEmpty();
        cut.FindAll("#memory-delete").Should().BeEmpty();
    }

    [Fact]
    public void Render_AccessCannotBeChecked_ShowsWhyAndNoFacts()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        memory.DenyReads = true;

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find(".alert-danger").TextContent.Should().Contain("could not be checked"));
        cut.FindAll(".memory-row").Should().BeEmpty();
    }

    [Fact]
    public void Search_AfterTheOpenFactBecomesUnreadable_ClosesItsPanel()
    {
        var fact = memory.Add("Halcyon", "database", "PostgreSQL 15", sources: [Runbook]);
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-history-none"));

        memory.Hidden.Add(fact.Id);
        cut.Find("form.memory-search").Submit();

        cut.WaitForAssertion(() => cut.FindAll(".memory-panel").Should().BeEmpty());
        cut.Markup.Should().NotContain("PostgreSQL 15");
    }

    [Fact]
    public void Render_AccessCannotBeCheckedOnRefresh_ClosesTheOpenFact()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-history-none"));

        memory.DenyReads = true;
        cut.Find("#memory-filter-all").Click();

        cut.WaitForAssertion(() => cut.Find(".alert-danger").TextContent.Should().Contain("could not be checked"));
        cut.FindAll(".memory-panel").Should().BeEmpty();
        cut.Markup.Should().NotContain("PostgreSQL 15");
    }

    [Fact]
    public void Select_EarlierFactsHistoryArrivesLast_IsNotShownForTheLaterOne()
    {
        var slow = memory.Add("Halcyon", "database", "MySQL");
        memory.Correct(slow, MemoryCorrection.Wrong, "MariaDB");
        memory.Add("Halcyon", "on-call", "Dana");
        memory.HeldChain = slow.ChainId;
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));

        cut.FindAll(".memory-row").Single(r => r.TextContent.Contains("MariaDB")).Click();
        cut.FindAll(".memory-row").Single(r => r.TextContent.Contains("Dana")).Click();
        cut.WaitForAssertion(() => cut.Find("#memory-history-none"));
        memory.ReleaseHeldChain();

        cut.WaitForAssertion(() => cut.Find(".fact-hero-value").TextContent.Should().Be("Dana"));
        cut.FindAll("#memory-history").Should().BeEmpty();
        cut.Find("#memory-history-none").Should().NotBeNull();
    }

    [Fact]
    public void Correct_ChainLongerThanOnePage_PagesBackToEveryVersionAndCountsThemAll()
    {
        var latest = memory.Add("Halcyon", "build", "v0");
        for (int i = 1; i < 120; i++)
            latest = memory.Correct(latest, MemoryCorrection.Wrong, $"v{i}");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.FindAll("#memory-history .history-item").Should().HaveCount(100));
        cut.Find("#memory-history-older").TextContent.Should().Contain("20 more");

        cut.Find("#memory-history-older").Click();

        cut.WaitForAssertion(() => cut.FindAll("#memory-history .history-item").Should().HaveCount(120));
        cut.FindAll("#memory-history-older").Should().BeEmpty();
        cut.FindAll("#memory-history .history-item")[^1].TextContent.Should().Contain("v0");

        cut.Find("#memory-correct").Click();
        cut.Find("#memory-edit-value").Input("v120");
        cut.Find("#memory-kind-wrong").Change(MemoryCorrection.Wrong);
        cut.Find("#memory-edit-save").Click();

        cut.WaitForAssertion(() => cut.Find(".fact-hero-value").TextContent.Should().Be("v120"));
        cut.Find("#memory-delete").Click();
        cut.Find("#memory-delete-body").TextContent.Should().Contain("its 120 earlier versions");
    }

    [Fact]
    public void ShowOlderVersions_AfterOpeningAnotherVersion_PagesTheChainItWasAskedFor()
    {
        var latest = memory.Add("Halcyon", "build", "v0");
        for (int i = 1; i < 120; i++)
            latest = memory.Correct(latest, MemoryCorrection.Wrong, $"v{i}");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.FindAll("#memory-history .history-item").Should().HaveCount(100));
        cut.FindAll(".history-open")[^1].Click();
        cut.Find(".fact-hero-value").TextContent.Should().NotBe("v119");

        cut.Find("#memory-history-older").Click();

        cut.WaitForAssertion(() => cut.FindAll("#memory-history .history-item").Should().HaveCount(120));
        cut.FindAll(".alert-danger").Should().BeEmpty();
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(60 * 5, "5 minutes ago")]
    [InlineData(60 * 60, "1 hour ago")]
    [InlineData(60 * 60 * 24 * 3, "3 days ago")]
    [InlineData(60 * 60 * 24 * 40, "Nov 26, 2025")]
    public void Relative_UnderAMonthIsRelative_OlderIsTheDay(int secondsAgo, string expected)
    {
        MemoryTab.Relative(Jan.AddSeconds(-secondsAgo), now: Jan).Should().Be(expected);
    }

    /// <summary>Memory in a list, with the visibility the real service gives a reader who can see everything.</summary>
    private sealed class FakeMemory : IMemoryService
    {
        private readonly List<MemoryFact> facts = [];

        public List<MemoryQuery> Queries { get; } = [];

        public List<(Guid FactId, MemoryCorrection Kind, NewMemoryFact Replacement)> Corrections { get; } = [];

        public List<Guid> Forgotten { get; } = [];

        public bool DenyReads { get; set; }

        public string? RefuseCorrections { get; set; }

        public MemoryFact Add(string subject, string attribute, string value, DateTimeOffset? from = null, IReadOnlyList<MemorySource>? sources = null)
        {
            Guid id = Guid.NewGuid();
            var fact = new MemoryFact(id, id, Container, subject, attribute, value, from, null, DateTimeOffset.UtcNow.AddDays(-2),
                null, null, null, new MemoryCaller(Guid.NewGuid(), Guid.NewGuid(), "pat"), sources ?? []);
            facts.Add(fact);
            return fact;
        }

        public MemoryFact Correct(MemoryFact old, MemoryCorrection kind, string value, DateTimeOffset? from = null, string? reason = null)
        {
            facts.Remove(old);
            facts.Add(kind == MemoryCorrection.Changed ? old with { ValidTo = from } : old with { SupersededAt = DateTimeOffset.UtcNow });
            var fact = old with
            {
                Id = Guid.NewGuid(),
                Value = value,
                ValidFrom = kind == MemoryCorrection.Changed ? from : old.ValidFrom,
                ValidTo = null,
                SupersededAt = null,
                ReplacesId = old.Id,
                Reason = reason,
                RecordedAt = old.RecordedAt.AddMinutes(1),
            };
            facts.Add(fact);
            return fact;
        }

        public Task<MemoryWriteResult> RememberAsync(Guid containerId, NewMemoryFact fact, MemoryCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException("The Memory tab doesn't write new facts.");

        public Task<MemoryWriteResult> CorrectAsync(
            Guid factId, MemoryCorrection kind, NewMemoryFact replacement, MemoryCaller caller, bool teamWriter,
            CancellationToken ct = default)
        {
            Corrections.Add((factId, kind, replacement));
            if (RefuseCorrections is { } refusal)
                return Task.FromResult(MemoryWriteResult.Failed(refusal));
            MemoryFact old = facts.Single(f => f.Id == factId);
            MemoryFact written = Correct(old, kind, replacement.Value, replacement.ValidFrom, replacement.Reason);
            return Task.FromResult(new MemoryWriteResult(written, facts.Single(f => f.Id == factId), null));
        }

        public Task<(int Deleted, MemoryWriteFailure Failure)> ForgetAsync(
            Guid factId, MemoryCaller caller, bool teamWriter, CancellationToken ct = default)
        {
            Forgotten.Add(factId);
            Guid chain = facts.Single(f => f.Id == factId).ChainId;
            int deleted = facts.RemoveAll(f => f.ChainId == chain);
            return Task.FromResult((deleted, MemoryWriteFailure.None));
        }

        /// <summary>Facts the reader can no longer see, as when access to their sources is taken away.</summary>
        public HashSet<Guid> Hidden { get; } = [];

        /// <summary>History reads for this chain wait until <see cref="ReleaseHeldChain"/>.</summary>
        public Guid? HeldChain { get; set; }

        private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseHeldChain() => held.SetResult();

        public async Task<MemoryRecall> RecallAsync(MemoryQuery query, MemoryCaller caller, CancellationToken ct = default)
        {
            Queries.Add(query);
            if (query.ChainId is { } heldChain && heldChain == HeldChain)
                await held.Task;
            if (DenyReads)
                return MemoryRecall.DeniedResult;
            IEnumerable<MemoryFact> visible = facts.Where(f => !Hidden.Contains(f.Id));
            // A chain is read newest first, as the store reads it.
            IEnumerable<MemoryFact> found = query.ChainId is { } chain
                ? visible.Where(f => f.ChainId == chain).OrderByDescending(f => f.RecordedAt)
                : visible.Where(f => query.History || (f.SupersededAt is null && f.ValidTo is null)).OrderByDescending(f => f.RecordedAt);
            if (query.Text is { } text)
                found = found.Where(f => f.Value.Contains(text, StringComparison.OrdinalIgnoreCase));
            return new MemoryRecall([.. found.Take(query.Limit).Select(f => new RecalledFact(f,
                [.. f.Sources.Select(s => new ShownMemorySource(s.Kind, s.Ref, s == Runbook ? "Ops runbook" : null))]))]);
        }

        public Task<MemoryFactPage> ListAsync(
            Guid containerId, MemoryFactFilter filter, MemoryFactSort sort, MemoryFactKey? after, int limit, MemoryCaller caller,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The Memory tab lists facts through recall.");

        public Task<MemoryFactCount> CountAsync(Guid containerId, MemoryFactFilter filter, MemoryCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException("The Memory tab doesn't count facts.");

        public Task<MemorySubjectPage> SubjectsAsync(
            Guid containerId, string? text, MemoryFactStatus status, string? after, int limit, MemoryCaller caller,
            CancellationToken ct = default) =>
            throw new NotSupportedException("The Memory tab doesn't list subjects.");

        public Task<Guid?> SpaceOfAsync(Guid factId, CancellationToken ct = default) =>
            Task.FromResult(facts.Any(f => f.Id == factId) ? Container : (Guid?)null);

        /// <summary>The fact each history read asked about.</summary>
        public List<Guid> HistoryReads { get; } = [];

        public async Task<MemoryHistoryPage> HistoryAsync(
            Guid containerId, Guid factId, MemoryVersionKey? after, int limit, MemoryCaller caller, CancellationToken ct = default)
        {
            HistoryReads.Add(factId);
            if (facts.FirstOrDefault(f => f.Id == factId) is not { } of)
                return MemoryHistoryPage.Missing;
            if (of.ChainId == HeldChain)
                await held.Task;
            if (DenyReads)
                return MemoryHistoryPage.DeniedResult;
            if (Hidden.Contains(factId))
                return MemoryHistoryPage.Missing;
            // Newest first, as the store reads a chain.
            List<MemoryFact> chain = [.. facts.Where(f => f.ChainId == of.ChainId && !Hidden.Contains(f.Id))
                .OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id)];
            List<MemoryFact> rest = after is null
                ? chain
                : [.. chain.SkipWhile(f => f.RecordedAt > after.Recorded || (f.RecordedAt == after.Recorded && f.Id.CompareTo(after.Id) >= 0))];
            List<MemoryFact> page = [.. rest.Take(limit)];
            MemoryVersionKey? next = rest.Count > limit ? new MemoryVersionKey(page[^1].RecordedAt, page[^1].Id) : null;
            return new MemoryHistoryPage([.. page.Select(f => new RecalledFact(f,
                [.. f.Sources.Select(s => new ShownMemorySource(s.Kind, s.Ref, s == Runbook ? "Ops runbook" : null))]))], chain.Count, next);
        }
    }
}
