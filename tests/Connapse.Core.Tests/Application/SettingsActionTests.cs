using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Vectors;
using CsCheck;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

/// <summary>The settings actions: embedding models, re-index and its status, clearing all summaries, and connection tests.</summary>
public partial class SettingsOperationTests
{
    private static readonly string ConfiguredModel = EmbeddingIdentity.For(new EmbeddingSettings());

    private sealed class Actions
    {
        public readonly World World;
        public readonly IBackgroundReindex Reindex = Substitute.For<IBackgroundReindex>();
        public readonly ReindexStateService Runs = new();
        public readonly IIngestionQueue Queue = Substitute.For<IIngestionQueue>();
        public readonly IContainerStore Containers = Substitute.For<IContainerStore>();
        public readonly IDocumentStore Documents = Substitute.For<IDocumentStore>();
        public readonly IConnectionTests Tests = Substitute.For<IConnectionTests>();

        public Actions(bool admin = true)
        {
            World = new World(admin);
            Containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
            Tests.TestAsync(Arg.Any<ConnectionTesterKind>(), Arg.Any<object>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(ConnectionTestResult.CreateSuccess("connected"));
        }

        public ListEmbeddingModels Models() => new(World.Store, World.Configured, World.Models, World.Authorizer);

        public StartReindex Start() => new(World.Store, World.Configured, Reindex, World.Audit, World.Authorizer);

        public GetReindexStatus Status() => new(Runs, Queue, World.Authorizer);

        public ClearAllSummaries Clear() => new(Containers, Documents, World.Audit, World.Authorizer);

        public TestConnection Test() => new(Tests, World.Store, World.Configured, World.Authorizer);

        /// <summary>Containers in pages of 200, as ListAsync returns them.</summary>
        public void Holding(params Container[] containers) =>
            Containers.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => containers.Skip(call.ArgAt<int>(0)).Take(call.ArgAt<int>(1)).ToList());
    }

    private static Container ContainerWith(string id) => new(id, id, null, DateTime.UnixEpoch, DateTime.UnixEpoch);

