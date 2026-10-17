using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SourceEditPage = Connapse.Web.Components.Pages.SourceEdit;

namespace Connapse.Web.Tests.Components;

/// <summary>A custom source's edit page holds no token: it says where the token it reads with is replaced.</summary>
[Trait("Category", "Unit")]
public class SourceEditCustomTokenTests : IDisposable
{
    private readonly BunitContext ctx = new();
    private readonly ISourceStore sources = Substitute.For<ISourceStore>();
    private readonly IConnectionStore connections = Substitute.For<IConnectionStore>();

    public SourceEditCustomTokenTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(sources);
        ctx.Services.AddSingleton(connections);
        ctx.Services.AddSingleton(Substitute.For<IConnectorFactory>());
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddLogging();
    }

    public void Dispose() => ctx.Dispose();

    private IRenderedComponent<SourceEditPage> RenderOn(string connectionConfig)
    {
        Connection connection = new(Guid.NewGuid(), "Acme bridge", ConnectionProvider.Custom, connectionConfig, null,
            DateTime.UtcNow, DateTime.UtcNow, HasSecret: true);
        Source source = new(Guid.NewGuid(), "eng", null, connection.Id,
            new CustomSourceScope("https://bridge.acme.test", "eng").ToJson(), DateTime.UtcNow, DateTime.UtcNow);
        sources.GetAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        connections.GetAsync(connection.Id, Arg.Any<CancellationToken>()).Returns(connection);
        return ctx.Render<SourceEditPage>(p => p.Add(x => x.Id, source.Id));
    }

    [Fact]
    public void ContentOnlyConnection_PointsAtTheConnectionToReplaceTheToken()
    {
        var cut = RenderOn(CustomConnectionConfig.ForMiddleMan("https://bridge.acme.test"));

        cut.WaitForAssertion(() => cut.Find("#source-token-where a").GetAttribute("href").Should().StartWith("/connections?edit="));
        cut.FindAll("#source-token, #source-token-replace").Should().BeEmpty("a custom source holds no token of its own");
    }

    [Fact]
    public void ProviderConnection_PointsAtTheProviderToReplaceTheToken()
    {
        Guid providerId = Guid.NewGuid();
        var cut = RenderOn(CustomConnectionConfig.ToJson(providerId));

        cut.WaitForAssertion(() => cut.Find("#source-token-where a").GetAttribute("href").Should().Be($"/admin/providers/custom/{providerId}"));
    }
}
