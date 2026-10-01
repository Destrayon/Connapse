using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using SearchPage = Connapse.Web.Components.Pages.Search;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// A Confluence space is listed to administrators only, yet a non-admin given its id can search it
/// from /search?scope=&lt;id&gt; — the search checks every hit — without the page naming it or
/// telling a hidden source apart from an id that does not exist.
/// </summary>
[Trait("Category", "Unit")]
public class SearchPageHiddenSourceTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IKnowledgeSearch search = Substitute.For<IKnowledgeSearch>();

    public SearchPageHiddenSourceTests()
    {
        ctx.AddAuthorization().SetAuthorized("viewer");

        var containers = Substitute.For<IContainerStore>();
        containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var resolver = Substitute.For<ISearchScopeResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException());
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult([new SearchHit("c1", "d1", "permitted text", 1f, new() { ["fileName"] = "permitted.md" })], 1, TimeSpan.Zero));
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings { EnableCrossModelSearch = false });

        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(search);
        ctx.Services.AddSingleton(new PrivateSourceVisibility(resolver, authorization, Substitute.For<IConnectionStore>()));
        ctx.Services.AddSingleton(settings);
        ctx.Services.AddSingleton(Substitute.For<IOptionsMonitor<EmbeddingSettings>>());
        ctx.Services.AddSingleton(new VectorModelDiscovery(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance));
    }

    public void Dispose() => ctx.Dispose();

    private Source Seed(string scopeJson)
    {
        var source = new Source(Guid.NewGuid(), "secret-space-name", "secret description", Guid.NewGuid(),
            scopeJson, DateTime.UtcNow, DateTime.UtcNow);
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([source]);
        sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        return source;
    }

    private IRenderedComponent<SearchPage> SearchFor(Guid scope)
    {
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"search?scope={scope}");
        var cut = ctx.Render<SearchPage>();
        cut.Find("input.form-control-lg").Input("anything");
        cut.Find("button.btn-primary").Click();
        return cut;
    }

    [Fact]
    public void ConfluenceSourceById_NonAdmin_SearchesItWithoutNamingIt()
    {
        var source = Seed("""{"kind":"confluence-space","spaceKey":"ENG"}""");

        var cut = SearchFor(source.Id);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("permitted.md"));
        cut.Markup.Should().Contain("Source from link").And.NotContain("secret-space-name");
        search.Received(1).SearchAsync("anything",
            Arg.Is<SearchOptions>(o => o.ContainerId == source.Id.ToString()), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MissingId_LooksLikeAHiddenSourceAndSearchesNothing()
    {
        Seed("""{"kind":"confluence-space","spaceKey":"ENG"}""");

        var cut = SearchFor(Guid.NewGuid());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No results found"));
        cut.Markup.Should().Contain("Source from link");
        search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default);
    }

    [Fact]
    public void HiddenPrivateGitHubSourceById_SearchesNothing()
    {
        var source = Seed("""{"owner":"acme","repo":"secret","private":true,"repoId":42}""");

        var cut = SearchFor(source.Id);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No results found"));
        cut.Markup.Should().NotContain("secret-space-name");
        search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default);
    }
}
