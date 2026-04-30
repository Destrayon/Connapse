using Connapse.Web.Services;
using FluentAssertions;

namespace Connapse.Web.Tests.Services;

public class DefaultProfileMenuProviderTests
{
    [Fact]
    public async Task GetItemsAsync_ReturnsProfileThenIntegrations()
    {
        var provider = new DefaultProfileMenuProvider();

        var items = await provider.GetItemsAsync();

        items.Should().HaveCount(2);
        items[0].Key.Should().Be("profile");
        items[0].Href.Should().Be("/profile");
        items[1].Key.Should().Be("integrations");
        items[1].Href.Should().Be("/profile/integrations");
    }

    [Fact]
    public void BackUrl_IsRoot()
    {
        var provider = new DefaultProfileMenuProvider();

        provider.BackUrl.Should().Be("/");
    }
}