    // ── settings.embeddingModels ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Models_NotAnAdministrator_IsForbiddenAndReadsNothing()
    {
        var actions = new Actions(admin: false);

        var listed = await actions.Models().RunAsync(Caller);

        listed.Status.Should().Be(SettingsStatus.Forbidden);
        listed.Detail.Should().Be("Only administrators can list embedding models.");
        actions.World.Models.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Models_NothingStored_MarksTheConfiguredModelCurrent()
    {
        var actions = new Actions();
        actions.World.Models.GetModelsAsync(null, Arg.Any<CancellationToken>())
            .Returns([new EmbeddingModelInfo(ConfiguredModel, 768, 10)]);

        var listed = await actions.Models().RunAsync(Caller);

        listed.Result.Should().Be(new EmbeddingModels(ConfiguredModel, listed.Result!.Models, false));
        listed.Result.Models.Should().Equal(new EmbeddingModelUsage(ConfiguredModel, 768, 10, true));
        listed.Result.LegacyVectorCount.Should().Be(0);
    }

    [Fact]
    public async Task Models_Stored_MarksTheStoredModelCurrentAndCountsTheOthersAsLegacy()
    {
        var actions = new Actions();
        var stored = new EmbeddingSettings { Model = "other-model", Dimensions = 1024 };
        await actions.World.Store.SaveAsync("embedding", stored);
        string current = EmbeddingIdentity.For(stored);
        actions.World.Models.GetModelsAsync(null, Arg.Any<CancellationToken>()).Returns(
            [new EmbeddingModelInfo(current.ToUpperInvariant(), 1024, 5), new EmbeddingModelInfo(ConfiguredModel, 768, 7), new EmbeddingModelInfo("old", 384, 3)]);

        var listed = await actions.Models().RunAsync(Caller);

        listed.Result!.CurrentModel.Should().Be(current);
        listed.Result.Models.Select(m => m.IsCurrent).Should().Equal(true, false, false);
        listed.Result.HasLegacyVectors.Should().BeTrue();
        listed.Result.LegacyVectorCount.Should().Be(10);
    }

    // ── settings.reindex ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reindex_NotAnAdministrator_IsForbiddenAndStartsNothing()
    {
        var actions = new Actions(admin: false);

        var started = await actions.Start().RunAsync(Caller, new ReindexRequest());

        started.Status.Should().Be(SettingsStatus.Forbidden);
        started.Detail.Should().Be("Only administrators can re-index.");
        actions.Reindex.ReceivedCalls().Should().BeEmpty();
        actions.World.Store.Rows.Should().BeEmpty();
        actions.World.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Reindex_ContainerIdNotAGuid_IsInvalidAndStartsNothing()
    {
        var actions = new Actions();

        var started = await actions.Start().RunAsync(Caller, new ReindexRequest("not-a-guid"));

        started.Status.Should().Be(SettingsStatus.Invalid);
        started.Detail.Should().Be("containerId must be a container's id.");
        started.Field.Should().Be("containerId");
        actions.Reindex.ReceivedCalls().Should().BeEmpty();
        actions.World.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Reindex_WhatChanged_TurnsOnCrossModelSearchKeepingTheRestAndStarts()
    {
        var actions = new Actions();
        var configured = new SearchSettings { TopK = 17, CrossEncoderApiKey = "key" };
        actions.World.Configured.Values[typeof(SearchSettings)] = configured;
        string container = Guid.NewGuid().ToString();

        var started = await actions.Start().RunAsync(Caller, new ReindexRequest(container));

        started.Status.Should().Be(SettingsStatus.Success);
        started.Result.Should().Be(new ReindexRequest(container));
        var stored = actions.World.Store.Stored<SearchSettings>("search")!;
        (stored.EnableCrossModelSearch, stored.TopK, stored.CrossEncoderApiKey).Should().Be((true, 17, "key"));
        configured.EnableCrossModelSearch.Should().BeFalse("the configured value is shared by every reader");
        actions.Reindex.Received(1).Start(new ReindexOptions { ContainerId = container, Force = false, DetectSettingsChanges = true });
        await actions.World.Audit.Received(1).LogAsync(StartReindex.StartedEvent, "settings", "reindex", Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reindex_WhatChangedWithCrossModelSearchAlreadyOn_StoresNothing()
    {
        var actions = new Actions();
        actions.World.Configured.Values[typeof(SearchSettings)] = new SearchSettings { EnableCrossModelSearch = true };

        var started = await actions.Start().RunAsync(Caller, new ReindexRequest());

        started.Status.Should().Be(SettingsStatus.Success);
        actions.World.Store.Rows.Should().BeEmpty();
        actions.Reindex.Received(1).Start(Arg.Any<ReindexOptions>());
    }

    [Fact]
    public async Task Reindex_WhatChangedWithCrossModelSearchStoredOff_TurnsItOnInTheStoredSettings()
    {
        var actions = new Actions();
        await actions.World.Store.SaveAsync("search", new SearchSettings { TopK = 3 });
        actions.World.Configured.Values[typeof(SearchSettings)] = new SearchSettings { EnableCrossModelSearch = true, TopK = 99 };

        await actions.Start().RunAsync(Caller, new ReindexRequest());

        var stored = actions.World.Store.Stored<SearchSettings>("search")!;
        (stored.EnableCrossModelSearch, stored.TopK).Should().Be((true, 3));
    }

    [Fact]
    public async Task Reindex_Force_ReembedsEverythingAndTurnsOnCrossModelSearch()
    {
        var actions = new Actions();

        var started = await actions.Start().RunAsync(Caller, new ReindexRequest(Force: true));

        started.Status.Should().Be(SettingsStatus.Success);
        actions.World.Store.Stored<SearchSettings>("search")!.EnableCrossModelSearch.Should()
            .BeTrue("after a model change, documents not yet re-embedded are findable only across models");
        actions.Reindex.Received(1).Start(new ReindexOptions { ContainerId = null, Force = true, DetectSettingsChanges = true });
    }

    // ── settings.reindexStatus ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_NotAnAdministrator_IsForbidden()
    {
        var actions = new Actions(admin: false);

        var read = await actions.Status().RunAsync(Caller);

        read.Status.Should().Be(SettingsStatus.Forbidden);
        read.Detail.Should().Be("Only administrators can read re-index status.");
        actions.Queue.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Status_ReportsTheQueueDepthAndTheLatestRun()
    {
        var actions = new Actions();
        actions.Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>()).Returns(4);
        actions.Runs.MarkStarted();
        actions.Runs.MarkFailed("boom");

        var read = await actions.Status().RunAsync(Caller);

        read.Result!.QueueDepth.Should().Be(4);
        read.Result.Run.Should().Be(actions.Runs.Current);
        (read.Result.Run.Status, read.Result.Run.LastError).Should().Be((ReindexStatus.Failed, "boom"));
    }

    // ── settings.summaries.clear ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_NotAnAdministrator_IsForbiddenAndClearsNothing()
    {
        var actions = new Actions(admin: false);

        var cleared = await actions.Clear().RunAsync(Caller);

        cleared.Status.Should().Be(SettingsStatus.Forbidden);
        cleared.Detail.Should().Be("Only administrators can clear all summaries.");
        actions.Containers.ReceivedCalls().Should().BeEmpty();
        actions.Documents.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_NoContainers_ClearsNoneAndRecordsIt()
    {
        var actions = new Actions();

        var cleared = await actions.Clear().RunAsync(Caller);

        cleared.Result.Should().Be(new SummariesCleared(0, 0));
        await actions.World.Audit.Received(1).LogAsync(ClearAllSummaries.ClearedEvent, "settings", "summaries",
            new SummariesCleared(0, 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_ManyContainers_ClearsEveryPageSkippingIdsThatArentGuids()
    {
        var actions = new Actions();
        var containers = Enumerable.Range(0, 201).Select(_ => ContainerWith(Guid.NewGuid().ToString()))
            .Append(ContainerWith("not-a-guid")).ToArray();
        actions.Holding(containers);
        actions.Documents.ClearDocumentSummariesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(2);

        var cleared = await actions.Clear().RunAsync(Caller);

        cleared.Result.Should().Be(new SummariesCleared(201, 402));
        await actions.Containers.Received(1).ListAsync(0, 200, Arg.Any<CancellationToken>());
        await actions.Containers.Received(1).ListAsync(200, 200, Arg.Any<CancellationToken>());
        foreach (var container in containers.Take(201))
            await actions.Containers.Received(1).UpdateSummaryAsync(Guid.Parse(container.Id), null, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Clear_ExactlyAPage_AsksForTheNextPageAndStops()
    {
        var actions = new Actions();
        actions.Holding(Enumerable.Range(0, 200).Select(_ => ContainerWith(Guid.NewGuid().ToString())).ToArray());

        var cleared = await actions.Clear().RunAsync(Caller);

        cleared.Result!.Containers.Should().Be(200);
        await actions.Containers.Received(2).ListAsync(Arg.Any<int>(), 200, Arg.Any<CancellationToken>());
    }

    // ── settings.testConnection ───────────────────────────────────────────────────────────────

    private static ConnectionTestRequest TestOf(string? category, object? settings = null, int? timeout = null) =>
        new(category, settings is null ? default : JsonSerializer.SerializeToElement(settings), timeout);

    [Fact]
    public async Task Test_NotAnAdministrator_IsForbiddenAndTestsNothing()
    {
        var actions = new Actions(admin: false);

        var tested = await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings()));

        tested.Status.Should().Be(SettingsStatus.Forbidden);
        tested.Detail.Should().Be("Only administrators can test connections.");
        actions.Tests.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Test_NoCategory_IsInvalid(string? category)
    {
        var actions = new Actions();

        var tested = await actions.Test().RunAsync(Caller, TestOf(category, new LlmSettings()));

        (tested.Status, tested.Detail, tested.Field).Should().Be((SettingsStatus.Invalid, "Category is required", "category"));
        actions.Tests.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    public async Task Test_SettingsNotAnObject_IsInvalid(string? json)
    {
        var actions = new Actions();
        var request = new ConnectionTestRequest("llm", json is null ? default : Raw(json));

        var tested = await actions.Test().RunAsync(Caller, request);

        (tested.Status, tested.Detail, tested.Field).Should().Be((SettingsStatus.Invalid, "Settings are required", "settings"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(301)]
    public async Task Test_TimeoutOutOfRange_IsInvalid(int timeout)
    {
        var actions = new Actions();

        var tested = await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings(), timeout));

        (tested.Status, tested.Detail, tested.Field).Should().Be((SettingsStatus.Invalid, "timeoutSeconds must be between 1 and 300.", "timeoutSeconds"));
        actions.Tests.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public async Task Test_TimeoutInRange_IsPassedOn(int timeout)
    {
        var actions = new Actions();

        await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings(), timeout));

        await actions.Tests.Received(1).TestAsync(ConnectionTesterKind.Ollama, Arg.Any<object>(), TimeSpan.FromSeconds(timeout), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_NoTimeout_LeavesItToTheTester()
    {
        var actions = new Actions();

        await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings()));

        await actions.Tests.Received(1).TestAsync(ConnectionTesterKind.Ollama, Arg.Any<object>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_SettingsOfTheWrongShape_IsInvalid()
    {
        var actions = new Actions();

        var tested = await actions.Test().RunAsync(Caller, new ConnectionTestRequest("embedding", Raw("""{"dimensions":"many"}""")));

        tested.Status.Should().Be(SettingsStatus.Invalid);
        tested.Detail.Should().StartWith("Invalid settings JSON: ");
        tested.Field.Should().Be("settings");
        actions.Tests.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Test_CategoryWithoutATest_IsAFailedTestAsTheRouteHasAlwaysAnswered()
    {
        var actions = new Actions();

        var tested = await actions.Test().RunAsync(Caller, TestOf("chunking", new ChunkingSettings()));

        tested.Status.Should().Be(SettingsStatus.Success);
        tested.Result!.Success.Should().BeFalse();
        tested.Result.Message.Should().Be("Category 'chunking' does not support connection testing");
        actions.Tests.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("embedding", null, ConnectionTesterKind.Ollama)]
    [InlineData("embedding", "Ollama", ConnectionTesterKind.Ollama)]
    [InlineData("Embedding", "OpenAI", ConnectionTesterKind.OpenAiEmbedding)]
    [InlineData("embedding", "AzureOpenAI", ConnectionTesterKind.AzureOpenAiEmbedding)]
    [InlineData("llm", "Ollama", ConnectionTesterKind.Ollama)]
    [InlineData("LLM", "OpenAI", ConnectionTesterKind.OpenAiLlm)]
    [InlineData("llm", "AzureOpenAI", ConnectionTesterKind.AzureOpenAiLlm)]
    [InlineData("llm", "Anthropic", ConnectionTesterKind.Anthropic)]
    [InlineData("crossencoder", "TEI", ConnectionTesterKind.Tei)]
    [InlineData("crossencoder", "Cohere", ConnectionTesterKind.Cohere)]
    [InlineData("CrossEncoder", "Jina", ConnectionTesterKind.Jina)]
    [InlineData("crossencoder", "AzureAIFoundry", ConnectionTesterKind.AzureAIFoundry)]
    [InlineData("crossencoder", "Voyage", ConnectionTesterKind.Voyage)]
    [InlineData("minio", null, ConnectionTesterKind.Minio)]
    public async Task Test_ChoosesTheTesterForTheCategoryAndProvider(string category, string? provider, ConnectionTesterKind kind)
    {
        var actions = new Actions();
        object settings = category.ToLowerInvariant() switch
        {
            "embedding" when provider is null => Raw("""{"apiKey":"sent"}"""),
            "embedding" => new EmbeddingSettings { Provider = provider, ApiKey = "sent" },
            "llm" => new LlmSettings { Provider = provider!, ApiKey = "sent" },
            "crossencoder" => new SearchSettings { CrossEncoderProvider = provider!, CrossEncoderApiKey = "sent" },
            _ => new MinioOptions { Endpoint = "minio:9000" },
        };
        object? given = null;
        actions.Tests.TestAsync(kind, Arg.Do<object>(s => given = s), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.CreateFailure("refused"));

        var tested = await actions.Test().RunAsync(Caller, new ConnectionTestRequest(category,
            settings is JsonElement json ? json : JsonSerializer.SerializeToElement(settings, settings.GetType())));

        tested.Status.Should().Be(SettingsStatus.Success);
        tested.Result!.Message.Should().Be("refused");
        given.Should().NotBeNull();
        switch (given)
        {
            case EmbeddingSettings e: e.ApiKey.Should().Be("sent", "a key given is the one used"); break;
            case LlmSettings l: l.ApiKey.Should().Be("sent"); break;
            case SearchSettings s: s.CrossEncoderApiKey.Should().Be("sent"); break;
            default: given.Should().BeOfType<MinioOptions>().Which.Endpoint.Should().Be("minio:9000"); break;
        }
    }

    // ── a saved key goes only where it was saved for ──────────────────────────────────────────

    /// <summary>The settings the tester for <paramref name="kind"/> is given, once the test has run.</summary>
    private static Func<object?> Given(Actions actions, ConnectionTesterKind kind)
    {
        object? given = null;
        actions.Tests.TestAsync(kind, Arg.Do<object>(s => given = s), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.CreateSuccess("connected"));
        return () => given;
    }

    [Fact]
    public async Task Test_KeyLeftOutAtTheSavedAddress_UsesTheSavedKey()
    {
        var actions = new Actions();
        await actions.World.Store.SaveAsync("llm", new LlmSettings { Provider = "OpenAI", OpenAiBaseUrl = "https://api.openai.com/v1", OpenAiApiKey = "saved" });
        var given = Given(actions, ConnectionTesterKind.OpenAiLlm);

        await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings { Provider = "OpenAI", OpenAiBaseUrl = "https://api.openai.com/v1", Model = "gpt-4.1" }));

        given().Should().BeOfType<LlmSettings>().Which.OpenAiApiKey.Should().Be("saved");
    }

    [Fact]
    public async Task Test_KeyLeftOutWithNothingStored_UsesTheConfiguredKeyAtItsAddress()
    {
        var actions = new Actions();
        actions.World.Configured.Values[typeof(SearchSettings)] = new SearchSettings { CrossEncoderProvider = "Cohere", CrossEncoderBaseUrl = null, CrossEncoderApiKey = "configured" };
        var given = Given(actions, ConnectionTesterKind.Cohere);

        await actions.Test().RunAsync(Caller, TestOf("crossencoder", new SearchSettings { CrossEncoderProvider = "Cohere", CrossEncoderBaseUrl = null }));

        given().Should().BeOfType<SearchSettings>().Which.CrossEncoderApiKey.Should().Be("configured");
    }

    [Fact]
    public async Task Test_KeyLeftOutWithAnEmptyOverrideForAMissingOne_SendsNoKey()
    {
        // The tester reads OpenAiBaseUrl ?? BaseUrl: saved, that is the proxy; tested, "" is the default endpoint.
        var actions = new Actions();
        await actions.World.Store.SaveAsync("llm", new LlmSettings { Provider = "OpenAI", OpenAiBaseUrl = null, BaseUrl = "https://proxy.internal/v1", OpenAiApiKey = "saved" });
        var given = Given(actions, ConnectionTesterKind.OpenAiLlm);

        await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings { Provider = "OpenAI", OpenAiBaseUrl = "", BaseUrl = "https://proxy.internal/v1" }));

        given().Should().BeOfType<LlmSettings>().Which.OpenAiApiKey.Should().BeNull("an empty override sends the request somewhere other than the saved proxy");
    }

    [Theory]
    [InlineData("OpenAI", "https://attacker.example/v1", null)]
    [InlineData("OpenAI", "https://api.openai.com/v1", "https://attacker.example")]
    [InlineData("Anthropic", "https://api.openai.com/v1", null)]
    public async Task Test_KeyLeftOutWithAnotherAddressOrProvider_SendsNoKey(string provider, string openAiBaseUrl, string? baseUrl)
    {
        var actions = new Actions();
        await actions.World.Store.SaveAsync("llm", new LlmSettings { Provider = "OpenAI", OpenAiBaseUrl = "https://api.openai.com/v1", OpenAiApiKey = "saved", ApiKey = "legacy" });
        object? given = null;
        actions.Tests.TestAsync(Arg.Any<ConnectionTesterKind>(), Arg.Do<object>(s => given = s), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(ConnectionTestResult.CreateFailure("no key"));

        await actions.Test().RunAsync(Caller, TestOf("llm", new LlmSettings { Provider = provider, OpenAiBaseUrl = openAiBaseUrl, BaseUrl = baseUrl }));

        var tested = given.Should().BeOfType<LlmSettings>().Subject;
        (tested.OpenAiApiKey, tested.ApiKey, tested.AnthropicApiKey).Should().Be((null, null, null), "a saved key never goes anywhere but where it was saved for");
    }

    [Fact]
    public async Task Test_KeyTypedWithAnotherAddress_UsesTheTypedKey()
    {
        var actions = new Actions();
        await actions.World.Store.SaveAsync("embedding", new EmbeddingSettings { Provider = "AzureOpenAI", AzureEndpoint = "https://ours.openai.azure.com", AzureApiKey = "saved" });
        var given = Given(actions, ConnectionTesterKind.AzureOpenAiEmbedding);

        await actions.Test().RunAsync(Caller, TestOf("embedding", new EmbeddingSettings { Provider = "AzureOpenAI", AzureEndpoint = "https://new.openai.azure.com", AzureApiKey = "typed" }));

        given().Should().BeOfType<EmbeddingSettings>().Which.AzureApiKey.Should().Be("typed");
    }

    [Fact]
    [RandomInputTestOf(typeof(TestConnection))]
    public Task Test_AnyChangedDestination_SendsOnlyTypedKeys() =>
        Gen.Select(Gen.Int[0, 2], Gen.Bool, Gen.Int[0, 63], Gen.Int[0, 63]).SampleAsync(async t =>
        {
            var (which, stored, changed, typed) = t;
            var (name, category) = which switch
            {
                0 => ("embedding", SettingsCategories.Find("embedding")!),
                1 => ("llm", SettingsCategories.Find("llm")!),
                _ => ("crossencoder", SettingsCategories.Find("search")!),
            };
            var destinations = category.Destinations;
            var secrets = category.Secrets;
            var saved = Activator.CreateInstance(category.Type)!;
            var tested = Activator.CreateInstance(category.Type)!;
            for (int i = 0; i < destinations.Count; i++)
            {
                destinations[i].SetValue(saved, $"to-{i}");
                destinations[i].SetValue(tested, (changed & (1 << i)) != 0 ? "elsewhere" : $"to-{i}");
            }
            for (int i = 0; i < secrets.Count; i++)
            {
                secrets[i].SetValue(saved, $"saved-{i}");
                secrets[i].SetValue(tested, (typed & (1 << i)) != 0 ? $"typed-{i}" : null);
            }
            var actions = new Actions();
            if (stored)
                actions.World.Store.Rows[category.Key] = JsonSerializer.Serialize(saved, category.Type);
            else
                actions.World.Configured.Values[category.Type] = saved;
            object? given = null;
            actions.Tests.TestAsync(Arg.Any<ConnectionTesterKind>(), Arg.Do<object>(s => given = s), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(ConnectionTestResult.CreateSuccess("connected"));

            await new TestConnection(actions.Tests, actions.World.Store, actions.World.Configured, actions.World.Authorizer).RunAsync(Caller, new ConnectionTestRequest(name, JsonSerializer.SerializeToElement(tested, category.Type)));

            bool moved = Enumerable.Range(0, destinations.Count).Any(i => (changed & (1 << i)) != 0);
            for (int i = 0; i < secrets.Count; i++)
                secrets[i].GetValue(given).Should().Be((typed & (1 << i)) != 0 ? $"typed-{i}" : moved ? null : $"saved-{i}", secrets[i].Name);
        }, iter: 300);

    // ── what the Settings page sends ──────────────────────────────────────────────────────────

    [Fact]
    public void BodyFrom_KeyUnchangedOnThePage_IsSentAsNullSoTheOneInEffectIsKept()
    {
        var opened = new LlmSettings { Model = "a", ApiKey = "k1", OpenAiApiKey = "o1", AzureApiKey = null, AnthropicApiKey = "x" };
        var edited = opened with { Model = "b", OpenAiApiKey = "o2", AnthropicApiKey = null, AzureApiKey = "" };

        var body = SettingsCategories.Find("llm")!.BodyFrom(edited, opened);

        body.GetProperty("Model").GetString().Should().Be("b");
        body.GetProperty("ApiKey").ValueKind.Should().Be(JsonValueKind.Null, "unchanged: whatever is stored now is kept");
        body.GetProperty("OpenAiApiKey").GetString().Should().Be("o2", "changed on the page");
        body.GetProperty("AnthropicApiKey").GetString().Should().Be("", "emptied on the page, which clears it");
        body.GetProperty("AzureApiKey").GetString().Should().Be("", "typed into and emptied again: still cleared");
    }

    [Fact]
    public async Task BodyFrom_KeyChangedElsewhereAfterThePageOpened_IsNotWrittenBack()
    {
        var world = new World();
        var opened = new SearchSettings { CrossEncoderApiKey = "old" };
        await world.Store.SaveAsync("search", new SearchSettings { CrossEncoderApiKey = "rotated" });

        var saved = await world.Update().RunAsync(Caller, "search",
            SettingsCategories.Find("search")!.BodyFrom(opened with { TopK = 9 }, opened), UnsentSecrets.Keep);

        saved.Status.Should().Be(SettingsStatus.Success);
        var stored = world.Store.Stored<SearchSettings>("search")!;
        (stored.TopK, stored.CrossEncoderApiKey).Should().Be((9, "rotated"));
    }

    [Fact]
    public void BodyFrom_CategoryWithoutSecrets_IsTheSettingsAsTheyAre()
    {
        var edited = new ChunkingSettings { MaxChunkSize = 1234 };

        var body = SettingsCategories.Find("chunking")!.BodyFrom(edited, new ChunkingSettings());

        body.GetRawText().Should().Be(JsonSerializer.Serialize(edited));
    }

    // ── random inputs ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [RandomInputTestOf(typeof(ListEmbeddingModels))]
    public Task Models_AnyModels_MarkExactlyTheStoredOrConfiguredModelCurrent() =>
        Gen.Select(Gen.Bool, Gen.Bool, Gen.Int[0, 3].Array[0, 6]).SampleAsync(async t =>
        {
            var (admin, stored, picks) = t;
            var actions = new Actions(admin);
            var settings = new EmbeddingSettings { Model = "stored-model" };
            if (stored)
                await actions.World.Store.SaveAsync("embedding", settings);
            string current = stored ? EmbeddingIdentity.For(settings) : ConfiguredModel;
            string[] ids = [current, "a", "b", ConfiguredModel == current ? EmbeddingIdentity.For(settings) : ConfiguredModel];
            var models = picks.Select((p, i) => new EmbeddingModelInfo(ids[p], 8, i + 1)).ToList();
            actions.World.Models.GetModelsAsync(null, Arg.Any<CancellationToken>()).Returns(models);

            var listed = await new ListEmbeddingModels(actions.World.Store, actions.World.Configured, actions.World.Models, actions.World.Authorizer).RunAsync(Caller);

            listed.Status.Should().Be(admin ? SettingsStatus.Success : SettingsStatus.Forbidden);
            if (!admin)
                return;
            listed.Result!.CurrentModel.Should().Be(current);
            listed.Result.Models.Select(m => m.IsCurrent).Should().Equal(models.Select(m => m.ModelId == current));
            listed.Result.HasLegacyVectors.Should().Be(models.Any(m => m.ModelId != current));
            listed.Result.LegacyVectorCount.Should().Be(models.Where(m => m.ModelId != current).Sum(m => m.VectorCount));
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(StartReindex))]
    public Task Reindex_AnyRequest_StartsOnlyValidRequestsForAdministrators() =>
        Gen.Select(Gen.Bool, Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)"x"), Gen.Const((string?)""), Gen.Guid.Select(g => (string?)g.ToString())),
            Gen.Bool, Gen.Bool).SampleAsync(async t =>
        {
            var (admin, container, force, crossModelOn) = t;
            var actions = new Actions(admin);
            actions.World.Configured.Values[typeof(SearchSettings)] = new SearchSettings { EnableCrossModelSearch = crossModelOn };

            var started = await new StartReindex(actions.World.Store, actions.World.Configured, actions.Reindex, actions.World.Audit, actions.World.Authorizer).RunAsync(Caller, new ReindexRequest(container, force));

            bool valid = container is null || Guid.TryParse(container, out _);
            started.Status.Should().Be(!admin ? SettingsStatus.Forbidden : !valid ? SettingsStatus.Invalid : SettingsStatus.Success);
            if (started.Status != SettingsStatus.Success)
            {
                actions.Reindex.ReceivedCalls().Should().BeEmpty();
                actions.World.Store.Rows.Should().BeEmpty();
                return;
            }
            actions.Reindex.Received(1).Start(new ReindexOptions { ContainerId = container, Force = force, DetectSettingsChanges = true });
            actions.World.Store.Rows.ContainsKey("search").Should().Be(!crossModelOn);
            (actions.World.Store.Stored<SearchSettings>("search") ?? new SearchSettings { EnableCrossModelSearch = crossModelOn })
                .EnableCrossModelSearch.Should().BeTrue();
        }, iter: 300);

    [Fact]
    [RandomInputTestOf(typeof(GetReindexStatus))]
    public Task Status_AnyRun_ReportsItAsRecorded() =>
        Gen.Select(Gen.Bool, Gen.Int[0, 1000], Gen.Int[0, 3]).SampleAsync(async t =>
        {
            var (admin, depth, step) = t;
            var actions = new Actions(admin);
            actions.Queue.GetQueueDepthAsync(Arg.Any<CancellationToken>()).Returns(depth);
            if (step > 0)
                actions.Runs.MarkStarted();
            if (step == 2)
                actions.Runs.MarkCompleted();
            if (step == 3)
                actions.Runs.MarkFailed("why");

            var read = await new GetReindexStatus(actions.Runs, actions.Queue, actions.World.Authorizer).RunAsync(Caller);

            read.Status.Should().Be(admin ? SettingsStatus.Success : SettingsStatus.Forbidden);
            if (admin)
                read.Result.Should().Be(new ReindexProgress(depth, actions.Runs.Current));
        }, iter: 200);

    [Fact]
    [RandomInputTestOf(typeof(ClearAllSummaries))]
    public Task Clear_AnyContainers_ClearsEachContainerWithAGuidOnce() =>
        Gen.Select(Gen.Bool, Gen.Int[0, 450], Gen.Int[0, 5], Gen.Int[0, 3]).SampleAsync(async t =>
        {
            var (admin, count, junk, perContainer) = t;
            var actions = new Actions(admin);
            var containers = Enumerable.Range(0, count).Select(_ => ContainerWith(Guid.NewGuid().ToString()))
                .Concat(Enumerable.Range(0, junk).Select(i => ContainerWith($"junk-{i}"))).ToArray();
            actions.Holding(containers);
            actions.Documents.ClearDocumentSummariesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(perContainer);

            var cleared = await new ClearAllSummaries(actions.Containers, actions.Documents, actions.World.Audit, actions.World.Authorizer).RunAsync(Caller);

            cleared.Status.Should().Be(admin ? SettingsStatus.Success : SettingsStatus.Forbidden);
            if (!admin)
            {
                actions.Documents.ReceivedCalls().Should().BeEmpty();
                return;
            }
            cleared.Result.Should().Be(new SummariesCleared(count, count * perContainer));
            actions.Containers.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IContainerStore.UpdateSummaryAsync)).Should().Be(count);
        }, iter: 100);

    [Fact]
    [RandomInputTestOf(typeof(TestConnection))]
    public Task Test_AnyRequest_TestsOnlyWellFormedRequestsForAdministrators() =>
        Gen.Select(Gen.Bool, Gen.OneOf(Gen.Const((string?)null), Gen.Const((string?)" "), Gen.Const((string?)"embedding"),
                Gen.Const((string?)"LLM"), Gen.Const((string?)"crossencoder"), Gen.Const((string?)"minio"), Gen.Const((string?)"upload")),
            Gen.Int[0, 3], Gen.OneOf(Gen.Const((int?)null), Gen.Int[-2, 400].Select(i => (int?)i))).SampleAsync(async t =>
        {
            var (admin, category, bodyKind, timeout) = t;
            var actions = new Actions(admin);
            var body = bodyKind switch
            {
                0 => default,
                1 => Raw("[1]"),
                // A number where each tested category's settings take a string.
                2 => Raw("""{"provider":5,"crossEncoderProvider":5,"endpoint":5}"""),
                _ => Raw("{}"),
            };

            var tested = await new TestConnection(actions.Tests, actions.World.Store, actions.World.Configured, actions.World.Authorizer).RunAsync(Caller, new ConnectionTestRequest(category, body, timeout));

            bool tests = category is "embedding" or "LLM" or "crossencoder" or "minio";
            bool wrongShape = bodyKind == 2 && tests;
            var expected = !admin ? SettingsStatus.Forbidden
                : string.IsNullOrWhiteSpace(category) || bodyKind < 2 || timeout is < 1 or > 300 || wrongShape ? SettingsStatus.Invalid
                : SettingsStatus.Success;
            tested.Status.Should().Be(expected);
            int calls = actions.Tests.ReceivedCalls().Count();
            calls.Should().Be(expected == SettingsStatus.Success && tests ? 1 : 0);
            if (expected == SettingsStatus.Success)
                tested.Result!.Success.Should().Be(tests);
        }, iter: 500);
}
