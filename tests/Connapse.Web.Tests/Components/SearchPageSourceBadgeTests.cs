using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using SearchPage = Connapse.Web.Components.Pages.Search;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text.RegularExpressions;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// A result's file name is an identifier, so its badge shows it exactly as stored. The badge used
/// to carry <c>text-transform: capitalize</c>, which displayed README.md as README.Md (#751).
/// bUnit does not apply CSS, so the stylesheet is source-scanned alongside the markup check.
/// </summary>
[Trait("Category", "Unit")]
public class SearchPageSourceBadgeTests : IDisposable
{
    private static readonly Guid ContainerId = Guid.NewGuid();
    private readonly BunitContext ctx = new();

    public SearchPageSourceBadgeTests()
    {
        ctx.AddAuthorization().SetAuthorized("viewer");

        var containers = Substitute.For<IContainerStore>();
        var docs = new Container(ContainerId.ToString(), "docs", null, DateTime.UtcNow, DateTime.UtcNow);
        containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([docs]);
        containers.GetAsync(ContainerId, Arg.Any<CancellationToken>()).Returns(docs);
        var sources = Substitute.For<ISourceStore>();
        sources.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var search = Substitute.For<IKnowledgeSearch>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResult([new SearchHit("c1", "d1", "text", 1f, new() { ["fileName"] = "README.md" })], 1, TimeSpan.Zero));
        var settings = Substitute.For<IOptionsMonitor<SearchSettings>>();
        settings.CurrentValue.Returns(new SearchSettings { EnableCrossModelSearch = false });

        ctx.Services.AddSingleton(containers);
        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(search);
        var resolver = Substitute.For<ISearchScopeResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException());
        ctx.Services.AddSingleton(new PrivateSourceVisibility(resolver,
            Substitute.For<IAuthorizationService>(), Substitute.For<IConnectionStore>()));
        ctx.Services.AddSingleton(settings);
        ctx.Services.AddSingleton(Substitute.For<IOptionsMonitor<EmbeddingSettings>>());
        ctx.Services.AddSingleton(new VectorModelDiscovery(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance));
        ctx.Services.AddSourceOperations();
    }

    public void Dispose() => ctx.Dispose();

    [Fact]
    public void Result_FileNameBadge_RendersFileNameUnchanged()
    {
        var cut = ctx.Render<SearchPage>();
        cut.Find("select.form-select").Change(ContainerId.ToString());
        cut.Find("input.form-control-lg").Input("anything");
        cut.Find("button.btn-primary").Click();

        cut.WaitForAssertion(() => cut.Find(".source-badge").TextContent.Should().Be("README.md"));
    }

    [Fact]
    public void SourceBadgeStyle_DoesNotTransformTextCase()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Connapse.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test walks up to the solution file to find the stylesheet");
        string css = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "Connapse.Web", "Components", "Pages", "Search.razor.css"));

        Match rule = Regex.Match(css, @"\.source-badge\s*\{(?<body>[^}]*)\}");
        rule.Success.Should().BeTrue("Search.razor.css styles the file-name badge");
        rule.Groups["body"].Value.Should().NotContain("text-transform");
    }
}
