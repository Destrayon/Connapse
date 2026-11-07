using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

[Trait("Category", "Unit")]
public class LocalModelBannerTests : BunitContext
{
    private readonly ILocalModelMonitor _monitor = Substitute.For<ILocalModelMonitor>();

    public LocalModelBannerTests() => Services.AddSingleton(_monitor);

    [Fact]
    public void Render_Downloading_SaysHowFarItHasGot()
    {
        _monitor.Current.Returns(new LocalModelStatus(LocalModelState.Downloading, "m", 42));

        Render<LocalModelBanner>().Markup.Should().Contain("42%");
    }

    [Fact]
    public void Render_Ready_ShowsNothing()
    {
        _monitor.Current.Returns(new LocalModelStatus(LocalModelState.Ready, "m"));

        Render<LocalModelBanner>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Changed_ModelBecomesReady_BannerGoesAway()
    {
        _monitor.Current.Returns(new LocalModelStatus(LocalModelState.Downloading, "m", 42));
        IRenderedComponent<LocalModelBanner> banner = Render<LocalModelBanner>();

        _monitor.Current.Returns(new LocalModelStatus(LocalModelState.Ready, "m"));
        _monitor.Changed += Raise.Event<Action>();

        banner.WaitForAssertion(() => banner.Markup.Trim().Should().BeEmpty());
    }
}
