using System.Security.Claims;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Memory;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Tests.Support;
using Connapse.Web.Components.Memory;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// CON-248: the Memory page through the real space operations over a fake space store: cards up to nine spaces, the
/// paged table past that with find, kind filters, sort and stars kept in the address, no space made on a writer's
/// first visit (CON-291), and the New space dialog.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemoryPageTests : IDisposable
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly DateTimeOffset Jan = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly BunitContext ctx = new();
    private readonly FakeSpaces spaces = new();
    private int accounts = 7;
    private bool writer = true;

    public MemoryPageTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization().SetAuthorized("pat").SetClaims(new Claim(ClaimTypes.NameIdentifier, Me.ToString()));
        IContainerStore containers = ContainerStoreFakes.Create();
        containers.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => spaces.Find(call.ArgAt<Guid>(0)));
        containers.CreateAsync(Arg.Any<CreateContainerRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => spaces.Add(call.ArgAt<CreateContainerRequest>(0).Name, MemorySpaceKind.Team, 0,
                description: call.ArgAt<CreateContainerRequest>(0).Description));
        IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>())
            .Returns(call => writer || call.ArgAt<string>(1) is not (Policies.Writer or Policies.KnowledgeWrite));
        IUserDirectory people = Substitute.For<IUserDirectory>();
        people.ListAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => new DirectoryPage([], accounts));
        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton(people);
        ctx.Services.AddSingleton<IMemorySpaceStore>(spaces);
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddApplication();
    }

    public void Dispose() => ctx.Dispose();

    private NavigationManager Navigation => ctx.Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<MemoryPage> Render(string address = "http://localhost/memory")
    {
        Navigation.NavigateTo(address);
        return ctx.Render<MemoryPage>();
    }

    /// <summary>Team spaces team-000 onward, each a minute newer than the last.</summary>
    private void AddTeams(int count, int from = 0)
    {
        for (int i = from; i < from + count; i++)
            spaces.Add($"team-{i:D3}", MemorySpaceKind.Team, i, minutes: i);
    }

    /// <summary>A starred personal space the person made earlier; none is made for them (CON-291).</summary>
    private void HaveMine()
    {
        Container mine = spaces.Add("my-memory", MemorySpaceKind.Personal, 0);
        spaces.Stars.Add(mine.Id);
    }

    private static IEnumerable<string> Names(IRenderedComponent<MemoryPage> cut) =>
        cut.FindAll("tbody tr[data-space-id] .space-name a").Select(a => a.TextContent);

    [Fact]
    public void Render_NineOrFewerSpaces_ShowsCardsInTheirSections()
    {
        HaveMine();
        spaces.Add("on-call", MemorySpaceKind.Team, 4, description: "Who to page");

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll(".space-card").Should().HaveCount(2, "my-memory and on-call"));
        cut.Find("#memory-section-personal .space-card-name").TextContent.Should().Be("my-memory");
        cut.Find("#memory-section-team .space-card").TextContent.Should().Contain("Who to page").And.Contain("4 facts");
        cut.FindAll("#memory-section-containers").Should().BeEmpty("document containers hold no memory");
        cut.FindAll("#memory-spaces-find").Should().BeEmpty("the find box is the table's");
    }

    [Fact]
    public void Render_TenSpaces_ShowsThePagedTable()
    {
        HaveMine();
        AddTeams(9);

        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr[data-space-id]").Should().HaveCount(10));
        cut.FindAll(".space-card").Should().BeEmpty();
        cut.Find("#memory-spaces-range").TextContent.Should().Be("1–10 of 10 spaces");
        cut.Find("#memory-spaces-pill-all").TextContent.Should().Contain("10");
        cut.Find("#memory-spaces-pill-team").TextContent.Should().Contain("9");
        cut.Find("#memory-spaces-pill-starred").TextContent.Should().Contain("1", "my-memory is starred");
        Names(cut).First().Should().Be("my-memory", "starred spaces list first");
    }

    [Fact]
    public void FirstVisit_Writer_MakesNoSpace()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.FindAll("#memory-new-space").Should().ContainSingle());
        cut.FindAll(".space-card").Should().BeEmpty();
        spaces.Personal.Should().Be(0);
        spaces.Stars.Should().BeEmpty();
    }

    [Fact]
    public void Render_Viewer_NoNewSpaceButtonAndNoPersonalSpaceMade()
    {
        writer = false;

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("#memory-spaces-empty").TextContent.Should().Contain("once someone makes them"));
        cut.FindAll("#memory-new-space").Should().BeEmpty();
        spaces.Personal.Should().Be(0);
    }

    [Fact]
    public void Paging_NextAndPrevious_WalkThePagesWithTheCursorInTheAddress()
    {
        HaveMine();
        AddTeams(60);
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("#memory-spaces-range").TextContent.Should().Be("1–50 of 61 spaces"));
        cut.Find("#memory-spaces-prev").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#memory-spaces-next").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-spaces-range").TextContent.Should().Be("51–61 of 61 spaces"));
        Navigation.Uri.Should().Contain("after=").And.Contain("page=2");
        cut.Find("#memory-spaces-next").HasAttribute("disabled").Should().BeTrue();
        Names(cut).First().Should().Be("team-010", "newest first after the starred one");

        cut.Find("#memory-spaces-prev").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-spaces-range").TextContent.Should().Be("1–50 of 61 spaces"));
        Navigation.Uri.Should().NotContain("after=");
    }

    [Fact]
    public void Paging_NextClickedTwiceWhileLoading_MovesOnePage()
    {
        HaveMine();
        AddTeams(60);
        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("#memory-spaces-range").TextContent.Should().Be("1–50 of 61 spaces"));
        spaces.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        cut.Find("#memory-spaces-next").Click();
        cut.Find("#memory-spaces-next").HasAttribute("disabled").Should().BeTrue("a page is loading");
        cut.Find("#memory-spaces-prev").HasAttribute("disabled").Should().BeTrue("a page is loading");
        cut.Find("#memory-spaces-next").Click();
        spaces.Hold.SetResult();

        cut.WaitForAssertion(() => cut.Find("#memory-spaces-range").TextContent.Should().Be("51–61 of 61 spaces"));
        Navigation.Uri.Should().Contain("page=2");
    }

    [Fact]
    public void Find_NarrowsTheTableWithoutTurningItIntoCards()
    {
        HaveMine();
        AddTeams(12);
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr[data-space-id]").Should().HaveCount(13));

        cut.Find("#memory-spaces-find").Input("team-01");

        cut.WaitForAssertion(() => Names(cut).Should().Equal("team-011", "team-010"));
        Navigation.Uri.Should().Contain("q=team-01");
        cut.FindAll(".space-card").Should().BeEmpty("the unfiltered total decides between cards and the table");
        cut.Find("#memory-spaces-pill-all").TextContent.Should().Contain("2", "counts follow the find text");

        cut.Find("#memory-spaces-find").Input("nothing-like-this");
        cut.WaitForAssertion(() => cut.Find("#memory-spaces-no-matches").TextContent.Should().Contain("No spaces match"));
    }

    [Fact]
    public void KindPill_ListsThatKindAndKeepsItInTheAddress()
    {
        HaveMine();
        AddTeams(10);
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr[data-space-id]").Should().HaveCount(11));
        cut.FindAll("#memory-spaces-pill-container").Should().BeEmpty("document containers hold no memory");

        cut.Find("#memory-spaces-pill-personal").Click();

        cut.WaitForAssertion(() => Names(cut).Should().Equal("my-memory"));
        Navigation.Uri.Should().Contain("kind=personal");
        cut.Find("#memory-spaces-pill-personal").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find("#memory-spaces-pill-starred").Click();
        cut.WaitForAssertion(() => Names(cut).Should().Equal("my-memory"));
        Navigation.Uri.Should().Contain("starred=true").And.NotContain("kind=");
    }

    [Fact]
    public void Sort_ByName_ReordersAndKeepsItInTheAddress()
    {
        HaveMine();
        AddTeams(10);
        var cut = Render();
        cut.WaitForAssertion(() => Names(cut).Skip(1).First().Should().Be("team-009"));

        cut.Find("#memory-spaces-sort").Change("name");

        cut.WaitForAssertion(() => Names(cut).Skip(1).First().Should().Be("team-000"));
        Navigation.Uri.Should().Contain("sort=name");
    }

    [Fact]
    public void Star_ListsTheSpaceFirst()
    {
        HaveMine();
        AddTeams(10);
        var cut = Render();
        cut.WaitForAssertion(() => Names(cut).Should().HaveCount(11));

        cut.Find("tr[data-space-id] .star-toggle[aria-label='Star team-000']").Click();

        cut.WaitForAssertion(() => Names(cut).Take(2).Should().BeEquivalentTo("my-memory", "team-000"));
        spaces.Stars.Should().Contain(spaces.Named("team-000").Id);
    }

    [Fact]
    public void Address_OpensTheTableViewItNames()
    {
        AddTeams(10);

        var cut = Render("http://localhost/memory?kind=team&sort=name");

        cut.WaitForAssertion(() => Names(cut).Should().HaveCount(10).And.StartWith("team-000"));
        cut.Find("#memory-spaces-pill-team").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("#memory-spaces-sort").GetAttribute("value").Should().Be("name");
    }

    [Fact]
    public void Address_StaleCursor_OpensTheFirstPageOfTheSameView()
    {
        AddTeams(10);

        var cut = Render("http://localhost/memory?kind=team&after=not-a-cursor&page=3");

        cut.WaitForAssertion(() => cut.Find("#memory-spaces-range").TextContent.Should().Be("1–10 of 10 spaces"));
        Navigation.Uri.Should().Contain("kind=team").And.NotContain("after=");
    }

    [Fact]
    public void NewSpace_TeamWithOneAccount_SaysThePersonInTheSingular()
    {
        HaveMine();
        accounts = 1;
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".space-card").Should().ContainSingle());

        cut.Find("#memory-new-space").Click();
        cut.Find("#new-space-team").Change(true);

        cut.Find("#new-space-note").TextContent.Should().Contain("with the 1 person who has an account here");
    }

    [Fact]
    public void NewSpace_Team_SaysWhoSeesItAndListsItWhenMade()
    {
        HaveMine();
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".space-card").Should().ContainSingle());

        cut.Find("#memory-new-space").Click();
        cut.Find("#new-space-note").TextContent.Should().Contain("No one else sees it");
        cut.Find("#new-space-create").HasAttribute("disabled").Should().BeTrue("a space needs a name");
        cut.Find("#new-space-team").Change(true);
        cut.Find("#new-space-note").TextContent.Should().Contain("all 7 people").And.Contain("can't switch");
        cut.Find("#new-space-create").TextContent.Should().Contain("Create team space");
        cut.Find("#new-space-name").Input("on-call");
        cut.Find("#new-space-description").Input("Who to page");
        cut.Find("#new-space-create").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-spaces-notice").TextContent.Should().Contain("Made team space on-call"));
        cut.FindAll("#new-space-name").Should().BeEmpty("the dialog closes");
        cut.Find("#memory-section-team .space-card").TextContent.Should().Contain("on-call").And.Contain("Who to page");
    }

    [Fact]
    public void NewSpace_DuplicatePersonalName_MarksTheNameAndKeepsTheDialogOpen()
    {
        HaveMine();
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".space-card").Should().ContainSingle());

        cut.Find("#memory-new-space").Click();
        cut.Find("#new-space-name").Input("my-memory");
        cut.Find("#new-space-create").Click();

        cut.WaitForAssertion(() => cut.Find("#new-space-error").TextContent.Should().Contain("already have a personal space"));
        cut.Find("#new-space-name").ClassList.Should().Contain("is-invalid");
        spaces.Personal.Should().Be(1);
    }

    [Fact]
    public void NewSpace_InvalidName_ShowsTheNameRule()
    {
        HaveMine();
        var cut = Render();
        cut.WaitForAssertion(() => cut.FindAll(".space-card").Should().ContainSingle());

        cut.Find("#memory-new-space").Click();
        cut.Find("#new-space-name").Input("Not Valid!");
        cut.Find("#new-space-create").Click();

        cut.WaitForAssertion(() => cut.Find("#new-space-name").ClassList.Should().Contain("is-invalid"));
        cut.Find("#new-space-error").TextContent.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("http://localhost/memory", null, null, false, MemorySpaceSort.Updated, null, 1)]
    [InlineData("http://localhost/memory?q=%20ops%20&kind=team&sort=facts", "ops", MemorySpaceKind.Team, false, MemorySpaceSort.Facts, null, 1)]
    [InlineData("http://localhost/memory?starred=true&sort=name&after=abc&page=3", null, null, true, MemorySpaceSort.Name, "abc", 3)]
    [InlineData("http://localhost/memory?kind=personal&starred=true&page=4", null, MemorySpaceKind.Personal, false, MemorySpaceSort.Updated, null, 1)]
    [InlineData("http://localhost/memory?kind=nope&sort=nope&after=x&page=-2", null, null, false, MemorySpaceSort.Updated, "x", 1)]
    public void ViewParse_ReadsTheAddressAndDropsWhatDoesNotParse(
        string uri, string? text, MemorySpaceKind? kind, bool starred, MemorySpaceSort sort, string? cursor, int page)
    {
        MemorySpacesView view = MemorySpacesView.Parse(uri);

        view.Should().Be(new MemorySpacesView(text, kind, starred, sort, cursor, page));
    }

    [Fact]
    public void ViewAddress_RoundTripsThroughTheQuery()
    {
        MemorySpacesView view = new("ops", MemorySpaceKind.Team, false, MemorySpaceSort.Name, "abc", 2);
        string uri = Navigation.GetUriWithQueryParameters("http://localhost/memory", view.Address());

        MemorySpacesView.Parse(uri).Should().Be(view);
        view.Range(11, 61).Should().Be("51–61 of 61 spaces");
        new MemorySpacesView().Range(1, 1).Should().Be("1–1 of 1 space");
        new MemorySpacesView().Range(0, 0).Should().Be("0–0 of 0 spaces");
    }

    /// <summary>Spaces held in memory, every candidate settled so no tally is needed, with stars per space.</summary>
    private sealed class FakeSpaces : IMemorySpaceStore
    {
        private readonly List<(Container Container, int Facts, DateTimeOffset Updated)> all = [];

        public HashSet<string> Stars { get; } = [];

        public int Personal => all.Count(s => s.Container.Audience == MemoryScope.Personal);

        public Container Named(string name) => all.Single(s => s.Container.Name == name).Container;

        public Container? Find(Guid id) => all.FirstOrDefault(s => s.Container.Id == id.ToString()).Container;

        public Container Add(string name, MemorySpaceKind kind, int facts, int minutes = 0, string? description = null)
        {
            Container container = new(Guid.NewGuid().ToString(), name, description, Jan.UtcDateTime, Jan.UtcDateTime,
                Kind: ContainerKind.Memory,
                Audience: kind == MemorySpaceKind.Personal ? MemoryScope.Personal : MemoryScope.Team);
            lock (all)
                all.Add((container, facts, Jan.AddMinutes(minutes)));
            return container;
        }

        /// <summary>When set, every listing waits for it, to hold a page load open.</summary>
        public TaskCompletionSource? Hold { get; set; }

        public async Task<IReadOnlyList<MemorySpaceCandidate>> CandidatesAsync(MemoryCaller viewer, string? text, CancellationToken ct = default)
        {
            if (Hold is { } hold)
                await hold.Task;
            lock (all)
                return [.. all
                    .Where(s => text is null || s.Container.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (s.Container.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Select(s => new MemorySpaceCandidate(s.Container, Stars.Contains(s.Container.Id), s.Facts, s.Facts, s.Updated, s.Updated))];
        }

        public Task<IReadOnlyDictionary<string, MemorySpaceTally>?> TallyAsync(
            MemoryCaller viewer, IReadOnlyCollection<string> containerIds, CancellationToken ct = default) =>
            throw new InvalidOperationException("every candidate is settled");

        public Task<Container?> CreatePersonalAsync(MemoryCaller owner, string name, string? description, CancellationToken ct = default)
        {
            lock (all)
                if (all.Any(s => s.Container.Audience == MemoryScope.Personal && s.Container.Name == name))
                    return Task.FromResult<Container?>(null);
            return Task.FromResult<Container?>(Add(name, MemorySpaceKind.Personal, 0, description: description));
        }

        public Task StarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default)
        {
            lock (all)
                Stars.Add(containerId.ToString());
            return Task.CompletedTask;
        }

        public Task UnstarAsync(Guid containerId, MemoryCaller caller, CancellationToken ct = default)
        {
            lock (all)
                Stars.Remove(containerId.ToString());
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemorySpaceSummary>?> ListAsync(MemoryCaller viewer, string? text, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<MemorySpaceImpact?> ImpactAsync(Guid containerId, MemoryCaller viewer, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
