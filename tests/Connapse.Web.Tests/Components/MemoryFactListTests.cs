using System.Security.Claims;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Memory;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The shared memory list, through the real memory operations over a fake memory service: what it lists a page at a
/// time, what it keeps in the address, what a fact's panel shows, and what correcting and deleting send.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryFactListTests : IDisposable
{
    private static readonly Guid Container = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly DateTimeOffset Jan = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly MemorySource Runbook = new(MemorySourceKind.Document, Guid.NewGuid().ToString());

    private readonly BunitContext ctx = new();
    private readonly FakeMemory memory = new();

    public MemoryFactListTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization().SetAuthorized("pat").SetClaims(new Claim(ClaimTypes.NameIdentifier, Me.ToString()));
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

    private NavigationManager Navigation => ctx.Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<MemoryFactList> Render(bool canEdit = true, string? address = null)
    {
        if (address is not null)
            Navigation.NavigateTo(address);
        return ctx.Render<MemoryFactList>(p => p.Add(t => t.Space, Container.ToString()).Add(t => t.CanEdit, canEdit));
    }

    private void AddMany(int count)
    {
        for (int i = 0; i < count; i++)
            memory.Add($"project {i:D3}", "database", $"value {i:D3}", recorded: Jan.AddMinutes(i));
    }

    [Fact]
    public void Render_WithFacts_ListsEachFactWithItsSourceAuthorAndDates()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15", from: Jan, sources: [Runbook]);
        memory.Add("Halcyon", "on-call", "Dana", sources: []);

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));
        cut.Markup.Should().Contain("PostgreSQL 15").And.Contain("Ops runbook").And.Contain("Jan 5, 2026");
        cut.Find("[data-fact-id] .no-sources").TextContent.Should().Be("No sources");
        cut.FindAll(".memory-row .col-author").Should().HaveCount(2);
        memory.Lists.Should().ContainSingle().Which.Status.Should().Be(MemoryFactStatus.Current);
    }

    [Fact]
    public void Render_NoFacts_ExplainsHowMemoryFills()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("#memory-empty").TextContent.Should().Contain("memory tools"));
    }

    [Fact]
    public void Paging_NextAndPrevious_WalkThePagesWithTheCursorInTheAddress()
    {
        AddMany(120);
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("1–50 of 120 facts you can see"));
        cut.FindAll(".memory-row").Should().HaveCount(50);
        cut.Find("#memory-prev").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#memory-next").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("51–100 of 120"));
        Navigation.Uri.Should().Contain("after=").And.Contain("page=2");
        cut.Find(".memory-row .fact-value").TextContent.Should().Be("value 069", "the newest recorded come first");

        cut.Find("#memory-next").Click();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(20));
        cut.Find("#memory-range").TextContent.Should().Contain("101–120 of 120");
        cut.Find("#memory-next").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#memory-prev").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("51–100 of 120"));
        cut.Find("#memory-prev").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("1–50 of 120"));
        Navigation.Uri.Should().NotContain("after=");
        memory.Counts.Should().ContainSingle("paging keeps the count of the same filter");
    }

    [Fact]
    public void Count_ShowsAfterThePage()
    {
        AddMany(3);
        memory.HoldCounts();

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(3));
        cut.Find("#memory-range").TextContent.Should().Contain("1–3 of …");
        memory.ReleaseCounts();
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("1–3 of 3 facts you can see"));
    }

    [Fact]
    public void Address_OpensTheViewItNames()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        memory.Add("Halcyon", "on-call", "Dana");

        var cut = Render(address: $"http://localhost/containers/x?tab=memory&q=postgres&status=all&cites=none&author={Me}&from=2026-01-01&to=2026-02-01&sort=subject");

        cut.WaitForAssertion(() => memory.Lists.Should().ContainSingle());
        memory.Lists[0].Should().Be(new MemoryFactFilter("postgres", null, Me, MemoryCites.None,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 2, 1, 23, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            MemoryFactStatus.All));
        memory.Sorts[0].Should().Be(MemoryFactSort.Subject);
        cut.Find("#memory-search").GetAttribute("value").Should().Be("postgres");
        cut.Find("#memory-filter-author").TextContent.Should().Contain("Me");
    }

    [Fact]
    public void Address_StaleCursor_StartsTheViewFromItsFirstPage()
    {
        AddMany(3);

        var cut = Render(address: "http://localhost/containers/x?tab=memory&status=all&after=not-a-cursor&page=4");

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(3));
        Navigation.Uri.Should().NotContain("after=").And.NotContain("page=").And.Contain("status=all").And.Contain("tab=memory");
        cut.FindAll(".alert-danger").Should().BeEmpty();
    }

    [Fact]
    public void Filters_EachChoice_GoesToTheAddressAndTheList()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15", sources: [Runbook]);
        memory.Add("Atlas", "database", "MySQL");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));

        cut.Find("#memory-filter-sources").Click();
        cut.Find("#memory-cites-some").Click();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        Navigation.Uri.Should().Contain("cites=some");
        memory.Lists[^1].Cites.Should().Be(MemoryCites.Some);

        cut.Find("#memory-filter-subject").Click();
        cut.WaitForAssertion(() => cut.FindAll(".memory-subject-option").Should().HaveCount(2));
        cut.Find("#memory-subject-find").Input("halc");
        cut.WaitForAssertion(() => cut.FindAll(".memory-subject-option").Should().ContainSingle());
        cut.Find(".memory-subject-option").TextContent.Should().Contain("Halcyon").And.Contain("1");
        cut.Find(".memory-subject-option").Click();
        cut.WaitForAssertion(() => memory.Lists[^1].Subject.Should().Be("Halcyon"));
        Navigation.Uri.Should().Contain("subject=Halcyon");

        cut.Find("#memory-filter-recorded").Click();
        cut.Find("#memory-recorded-from").Change("2026-01-01");
        cut.Find("#memory-recorded-apply").Click();
        cut.WaitForAssertion(() => Navigation.Uri.Should().Contain("from=2026-01-01"));

        cut.Find("#memory-sort").Change(nameof(MemoryFactSort.ValidFrom));
        cut.WaitForAssertion(() => memory.Sorts[^1].Should().Be(MemoryFactSort.ValidFrom));
        Navigation.Uri.Should().Contain("sort=validFrom").And.Contain("cites=some").And.Contain("subject=Halcyon");
    }

    [Fact]
    public void AuthorFilter_PicksAnAuthorOnThePage_AndNamesThem()
    {
        var sam = new MemoryCaller(Guid.NewGuid(), null, "Sam");
        memory.Add("Halcyon", "database", "PostgreSQL 15", author: sam);
        memory.Add("Halcyon", "on-call", "Dana");
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));

        cut.Find("#memory-filter-author").Click();
        cut.FindAll(".memory-author-option").Single(o => o.TextContent.Contains("Sam")).Click();

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        memory.Lists[^1].Author.Should().Be(sam.UserId);
        cut.Find("#memory-filter-author").TextContent.Should().Contain("Author: Sam");
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
        memory.Lists[^1].Text.Should().Be("kafka");
        memory.Sorts[^1].Should().Be(MemoryFactSort.Relevance, "a search sorts by best match unless told otherwise");
        Navigation.Uri.Should().Contain("q=kafka");
        cut.Find("#memory-no-matches button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        Navigation.Uri.Should().NotContain("q=");
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
        memory.Lists[^1].Status.Should().Be(MemoryFactStatus.All);
        Navigation.Uri.Should().Contain("status=all");
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
        memory.Counts.Should().HaveCount(2, "a deletion counts again");
    }

    [Fact]
    public void Delete_OnlyFactOfALaterPage_StepsBackToAPageWithFacts()
    {
        AddMany(51);
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("1–50 of 51"));
        cut.Find("#memory-next").Click();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(1));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-delete"));

        cut.Find("#memory-delete").Click();
        cut.Find("#memory-delete-confirm").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("1–50 of 50"));
        Navigation.Uri.Should().NotContain("after=").And.NotContain("page=");
        cut.FindAll("#memory-empty").Should().BeEmpty();
    }

    [Fact]
    public void Count_FromBeforeADelete_DoesNotReplaceTheNewerCount()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        memory.Add("Atlas", "database", "MySQL");
        memory.HoldCounts();
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().HaveCount(2));
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-delete"));
        cut.Find("#memory-delete").Click();
        cut.Find("#memory-delete-confirm").Click();
        cut.WaitForAssertion(() => memory.Counts.Should().HaveCount(2));

        memory.ReleaseCount(1);
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("of 1 fact"));
        memory.ReleaseCount(0);
        cut.WaitForAssertion(() => memory.CountsAnswered.Should().Be(2));

        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("of 1 fact"));
        cut.Find("#memory-range").TextContent.Should().NotContain("of 2");
    }

    [Fact]
    public void Count_OfAFilterLeftAndReturnedTo_KeepsOnlyTheLatest()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15");
        memory.HoldCounts();
        var cut = Render();
        cut.WaitForAssertion(() => memory.Counts.Should().HaveCount(1));
        memory.Add("Atlas", "database", "MySQL");

        Navigation.NavigateTo("http://localhost/containers/x?tab=memory&status=all");
        cut.WaitForAssertion(() => memory.Counts.Should().HaveCount(2));
        Navigation.NavigateTo("http://localhost/containers/x?tab=memory");
        cut.WaitForAssertion(() => memory.Counts.Should().HaveCount(3));

        memory.ReleaseCount(2);
        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("of 2 facts"));
        memory.ReleaseCount(0);
        cut.WaitForAssertion(() => memory.CountsAnswered.Should().Be(2));

        cut.WaitForAssertion(() => cut.Find("#memory-range").TextContent.Should().Contain("of 2 facts"));
        memory.ReleaseCounts();
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
        memory.Counts.Should().BeEmpty("nothing is counted when the page wasn't read");
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
        cut.Find("#memory-search").Change("postgres");
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
        MemoryFactList.Relative(Jan.AddSeconds(-secondsAgo), now: Jan).Should().Be(expected);
    }

    /// <summary>Memory in a list, with the visibility the real service gives a reader who can see everything.</summary>
    private sealed class FakeMemory : IMemoryService
    {
        private readonly List<MemoryFact> facts = [];

        /// <summary>The filter of each page listed.</summary>
        public List<MemoryFactFilter> Lists { get; } = [];

        /// <summary>The sort of each page listed.</summary>
        public List<MemoryFactSort> Sorts { get; } = [];

        /// <summary>The filter of each count.</summary>
        public List<MemoryFactFilter> Counts { get; } = [];

        public List<(Guid FactId, MemoryCorrection Kind, NewMemoryFact Replacement)> Corrections { get; } = [];

        public List<Guid> Forgotten { get; } = [];

        public bool DenyReads { get; set; }

        public string? RefuseCorrections { get; set; }

        private int countsAnswered;

        /// <summary>How many counts have answered.</summary>
        public int CountsAnswered => Volatile.Read(ref countsAnswered);

        private bool holdingCounts;
        private readonly List<TaskCompletionSource> countGates = [];

        /// <summary>Counts wait until released, each answering with the total when it started.</summary>
        public void HoldCounts() => holdingCounts = true;

        public void ReleaseCounts()
        {
            holdingCounts = false;
            foreach (var gate in countGates)
                gate.TrySetResult();
        }

        /// <summary>Releases the count started <paramref name="index"/>th, from 0.</summary>
        public void ReleaseCount(int index) => countGates[index].SetResult();

        public MemoryFact Add(
            string subject, string attribute, string value, DateTimeOffset? from = null, IReadOnlyList<MemorySource>? sources = null,
            DateTimeOffset? recorded = null, MemoryCaller? author = null)
        {
            Guid id = Guid.NewGuid();
            var fact = new MemoryFact(id, id, Container, subject, attribute, value, from, null, recorded ?? DateTimeOffset.UtcNow.AddDays(-2),
                null, null, null, author ?? new MemoryCaller(Guid.NewGuid(), Guid.NewGuid(), "pat"), sources ?? []);
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
            throw new NotSupportedException("The memory list doesn't write new facts.");

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

        private static RecalledFact Shown(MemoryFact f) => new(f,
            [.. f.Sources.Select(s => new ShownMemorySource(s.Kind, s.Ref, s == Runbook ? "Ops runbook" : null))]);

        private IEnumerable<MemoryFact> Matching(MemoryFactFilter filter) => facts.Where(f =>
            !Hidden.Contains(f.Id)
            && (filter.Status == MemoryFactStatus.All || (f.SupersededAt is null && f.ValidTo is null))
            && (filter.Text is null || f.Value.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)
                || f.Subject.Contains(filter.Text, StringComparison.OrdinalIgnoreCase))
            && (filter.Subject is null || string.Equals(f.Subject, filter.Subject, StringComparison.OrdinalIgnoreCase))
            && (filter.Author is null || f.Author.UserId == filter.Author || f.Author.AgentId == filter.Author)
            && (filter.Cites == MemoryCites.Any || (filter.Cites == MemoryCites.Some) == (f.Sources.Count > 0))
            && (filter.From is null || f.RecordedAt >= filter.From)
            && (filter.To is null || f.RecordedAt <= filter.To));

        public Task<MemoryRecall> RecallAsync(MemoryQuery query, MemoryCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException("The memory list pages through list, not recall.");

        public Task<MemoryFactPage> ListAsync(
            Guid containerId, MemoryFactFilter filter, MemoryFactSort sort, MemoryFactKey? after, int limit, MemoryCaller caller,
            CancellationToken ct = default)
        {
            Lists.Add(filter);
            Sorts.Add(sort);
            if (DenyReads)
                return Task.FromResult(MemoryFactPage.DeniedResult);
            // Newest recorded first, whatever the sort: the fake's order only has to be stable for its cursor.
            IEnumerable<MemoryFact> found = Matching(filter).OrderByDescending(f => f.RecordedAt).ThenByDescending(f => f.Id);
            if (after is { Recorded: { } recorded })
                found = found.SkipWhile(f => f.RecordedAt > recorded || (f.RecordedAt == recorded && f.Id.CompareTo(after.Id) >= 0));
            List<MemoryFact> rest = [.. found.Take(limit + 1)];
            List<MemoryFact> page = [.. rest.Take(limit)];
            return Task.FromResult(new MemoryFactPage([.. page.Select(Shown)],
                rest.Count > limit ? new MemoryFactKey(page[^1].Id, page[^1].RecordedAt) : null));
        }

        public async Task<MemoryFactCount> CountAsync(Guid containerId, MemoryFactFilter filter, MemoryCaller caller, CancellationToken ct = default)
        {
            Counts.Add(filter);
            int total = Matching(filter).Count();
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            countGates.Add(gate);
            if (!holdingCounts)
                gate.SetResult();
            await gate.Task;
            Interlocked.Increment(ref countsAnswered);
            return new MemoryFactCount(total, false);
        }

        public Task<MemorySubjectPage> SubjectsAsync(
            Guid containerId, string? text, MemoryFactStatus status, string? after, int limit, MemoryCaller caller,
            CancellationToken ct = default) =>
            Task.FromResult(new MemorySubjectPage([.. Matching(new MemoryFactFilter(Status: status))
                .Where(f => text is null || f.Subject.Contains(text, StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.Subject.ToLowerInvariant())
                .OrderBy(g => g.Key)
                .Take(limit)
                .Select(g => new MemorySubject(g.First().Subject, g.Count()))], null));

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
            return new MemoryHistoryPage([.. page.Select(Shown)], chain.Count, next);
        }
    }
}
