using System.Security.Claims;
using Bunit;
using Connapse.Application.Extensions;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Tests.Support;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// CON-249: a memory space's own page through the real container, space and memory operations: who can see the space,
/// a notice and no edit controls for someone who can't add to it, and the paged list of its facts.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Medium")]
public class MemorySpacePageTests : IDisposable
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly DateTime Jan = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitContext ctx = new();
    private readonly Action<string> setRole;
    private readonly IContainerStore containers = ContainerStoreFakes.Create();
    private readonly Container team = Space("on-call", ContainerKind.Memory, MemoryScope.Team, "Who to page");
    private readonly Container mine = Space("my-memory", ContainerKind.Memory, MemoryScope.Personal);
    private readonly FakeMemory memory;
    private bool writer = true;

    public MemorySpacePageTests()
    {
        memory = new FakeMemory(Guid.Parse(team.Id));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var auth = ctx.AddAuthorization();
        setRole = role => auth.SetRoles(role);
        auth.SetAuthorized("pat").SetClaims(new Claim(ClaimTypes.NameIdentifier, Me.ToString()));
        foreach (Container space in new[] { team, mine })
        {
            containers.GetAsync(Guid.Parse(space.Id), Arg.Any<CancellationToken>()).Returns(space);
            containers.GetByNameAsync(space.Name, Arg.Any<CancellationToken>()).Returns(space);
        }
        containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([team, mine]);
        IOperationAuthorizer authorizer = Substitute.For<IOperationAuthorizer>();
        authorizer.AllowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>())
            .Returns(call => writer || call.ArgAt<string>(1) is not (Policies.Writer or Policies.KnowledgeWrite));
        IUserDirectory people = Substitute.For<IUserDirectory>();
        people.ListAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => new DirectoryPage([], 7));
        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(authorizer);
        ctx.Services.AddSingleton(people);
        ctx.Services.AddSingleton<IMemoryService>(memory);
        ctx.Services.AddSingleton(Substitute.For<IMemorySpaceStore>());
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddApplication();
        ctx.Services.AddSingleton(Substitute.For<Connapse.Application.Memory.IDocumentPassages>());
    }

    public void Dispose() => ctx.Dispose();

    private NavigationManager Navigation => ctx.Services.GetRequiredService<NavigationManager>();

    private static Container Space(string name, ContainerKind kind, MemoryScope audience, string? description = null) =>
        new(Guid.NewGuid().ToString(), name, description, Jan, Jan, Kind: kind, Audience: audience);

    private IRenderedComponent<MemorySpacePage> Render(string space)
    {
        Navigation.NavigateTo($"http://localhost/memory/{space}");
        return ctx.Render<MemorySpacePage>(p => p.Add(c => c.Space, space));
    }

    [Fact]
    public void Render_EditorOnATeamSpace_ShowsWhoSeesItAndTheEditControls()
    {
        memory.Add("Halcyon", "database", "PostgreSQL 15", sources: [FakeMemory.Runbook], scope: MemoryScope.Team);

        var cut = Render(team.Id);

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().ContainSingle());
        cut.Find("#memory-space-name").TextContent.Should().Be("on-call");
        cut.Find("#memory-space-audience").TextContent.Should().Contain("Team").And.Contain("(7 people) can search it");
        cut.Markup.Should().Contain("Who to page");
        cut.FindAll("#memory-readonly-notice").Should().BeEmpty();
        cut.FindAll(".memory-row .col-author").Should().ContainSingle("a team space shows who wrote each fact");
        cut.FindAll("[data-copy-fact]").Should().BeEmpty("a team space's facts are already shared");
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-correct"));
        cut.FindAll("#memory-delete").Should().ContainSingle();
    }

    [Fact]
    public void Render_Viewer_ShowsTheNoticeAndNoEditControls()
    {
        writer = false;
        setRole("Viewer");
        memory.Add("Halcyon", "database", "PostgreSQL 15", sources: [FakeMemory.Runbook], scope: MemoryScope.Team);

        var cut = Render(team.Name);

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().ContainSingle());
        cut.Find("#memory-readonly-notice").TextContent.Should().Contain("You're a Viewer").And.Contain("Ask an admin for the Editor role");
        cut.FindAll("[data-copy-fact]").Should().BeEmpty();
        cut.Find(".memory-row").Click();
        cut.WaitForAssertion(() => cut.Find(".fact-hero-value"));
        cut.FindAll("#memory-correct").Should().BeEmpty();
        cut.FindAll("#memory-delete").Should().BeEmpty();
    }

    [Fact]
    public void Render_SignInThatCannotWrite_SaysWhyWithoutTheRole()
    {
        writer = false;

        var cut = Render(team.Id);

        cut.WaitForAssertion(() => cut.Find("#memory-readonly-notice").TextContent.Should().Contain("your sign-in can't save memory"));
    }

    [Fact]
    public void Render_PersonalSpace_HidesTheAuthorAndOffersCopyToTeam()
    {
        memory.Add("Halcyon", "on-call", "Dana", inSpace: Guid.Parse(mine.Id));

        var cut = Render(mine.Id);

        cut.WaitForAssertion(() => cut.FindAll(".memory-row").Should().ContainSingle());
        cut.Find("#memory-space-audience").TextContent.Should().Contain("Only you and the agents acting for you");
        cut.FindAll(".memory-table .col-author").Should().BeEmpty("every fact in a personal space is the person's own");
        cut.Find("[data-copy-fact]").TextContent.Should().Contain("Copy to team");
    }

    [Fact]
    public void CopyToTeam_PersonalFact_SavesItInTheTeamSpaceAndSaysWhoSeesIt()
    {
        memory.Add("Import job", "file size limit", "Fails over 2 GB", sources: [FakeMemory.Runbook], inSpace: Guid.Parse(mine.Id));
        var cut = Render(mine.Id);
        cut.WaitForAssertion(() => cut.Find("[data-copy-fact]"));

        cut.Find("[data-copy-fact]").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-copy-confirm").TextContent.Should().Contain("Copy to on-call"));
        cut.FindAll("#memory-copy-to option").Select(o => o.TextContent).Should().Equal(["on-call"], "only team spaces are offered");
        cut.Find("#memory-copy-who").TextContent.Should().Contain("Everyone in this Connapse (7 people) who can read")
            .And.Contain("Your personal fact stays in my-memory");
        cut.Find("#memory-copy-confirm").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-notice").TextContent.Should().Contain("Copied to on-call. Your personal fact is unchanged."));
        memory.Remembered.Should().ContainSingle().Which.Space.Should().Be(Guid.Parse(team.Id));
        cut.FindAll("#memory-copy").Should().BeEmpty();
    }

    [Fact]
    public void CopyToTeam_MatchingFactThere_ShowsItAndCopiesAnyway()
    {
        memory.Add("Halcyon", "on-call", "Dana", inSpace: Guid.Parse(mine.Id));
        memory.Add("Halcyon", "on-call", "Sam", sources: [FakeMemory.Runbook], scope: MemoryScope.Team);
        var cut = Render(mine.Id);
        cut.WaitForAssertion(() => cut.Find("[data-copy-fact]"));

        cut.Find("[data-copy-fact]").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-copy-existing").TextContent.Should().Contain("Sam"));
        cut.Find("#memory-copy-confirm").TextContent.Should().Contain("Copy anyway");
        cut.Find("#memory-copy-confirm").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-notice"));
        memory.Remembered.Should().ContainSingle();
    }

    [Fact]
    public void CopyToTeam_SaveRefused_ShowsWhyAndKeepsTheDialog()
    {
        memory.RefuseRemembers = "you can no longer read runbook.md";
        memory.Add("Halcyon", "on-call", "Dana", sources: [FakeMemory.Runbook], inSpace: Guid.Parse(mine.Id));
        var cut = Render(mine.Id);
        cut.WaitForAssertion(() => cut.Find(".memory-row").Click());
        cut.WaitForAssertion(() => cut.Find("#memory-copy-open"));

        cut.Find("#memory-copy-open").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-copy-confirm").HasAttribute("disabled").Should().BeFalse());
        cut.Find("#memory-copy-confirm").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-copy-error").TextContent.Should().Contain("runbook.md"));
        cut.FindAll("#memory-notice").Should().BeEmpty();
    }

    [Fact]
    public void CopyToTeam_FactCorrectedAfterTheListLoaded_ShowsAndCopiesTheCurrentVersion()
    {
        MemoryFact first = memory.Add("Import job", "file size limit", "Fails over 2 GB", inSpace: Guid.Parse(mine.Id));
        var cut = Render(mine.Id);
        cut.WaitForAssertion(() => cut.Find("[data-copy-fact]"));
        memory.Correct(first, MemoryCorrection.Wrong, "Fails over 4 GB");

        cut.Find("[data-copy-fact]").Click();

        cut.WaitForAssertion(() => cut.Find(".copy-fact").TextContent.Should().Contain("Fails over 4 GB").And.NotContain("2 GB"));
        cut.Find("#memory-copy-confirm").Click();
        cut.WaitForAssertion(() => cut.Find("#memory-notice"));
        memory.Remembered.Should().ContainSingle().Which.Fact.Value.Should().Be("Fails over 4 GB");
    }

    [Fact]
    public void CopyToTeam_FactCorrectedWhileTheDialogIsOpen_RefusesAndShowsTheNewVersion()
    {
        MemoryFact first = memory.Add("Import job", "file size limit", "Fails over 2 GB", inSpace: Guid.Parse(mine.Id));
        var cut = Render(mine.Id);
        cut.WaitForAssertion(() => cut.Find("[data-copy-fact]").Click());
        cut.WaitForAssertion(() => cut.Find(".copy-fact").TextContent.Should().Contain("2 GB"));
        memory.Correct(first, MemoryCorrection.Wrong, "Fails over 4 GB");

        cut.Find("#memory-copy-confirm").Click();

        cut.WaitForAssertion(() => cut.Find("#memory-copy-error").TextContent.Should().Contain("has changed since it was shown"));
        cut.Find(".copy-fact").TextContent.Should().Contain("Fails over 4 GB");
        memory.Remembered.Should().BeEmpty();
        cut.Find("#memory-copy-confirm").Click();
        cut.WaitForAssertion(() => memory.Remembered.Should().ContainSingle().Which.Fact.Value.Should().Be("Fails over 4 GB"));
    }

    [Theory]
    [InlineData(MemoryScope.Personal, "only the person who wrote it can see it")]
    [InlineData(MemoryScope.Team, "everyone who can search this space can see it")]
    public void Panel_FactWithNoSources_SaysWhoSeesItByItsScope(MemoryScope scope, string says)
    {
        Container space = scope == MemoryScope.Team ? team : mine;
        memory.Add("Halcyon", "on-call", "Dana", inSpace: Guid.Parse(space.Id), scope: scope);
        var cut = Render(space.Id);

        cut.WaitForAssertion(() => cut.Find(".memory-row").Click());
        cut.WaitForAssertion(() => cut.Find("#memory-sources-none").TextContent.Should().Contain(says));
        cut.Find("#memory-correct").Click();

        cut.Markup.Should().Contain(scope == MemoryScope.Team
            ? "cites no sources, so everyone who can search this space will see it"
            : "cites no sources, so only you will see it");
    }

    [Fact]
    public void Render_UnknownSpace_SaysItIsMissingOrHidden()
    {
        var cut = Render(Guid.NewGuid().ToString());

        cut.WaitForAssertion(() => cut.Find("#memory-space-missing").TextContent.Should().Contain("doesn't exist, or you can't see it"));
        cut.FindAll(".memory-table").Should().BeEmpty();
    }

    [Fact]
    public void Render_DocumentContainer_GoesToItsMemoryTab()
    {
        Container docs = Space("halcyon", ContainerKind.Documents, MemoryScope.Team);
        containers.GetAsync(Guid.Parse(docs.Id), Arg.Any<CancellationToken>()).Returns(docs);

        Render(docs.Id);

        Navigation.Uri.Should().Be($"http://localhost/containers/{docs.Id}?tab=memory");
    }
}
