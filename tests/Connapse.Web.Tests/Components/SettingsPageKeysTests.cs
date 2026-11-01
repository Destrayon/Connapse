using Bunit;
using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using Connapse.Web.Components;
using Connapse.Web.Components.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Web.Tests.Components;

/// <summary>The Settings page never holds a saved API key: it shows only that one is saved (#951).</summary>
[Trait("Category", "Unit")]
public class SettingsPageKeysTests : IDisposable
{
    private const string Saved = "sk-saved-secret";
    private const string KeptPlaceholder = "A key is saved. Type a new one to replace it.";

    private readonly BunitContext ctx = new();
    private readonly Configured configured = new();
    private readonly ISettingsStore store = Substitute.For<ISettingsStore>();

    /// <summary>Configuration's values, one instance per type, as IOptionsMonitor hands out.</summary>
    private sealed class Configured : IConfiguredSettings
    {
        public readonly Dictionary<Type, object> Values = [];

        public T Current<T>() where T : class, new() => (T)(Values.TryGetValue(typeof(T), out var v) ? v : Values[typeof(T)] = new T());
    }

    public SettingsPageKeysTests()
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.AddAuthorization().SetAuthorized("admin");
        ctx.Services.AddLogging();
        ctx.Services.AddSingleton<IConfiguredSettings>(configured);
        ctx.Services.AddSingleton(store);
        ctx.Services.AddSingleton(Substitute.For<IConnectionTests>());
        ctx.Services.AddSingleton(Substitute.For<IOperationAuthorizer>());
        ctx.Services.AddSingleton(Substitute.For<IVectorIndexReconciler>());
        ctx.Services.AddSingleton(Substitute.For<VectorModelDiscovery>(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance));
        ctx.Services.AddSingleton(Substitute.For<IAuditLogger>());
        ctx.Services.AddSingleton(Substitute.For<IBackgroundReindex>());
        ctx.Services.AddSingleton(Substitute.For<IIngestionQueue>());
        ctx.Services.AddSingleton<ReindexStateService>();
        ctx.Services.AddTransient<TestConnection>();
        ctx.Services.AddTransient<ListEmbeddingModels>();
        ctx.Services.AddTransient<StartReindex>();
        ctx.Services.AddTransient<GetReindexStatus>();
        ctx.Services.AddTransient<GetSettings>();
        ctx.Services.AddTransient<UpdateSettings>();
        ctx.Services.AddScoped<SettingsPageSaves>();
    }

    public void Dispose() => ctx.Dispose();

    /// <summary>The tab for <paramref name="category"/>, showing <paramref name="provider"/>'s fields, opened as the page opens it, every key saved.</summary>
    private IRenderedComponent<IComponent> RenderTab(string category, string provider)
    {
        var saves = ctx.Services.GetRequiredService<SettingsPageSaves>();
        switch (category)
        {
            case "embedding":
                configured.Values[typeof(EmbeddingSettings)] = new EmbeddingSettings
                { Provider = provider, ApiKey = Saved, OpenAiApiKey = Saved, AzureApiKey = Saved };
                var embedding = saves.Open<EmbeddingSettings>(category);
                return ctx.Render<EmbeddingSettingsTab>(p => p.Add(x => x.Settings, embedding).Add(x => x.SavedKeys, saves.SavedKeys(category)));
            case "llm":
                configured.Values[typeof(LlmSettings)] = new LlmSettings
                { Provider = provider, ApiKey = Saved, OpenAiApiKey = Saved, AzureApiKey = Saved, AnthropicApiKey = Saved };
                var llm = saves.Open<LlmSettings>(category);
                return ctx.Render<LlmSettingsTab>(p => p.Add(x => x.Settings, llm).Add(x => x.SavedKeys, saves.SavedKeys(category)));
            default:
                configured.Values[typeof(SearchSettings)] = new SearchSettings
                { Reranker = "CrossEncoder", CrossEncoderProvider = provider, CrossEncoderApiKey = Saved };
                var search = saves.Open<SearchSettings>(category);
                return ctx.Render<SearchSettingsTab>(p => p.Add(x => x.Settings, search).Add(x => x.SavedKeys, saves.SavedKeys(category)));
        }
    }

    public static TheoryData<string, string> KeyFields => new()
    {
        { "embedding", "OpenAI" },
        { "embedding", "AzureOpenAI" },
        { "llm", "OpenAI" },
        { "llm", "AzureOpenAI" },
        { "llm", "Anthropic" },
        { "search", "Cohere" },
        { "search", "AzureAIFoundry" },
    };

    [Theory]
    [MemberData(nameof(KeyFields))]
    public void Open_KeySaved_NeverReachesTheBrowser(string category, string provider)
    {
        var cut = RenderTab(category, provider);

        cut.FindAll("input[type=password]").Should().NotBeEmpty("the tab shows this provider's key field");
        cut.Markup.Should().NotContain(Saved);
    }

    [Theory]
    [MemberData(nameof(KeyFields))]
    public void Open_KeySaved_SaysOneIsSaved(string category, string provider)
    {
        var cut = RenderTab(category, provider);

        cut.Find("input[type=password]").GetAttribute("placeholder").Should().Be(KeptPlaceholder);
    }

    [Fact]
    public void Open_NoKeySaved_SaysNothingIsSaved()
    {
        configured.Values[typeof(LlmSettings)] = new LlmSettings { Provider = "OpenAI" };
        var saves = ctx.Services.GetRequiredService<SettingsPageSaves>();
        var llm = saves.Open<LlmSettings>("llm");

        var cut = ctx.Render<LlmSettingsTab>(p => p.Add(x => x.Settings, llm).Add(x => x.SavedKeys, saves.SavedKeys("llm")));

        cut.Find("input[type=password]").GetAttribute("placeholder").Should().Be("sk-...");
    }

    [Fact]
    public void ProviderSwitch_KeySavedForTheOldProvider_IsClearedOnSave()
    {
        configured.Values[typeof(SearchSettings)] = new SearchSettings { Reranker = "CrossEncoder", CrossEncoderProvider = "Cohere", CrossEncoderApiKey = Saved };
        var saves = ctx.Services.GetRequiredService<SettingsPageSaves>();
        var search = saves.Open<SearchSettings>("search");
        SearchSettings? submitted = null;
        var cut = ctx.Render<SearchSettingsTab>(p => p.Add(x => x.Settings, search).Add(x => x.SavedKeys, saves.SavedKeys("search"))
            .Add(x => x.OnSave, (SearchSettings s) => submitted = s));

        cut.FindAll("select").Single(s => s.QuerySelector("option[value=Jina]") is not null).Change("Jina");
        cut.Find("form").Submit();

        cut.Find("input[type=password]").GetAttribute("placeholder").Should().BeNull("the Cohere key is not Jina's");
        submitted!.CrossEncoderApiKey.Should().Be("", "empty clears the saved key; null would keep it for Jina");
        SettingsCategories.Find("search")!.BodyFrom(submitted, search).GetProperty("CrossEncoderApiKey").GetString().Should().Be("");
    }

    [Fact]
    public async Task Save_KeyTyped_SendsItAndForgetsItAfterwards()
    {
        configured.Values[typeof(LlmSettings)] = new LlmSettings { Provider = "OpenAI", OpenAiApiKey = Saved, AnthropicApiKey = Saved };
        LlmSettings? stored = null;
        ctx.Services.GetRequiredService<IOperationAuthorizer>()
            .AllowsAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<string>()).Returns(true);
        store.UpdateWithRevisionAsync("llm", Arg.Any<Func<LlmSettings?, string?, LlmSettings>>(), Arg.Any<CancellationToken>())
            .Returns(call => (stored = call.Arg<Func<LlmSettings?, string?, LlmSettings>>()(null, null), "1"));
        var saves = ctx.Services.GetRequiredService<SettingsPageSaves>();
        var opened = saves.Open<LlmSettings>("llm");

        var (saved, _, kept) = await saves.SaveAsync("llm", opened with { OpenAiApiKey = "sk-typed", AnthropicApiKey = "" });

        saved.Should().BeTrue();
        (stored!.OpenAiApiKey, stored.AnthropicApiKey).Should().Be(("sk-typed", null));
        (kept.OpenAiApiKey, kept.AnthropicApiKey).Should().Be((null, null), "the page holds no key once it is saved");
        saves.SavedKeys("llm").Should().BeEquivalentTo(["OpenAiApiKey"], "the emptied key is cleared and the typed one is saved");
    }

    [Fact]
    public void Open_LlmTab_SaysNoFeatureUsesTheSettingsYetAndStillSaves()
    {
        configured.Values[typeof(LlmSettings)] = new LlmSettings { Provider = "Ollama" };
        var saves = ctx.Services.GetRequiredService<SettingsPageSaves>();
        var llm = saves.Open<LlmSettings>("llm");
        LlmSettings? submitted = null;
        var cut = ctx.Render<LlmSettingsTab>(p => p.Add(x => x.Settings, llm).Add(x => x.SavedKeys, saves.SavedKeys("llm"))
            .Add(x => x.OnSave, (LlmSettings s) => submitted = s));

        cut.Find("[data-testid=llm-unused-notice]").TextContent.Should().Contain("No feature uses these settings yet.");
        cut.Find("form").Submit();
        submitted.Should().NotBeNull("the notice only informs; the settings still save");
    }
}
