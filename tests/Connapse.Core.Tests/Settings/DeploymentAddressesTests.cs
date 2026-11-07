using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Settings;

[Trait("Category", "Unit")]
public class DeploymentAddressesTests
{
    private const string Compose = "http://compose:1234";

    private static string? Replacing(string key, string? savedProvider = null, string? deployment = Compose) =>
        DeploymentAddresses.Replacing(key, _ => savedProvider, _ => deployment);

    [Theory]
    [InlineData("Knowledge:embedding:BaseUrl")]
    [InlineData("Knowledge:embedding:baseUrl")]
    [InlineData("Knowledge:embedding:OpenAiBaseUrl")]
    [InlineData("Knowledge:embedding:AzureEndpoint")]
    [InlineData("Knowledge:llm:AnthropicBaseUrl")]
    [InlineData("Knowledge:search:crossEncoderBaseUrl")]
    public void Replacing_AnAddressTheDeploymentSet_IsTheDeploymentsValue(string key) =>
        Replacing(key).Should().Be(Compose);

    [Theory]
    [InlineData("Knowledge:embedding:Model")]
    [InlineData("Knowledge:embedding:Provider")]
    [InlineData("Knowledge:embedding:ApiKey")]
    [InlineData("Knowledge:search:Mode")]
    public void Replacing_AnythingButAnAddress_IsNull(string key) => Replacing(key).Should().BeNull();

    [Theory]
    [InlineData("Identity:SamlSignIn:AcsUrl")]
    [InlineData("Providers:Azure:Endpoint")]
    public void Replacing_AnAddressProviderSetupOwns_IsNull(string key) => Replacing(key).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Replacing_DeploymentSetNothing_IsNull(string? deployment) =>
        Replacing("Knowledge:embedding:BaseUrl", deployment: deployment).Should().BeNull();

    [Theory]
    [InlineData("Knowledge:embedding:BaseUrl", "Ollama")]
    [InlineData("Knowledge:embedding:BaseUrl", "ollama")]
    [InlineData("Knowledge:embedding:BaseUrl", "")]
    [InlineData("Knowledge:embedding:BaseUrl", null)]
    [InlineData("Knowledge:llm:BaseUrl", "Ollama")]
    [InlineData("Knowledge:search:CrossEncoderBaseUrl", "TEI")]
    public void Replacing_SharedAddressOfTheProviderItIsFor_IsTheDeploymentsValue(string key, string? savedProvider) =>
        Replacing(key, savedProvider).Should().Be(Compose);

    [Theory]
    [InlineData("Knowledge:embedding:BaseUrl", "OpenAI")]
    [InlineData("Knowledge:llm:BaseUrl", "Anthropic")]
    [InlineData("Knowledge:search:CrossEncoderBaseUrl", "AzureAIFoundry")]
    public void Replacing_SharedAddressOfAnotherProvider_IsNull(string key, string savedProvider) =>
        Replacing(key, savedProvider).Should().BeNull("a saved key must not follow the deployment's address to another provider");

    [Fact]
    public void Replacing_SharedAddress_AsksForThatCategorysProvider()
    {
        string? asked = null;

        DeploymentAddresses.Replacing("Knowledge:search:CrossEncoderBaseUrl", key => { asked = key; return null; }, _ => Compose);

        asked.Should().Be("Knowledge:search:CrossEncoderProvider");
    }
}
