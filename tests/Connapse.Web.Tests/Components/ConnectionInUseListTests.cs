using Bunit;
using Connapse.Core;
using Connapse.Web.Components.Connections;
using Connapse.Web.Services;
using FluentAssertions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// A connection that sources still use can't be deleted; the refusal names each of those sources,
/// on the page with a link to each, and over REST with the same sentence.
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionInUseListTests : IDisposable
{
    private readonly BunitContext ctx = new();

    public void Dispose() => ctx.Dispose();

    private static Source SourceNamed(string name) =>
        new(Guid.NewGuid(), name, null, Guid.NewGuid(), "{}", DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void Render_NamesEverySource_EachLinkingToItsOwnPage()
    {
        Source[] blocking = [SourceNamed("acme / Engineering"), SourceNamed("acme / Operations")];

        var cut = ctx.Render<ConnectionInUseList>(p => p
            .Add(x => x.ConnectionName, "acme.atlassian.net")
            .Add(x => x.Sources, blocking)
            .Add(x => x.Id, "blocked"));

        var links = cut.FindAll("#blocked li a");
        links.Select(a => a.TextContent).Should().Equal("acme / Engineering", "acme / Operations");
        links.Select(a => a.GetAttribute("href")).Should().Equal(blocking.Select(s => $"/sources/{s.Id}"));
        cut.Find("#blocked-text").TextContent.Should().Contain("acme.atlassian.net").And.Contain("these 2 sources use");
        cut.Find("#blocked").GetAttribute("aria-labelledby").Should().Be("blocked-text");
    }

    [Fact]
    public void Render_OneSource_IsWordedForOne()
    {
        var cut = ctx.Render<ConnectionInUseList>(p => p
            .Add(x => x.ConnectionName, "site")
            .Add(x => x.Sources, new[] { SourceNamed("only") }));

        cut.Markup.Should().Contain("this source uses").And.Contain("Delete it on the");
    }

    [Fact]
    public void InUseMessage_NamesTheSources_NotJustACount()
    {
        ConnectionAdminService.InUseMessage("acme.atlassian.net", [SourceNamed("A"), SourceNamed("B")])
            .Should().Be("'acme.atlassian.net' can't be deleted while 2 sources use it: A, B. Delete those sources first.");
        ConnectionAdminService.InUseMessage("site", [SourceNamed("A")])
            .Should().Be("'site' can't be deleted while 1 source uses it: A. Delete that source first.");
    }

    [Fact]
    public void ConnectionsPage_RefusesWithTheNamedListInADialog_NotACountInAnAlert()
    {
        string markup = File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(), "src", "Connapse.Web", "Components", "Pages", "Connections.razor"));

        markup.Should().Contain("<ConnectionInUseList Id=\"delete-blocked-sources\"")
            .And.Contain("await SourceStore.ListByConnectionAsync(connection.Id)")
            .And.Contain("blockedDelete = new BlockedDelete(connection, await SourceStore.ListByConnectionAsync(connection.Id))")
            .And.Contain("HoldDialogFocusAsync(\"delete-blocked-dialog\"")
            .And.NotContain("source(s) using it");
    }
}
