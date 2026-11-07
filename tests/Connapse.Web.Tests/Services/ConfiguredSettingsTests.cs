using Connapse.Storage.Settings;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Web.Tests.Services;

[Trait("Category", "Unit")]
public class ConfiguredSettingsTests
{
    [Fact]
    public void DeploymentValue_ProviderRegistered_ReadsTheSameValuesTheProviderApplies()
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(new DatabaseSettingsProvider(_ => { }, key => $"deployment {key}"))
            .BuildServiceProvider();

        new ConfiguredSettings(services).DeploymentValue("k").Should().Be("deployment k");
    }

    [Fact]
    public void DeploymentValue_NoProviderRegistered_IsNull()
    {
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        new ConfiguredSettings(services).DeploymentValue("k").Should().BeNull();
    }
}
