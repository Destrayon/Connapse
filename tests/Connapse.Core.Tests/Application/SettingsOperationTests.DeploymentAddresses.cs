using Connapse.Application.Settings;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Application;

/// <summary>
/// #399: an address the deployment set replaces the saved one in configuration, so what the API reads and compares
/// against has to be that address too, or it shows one address while another is in use.
/// </summary>
public partial class SettingsOperationTests
{
    private const string DeployedAddress = "http://ollama:11434";

    private static World WithDeployedAddress()
    {
        var world = new World();
        world.Configured.Deployment["Knowledge:embedding:BaseUrl"] = DeployedAddress;
        return world;
    }

    [Fact]
    public async Task Get_DeploymentSetTheAddress_ShowsTheDeploymentsAddress()
    {
        var world = WithDeployedAddress();
        await world.Store.SaveAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = "http://localhost:11434" });

        var read = await world.Get().RunAsync(Caller, "embedding");

        read.Result!.Settings.Should().BeOfType<EmbeddingSettings>().Which.BaseUrl.Should().Be(DeployedAddress);
    }

    [Fact]
    public async Task Get_DeploymentSetTheAddressOfAnotherProvider_ShowsTheSavedAddress()
    {
        var world = WithDeployedAddress();
        await world.Store.SaveAsync("embedding", new EmbeddingSettings { Provider = "OpenAI", BaseUrl = "https://proxy.example" });

        var read = await world.Get().RunAsync(Caller, "embedding");

        read.Result!.Settings.Should().BeOfType<EmbeddingSettings>().Which.BaseUrl.Should().Be("https://proxy.example");
    }

    [Fact]
    public async Task Update_WithTheVersionJustRead_IsNotRefused()
    {
        var world = WithDeployedAddress();
        await world.Store.SaveAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = "http://localhost:11434" });
        string read = (await world.Get().RunAsync(Caller, "embedding")).Result!.Version;

        var updated = await world.Update().RunAsync(Caller, "embedding", Body(new { provider = "Ollama", model = "m" }), expectedVersions: [read]);

        updated.Status.Should().Be(SettingsStatus.Success);
    }

    [Fact]
    public async Task Update_ThenGet_HaveTheSameVersion()
    {
        var world = WithDeployedAddress();

        var updated = await world.Update().RunAsync(Caller, "embedding", Body(new { provider = "Ollama", baseUrl = "http://localhost:11434" }));
        var read = await world.Get().RunAsync(Caller, "embedding");

        updated.Result!.Version.Should().Be(read.Result!.Version, "the version returned by a save is good for the next one");
    }

    [Fact]
    public async Task Update_SwitchingToAnotherProvider_ReturnsTheAddressItWasGiven()
    {
        var world = WithDeployedAddress();
        await world.Store.SaveAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = "http://localhost:11434" });

        await world.Update().RunAsync(Caller, "embedding", Body(new { provider = "OpenAI", baseUrl = "https://proxy.example" }));
        var read = await world.Get().RunAsync(Caller, "embedding");

        read.Result!.Settings.Should().BeOfType<EmbeddingSettings>().Which.BaseUrl.Should().Be("https://proxy.example");
    }

    [Fact]
    public async Task Update_DeploymentSetTheAddress_KeepsWhatWasSavedForThatAddress()
    {
        var world = WithDeployedAddress();

        await world.Update().RunAsync(Caller, "embedding", Body(new { provider = "Ollama", baseUrl = "http://saved:11434" }));

        world.Store.Stored<EmbeddingSettings>("embedding")!.BaseUrl.Should().Be("http://saved:11434", "the saved value returns if the deployment stops setting one");
    }

    [Fact]
    public async Task Get_NothingStored_NeverChangesTheConfiguredValue()
    {
        var world = WithDeployedAddress();
        var configured = new EmbeddingSettings { BaseUrl = "http://from-config:11434" };
        world.Configured.Values[typeof(EmbeddingSettings)] = configured;

        await world.Get().RunAsync(Caller, "embedding");

        configured.BaseUrl.Should().Be("http://from-config:11434", "the configured value is shared by every reader");
    }
}
