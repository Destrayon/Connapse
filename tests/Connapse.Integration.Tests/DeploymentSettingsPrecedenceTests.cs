using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// Which value wins when the deployment and the settings table disagree (#399), read from a real settings
/// table through the configuration the app builds at startup. The environment variables carry this class's own
/// prefix, so the shared host and anything else in this process never see them.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class DeploymentSettingsPrecedenceTests(SharedWebAppFixture fixture)
{
    private const string Prefix = "CON260_";
    private const string ComposeOllama = "http://ollama:11434";
    private const string ComposeReranker = "http://reranker:80";
    private const string SavedLocally = "http://localhost:11434";

    [Fact]
    public async Task AddDatabaseSettings_EnvironmentSetsTheAddressOfTheSavedProvider_DeploymentWins()
    {
        await WithStoredAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = SavedLocally }, () =>
        {
            Build(("Knowledge__Embedding__BaseUrl", ComposeOllama))["Knowledge:Embedding:BaseUrl"].Should().Be(ComposeOllama);
        });
    }

    [Fact]
    public async Task AddDatabaseSettings_CommandLineSetsTheAddress_DeploymentWins()
    {
        await WithStoredAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = SavedLocally }, () =>
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder([$"--Knowledge:Embedding:BaseUrl={ComposeOllama}"]);
            builder.Configuration.AddDatabaseSettings(ConnectionString);

            builder.Configuration["Knowledge:Embedding:BaseUrl"].Should().Be(ComposeOllama);
        });
    }

    [Fact]
    public async Task AddDatabaseSettings_NoDeploymentAddress_SavedAddressWins()
    {
        await WithStoredAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = SavedLocally }, () =>
        {
            Build()["Knowledge:Embedding:BaseUrl"].Should().Be(SavedLocally);
        });
    }

    [Fact]
    public async Task AddDatabaseSettings_DeploymentAddressIsBlank_SavedAddressWins()
    {
        // Compose passes an unset ${VAR:-} through as an empty variable.
        await WithStoredAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = SavedLocally }, () =>
        {
            Build(("Knowledge__Embedding__BaseUrl", " "))["Knowledge:Embedding:BaseUrl"].Should().Be(SavedLocally);
        });
    }

    [Fact]
    public async Task AddDatabaseSettings_EnvironmentSetsAModel_SavedModelStillWins()
    {
        await WithStoredAsync("embedding", new EmbeddingSettings { Provider = "Ollama", Model = "saved-model" }, () =>
        {
            Build(("Knowledge__Embedding__Model", "deployment-model"))["Knowledge:Embedding:Model"].Should().Be("saved-model");
        });
    }

    [Fact]
    public async Task AddDatabaseSettings_SavedRerankerIsAzure_ComposeTeiAddressDoesNotTakeItsPlace()
    {
        // The Azure key goes wherever this address points, and compose's address is for its TEI reranker.
        const string foundry = "https://example.models.ai.azure.com";
        await WithStoredAsync("search", new SearchSettings { CrossEncoderProvider = "AzureAIFoundry", CrossEncoderBaseUrl = foundry }, () =>
        {
            Build(("Knowledge__Search__CrossEncoderBaseUrl", ComposeReranker))["Knowledge:Search:CrossEncoderBaseUrl"].Should().Be(foundry);
        });
    }

    [Fact]
    public async Task AddDatabaseSettings_SavedRerankerIsTei_ComposeTeiAddressWins()
    {
        await WithStoredAsync("search", new SearchSettings { CrossEncoderProvider = "TEI", CrossEncoderBaseUrl = "http://localhost:8080" }, () =>
        {
            Build(("Knowledge__Search__CrossEncoderBaseUrl", ComposeReranker))["Knowledge:Search:CrossEncoderBaseUrl"].Should().Be(ComposeReranker);
        });
    }

    [Fact]
    public async Task Reload_AfterASave_DeploymentAddressStillWins()
    {
        await WithStoredAsync("embedding", new EmbeddingSettings { Provider = "Ollama", BaseUrl = SavedLocally }, () =>
        {
            ConfigurationBuilder builder = new();
            using EnvironmentScope environment = new(("Knowledge__Embedding__BaseUrl", ComposeOllama));
            builder.AddEnvironmentVariables(Prefix);
            DatabaseSettingsProvider provider = builder.AddDatabaseSettings(ConnectionString);
            IConfigurationRoot configuration = builder.Build();

            provider.Reload().Should().BeTrue();

            configuration["Knowledge:Embedding:BaseUrl"].Should().Be(ComposeOllama);
        });
    }

    private string ConnectionString =>
        fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;

    /// <summary>The configuration the app builds, as sources in the same order: appsettings, then environment, then the table.</summary>
    private IConfigurationRoot Build(params (string Name, string Value)[] variables)
    {
        using EnvironmentScope environment = new(variables);
        ConfigurationBuilder builder = new();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Knowledge:Embedding:BaseUrl"] = "http://appsettings:11434" });
        builder.AddEnvironmentVariables(Prefix);
        builder.AddDatabaseSettings(ConnectionString);
        return builder.Build();
    }

    private async Task WithStoredAsync<T>(string category, T settings, Action check) where T : class
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        ISettingsStore store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        T? original = await store.GetAsync<T>(category);
        try
        {
            await store.SaveAsync(category, settings);
            check();
        }
        finally
        {
            if (original is null)
                await store.ResetAsync(category);
            else
                await store.SaveAsync(category, original);
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string Value)[] _variables;

        public EnvironmentScope(params (string Name, string Value)[] variables)
        {
            _variables = variables;
            foreach ((string name, string value) in variables)
                Environment.SetEnvironmentVariable(Prefix + name, value);
        }

        public void Dispose()
        {
            foreach ((string name, string _) in _variables)
                Environment.SetEnvironmentVariable(Prefix + name, null);
        }
    }
}
