using System.Security.Claims;
using System.Text.Json;
using Connapse.Application;
using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Vectors;
using CsCheck;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Core.Tests.Application;

[Trait("Category", "Unit")]
public partial class SettingsOperationTests
{
    private static readonly OperationContext Caller = new(new ClaimsPrincipal(new ClaimsIdentity("test")), OperationSurface.Rest);

    /// <summary>A settings store in memory, which round-trips values through JSON as the database does.</summary>
    private sealed class MemoryStore : ISettingsStore
    {
        public readonly Dictionary<string, string> Rows = new(StringComparer.Ordinal);
        public readonly List<string> Reset = [];
        private int _writes;

        /// <summary>Each row's revision, as Postgres's xmin: a new one on every write.</summary>
        public string? RevisionOf(string category) => Rows.ContainsKey(category) ? _revisions.GetValueOrDefault(category, "0") : null;

        private readonly Dictionary<string, string> _revisions = new(StringComparer.Ordinal);

        private void Write<T>(string category, T settings)
        {
            Rows[category] = JsonSerializer.Serialize(settings);
            _revisions[category] = (++_writes).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public Task<T?> GetAsync<T>(string category, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(Rows.TryGetValue(category, out var json) ? JsonSerializer.Deserialize<T>(json) : null);

        public async Task<(T? Value, string? Revision)> GetWithRevisionAsync<T>(string category, CancellationToken cancellationToken = default) where T : class =>
            (await GetAsync<T>(category, cancellationToken), RevisionOf(category));

        public Task SaveAsync<T>(string category, T settings, CancellationToken cancellationToken = default) where T : class
        {
            Write(category, settings);
            return Task.CompletedTask;
        }

        public async Task<T> UpdateAsync<T>(string category, Func<T?, T> update, CancellationToken cancellationToken = default) where T : class =>
            (await UpdateWithRevisionAsync<T>(category, (current, _) => update(current), cancellationToken)).Value;

        public async Task<(T Value, string Revision)> UpdateWithRevisionAsync<T>(string category, Func<T?, string?, T> update, CancellationToken cancellationToken = default) where T : class
        {
            var next = update(await GetAsync<T>(category, cancellationToken), RevisionOf(category));
            Write(category, next);
            return (next, RevisionOf(category)!);
        }

        public Task ResetAsync(string category, CancellationToken cancellationToken = default)
        {
            Reset.Add(category);
            Rows.Remove(category);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Rows.Keys.ToList());

        public T? Stored<T>(string category) where T : class => Rows.TryGetValue(category, out var json) ? JsonSerializer.Deserialize<T>(json) : null;
    }

    /// <summary>Configuration's values: one instance per type, as IOptionsMonitor hands out.</summary>
    private sealed class Configured : IConfiguredSettings
    {
        public readonly Dictionary<Type, object> Values = [];

        public T Current<T>() where T : class, new() => (T)(Values.TryGetValue(typeof(T), out var v) ? v : Values[typeof(T)] = new T());
    }

    private sealed class World
    {
        public readonly MemoryStore Store = new();
        public readonly Configured Configured = new();
        public readonly IVectorIndexReconciler Indexes = Substitute.For<IVectorIndexReconciler>();
        public readonly VectorModelDiscovery Models = Substitute.For<VectorModelDiscovery>(
            Substitute.For<IDbContextFactory<KnowledgeDbContext>>(), NullLogger<VectorModelDiscovery>.Instance);
        public readonly IAuditLogger Audit = Substitute.For<IAuditLogger>();
        public readonly PolicyAuthorizer Authorizer;

        public World(bool admin = true)
        {
            Authorizer = new PolicyAuthorizer(policy => policy == Policies.SettingsAdmin && admin);
            Models.GetModelsAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([]);
        }

        public GetSettings Get() => new(Store, Configured, Authorizer);

        public UpdateSettings Update() => new(Store, Configured, Indexes, Models, Audit, Authorizer);

        public ResetSettings ResetOperation() => new(Store, Indexes, Audit, Authorizer);
    }

    private static JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── the categories ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Categories_AreTheSettingsPageCategories_StoredUnderTheirKeys()
    {
        SettingsCategories.All.Select(c => (c.Name, c.Key, c.Type)).Should().Equal(
            ("embedding", "embedding", typeof(EmbeddingSettings)), ("chunking", "chunking", typeof(ChunkingSettings)),
            ("search", "search", typeof(SearchSettings)), ("llm", "llm", typeof(LlmSettings)), ("upload", "upload", typeof(UploadSettings)),
            ("summary", "Summary", typeof(SummarySettings)));
    }

    [Theory]
    [InlineData("azure")]
    [InlineData("azuread")]
    [InlineData("samlsignin")]
    [InlineData("identitycenter")]
    public void Find_ProviderSignInCategory_IsNotASettingsCategory(string name) =>
        SettingsCategories.Find(name).Should().BeNull("provider setup owns the categories that change who can sign in");

    [Fact]
    public async Task Get_ProviderOwnedCategory_ReadsTheStoredValue()
    {
        var world = new World();
        await world.Store.SaveAsync("identitycenter", new IdentityCenterSettings { Region = "us-east-1", InstanceArn = "arn:i" });

        var read = await world.Get().RunAsync(Caller, "IdentityCenter");

        read.Succeeded.Should().BeTrue();
        read.Result!.Category.Should().Be("identitycenter");
        read.Result.Settings.Should().BeOfType<IdentityCenterSettings>().Which.InstanceArn.Should().Be("arn:i");
    }

    [Fact]
    public async Task Get_AzureCategory_LeavesOutTheCertificatePassword()
    {
        var world = new World();
        await world.Store.SaveAsync("azuread", new AzureAdSignInSettings { TenantId = "t", ClientCertificatePassword = "secret" });

        var read = await world.Get().RunAsync(Caller, "azuread");

        read.Result!.Settings.Should().BeOfType<AzureAdSignInSettings>().Which.ClientCertificatePassword.Should().BeNull();
    }

    [Theory]
    [InlineData("samlsignin", "/api/v1/providers/aws/permissions")]
    [InlineData("identitycenter", "/api/v1/providers/aws/identity-center")]
    [InlineData("azure", "/api/v1/providers/azure/access")]
    [InlineData("azuread", "/api/v1/providers/azure/permissions")]
    public async Task UpdateAndReset_ProviderOwnedCategory_AreRefusedAndChangeNothing(string category, string route)
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, category, Body(new { }));
        var reset = await world.ResetOperation().RunAsync(Caller, category);

        updated.Status.Should().Be(SettingsStatus.ProviderOwned);
        reset.Status.Should().Be(SettingsStatus.ProviderOwned);
        updated.Detail.Should().Contain(route);
        world.Store.Rows.Should().BeEmpty();
        world.Store.Reset.Should().BeEmpty();
    }

    [Theory]
    [InlineData("embedding", new[] { "ApiKey", "OpenAiApiKey", "AzureApiKey" })]
    [InlineData("llm", new[] { "ApiKey", "OpenAiApiKey", "AzureApiKey", "AnthropicApiKey" })]
    [InlineData("search", new[] { "CrossEncoderApiKey" })]
    [InlineData("chunking", new string[0])]
    [InlineData("summary", new string[0])]
    public void Secrets_AreTheKeysAndPasswords(string category, string[] secrets) =>
        SettingsCategories.Find(category)!.Secrets.Select(p => p.Name).Should().BeEquivalentTo(secrets);

    [Theory]
    [InlineData("EMBEDDING", "embedding")]
    [InlineData("Summary", "summary")]
    [InlineData("nope", null)]
    [InlineData(null, null)]
    public void Find_MatchesTheNameInAnyCase(string? name, string? found) =>
        SettingsCategories.Find(name)?.Name.Should().Be(found);

    // ── settings.get ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_NotAnAdministrator_IsForbiddenAndReadsNothing()
    {
        var world = new World(admin: false);
        world.Store.Rows["chunking"] = "{}";

        var read = await world.Get().RunAsync(Caller, "chunking");

        read.Status.Should().Be(SettingsStatus.Forbidden);
        read.Detail.Should().Be("Only administrators can read settings.");
        world.Configured.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_UnknownCategory_IsNotFound()
    {
        var read = await new World().Get().RunAsync(Caller, "colors");

        read.Status.Should().Be(SettingsStatus.NotFound);
        read.Detail.Should().Be("Unknown settings category: colors");
    }

    [Fact]
    public async Task Get_Stored_ReturnsItWithoutItsSecrets()
    {
        var world = new World();
        await world.Store.SaveAsync("embedding", new EmbeddingSettings { Model = "m1", ApiKey = "k", OpenAiApiKey = "o", AzureApiKey = "a" });

        var read = await world.Get().RunAsync(Caller, "Embedding");

        read.Status.Should().Be(SettingsStatus.Success);
        read.Result!.Category.Should().Be("embedding");
        var settings = read.Result.Settings.Should().BeOfType<EmbeddingSettings>().Subject;
        settings.Model.Should().Be("m1");
        (settings.ApiKey, settings.OpenAiApiKey, settings.AzureApiKey).Should().Be((null, null, null));
        world.Store.Stored<EmbeddingSettings>("embedding")!.ApiKey.Should().Be("k", "reading never changes what is stored");
    }

    [Fact]
    public async Task Get_NothingStored_ReturnsTheConfiguredValueWithoutChangingIt()
    {
        var world = new World();
        var configured = new LlmSettings { Model = "m", AnthropicApiKey = "secret" };
        world.Configured.Values[typeof(LlmSettings)] = configured;

        var read = await world.Get().RunAsync(Caller, "llm");

        var settings = read.Result!.Settings.Should().BeOfType<LlmSettings>().Subject;
        settings.Model.Should().Be("m");
        settings.AnthropicApiKey.Should().BeNull();
        configured.AnthropicApiKey.Should().Be("secret", "the configured value is shared by every reader");
    }

    [Fact]
    public async Task Get_Summary_ReadsTheKeyThePageStores()
    {
        var world = new World();
        world.Store.Rows["Summary"] = JsonSerializer.Serialize(new SummarySettings { Enabled = true });

        var read = await world.Get().RunAsync(Caller, "summary");

        read.Result!.Settings.Should().BeOfType<SummarySettings>().Which.Enabled.Should().BeTrue();
    }

    // ── settings.update ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_NotAnAdministrator_IsForbiddenAndWritesNothing()
    {
        var world = new World(admin: false);

        var updated = await world.Update().RunAsync(Caller, "chunking", Body(new { maxChunkSize = 100 }));

        updated.Status.Should().Be(SettingsStatus.Forbidden);
        updated.Detail.Should().Be("Only administrators can change settings.");
        world.Store.Rows.Should().BeEmpty();
        world.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_UnknownCategory_IsNotFound()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "colors", Body(new { }));

        updated.Status.Should().Be(SettingsStatus.NotFound);
        updated.Detail.Should().Be("Unknown settings category: colors");
        world.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_PropertyTheCategoryDoesNotHave_IsInvalid()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "chunking", Raw("""{"model":"x"}"""));

        updated.Status.Should().Be(SettingsStatus.Invalid);
        updated.Detail.Should().StartWith("Invalid JSON: ");
        world.Store.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("3")]
    public async Task Update_BodyThatIsNotAnObject_IsInvalid(string json)
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "upload", Raw(json));

        updated.Status.Should().Be(SettingsStatus.Invalid);
        updated.Detail.Should().Be("The upload settings must be a JSON object.");
        world.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_ValuesTheCategoryRefuses_IsInvalidWithTheFieldAtFault()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "summary", Body(new { containerSummaryMethod = "Guess" }));

        updated.Status.Should().Be(SettingsStatus.Invalid);
        updated.Detail.Should().StartWith("ContainerSummaryMethod must be one of: ");
        updated.Field.Should().Be("containerSummaryMethod");
        world.Store.Rows.Should().BeEmpty();
        world.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_Chunking_SavesAuditsAndLeavesTheIndexesAlone()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "Chunking", Raw("""{"MAXCHUNKSIZE":321}"""));

        updated.Status.Should().Be(SettingsStatus.Success);
        var stored = world.Store.Stored<ChunkingSettings>("chunking")!;
        updated.Result.Should().BeEquivalentTo(new SettingsChange("chunking", [], SettingsCategories.Find("chunking")!.VersionOf(stored, world.Store.RevisionOf("chunking"))));
        stored.MaxChunkSize.Should().Be(321);
        await world.Audit.Received(1).LogAsync("settings.updated", "settings", "chunking", null, Arg.Any<CancellationToken>());
        world.Indexes.ReceivedCalls().Should().BeEmpty();
        world.Models.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_SecretLeftOut_KeepsTheStoredOne()
    {
        var world = new World();
        await world.Store.SaveAsync("llm", new LlmSettings { Model = "old", AnthropicApiKey = "kept" });

        await world.Update().RunAsync(Caller, "llm", Body(new { model = "new" }));

        var stored = world.Store.Stored<LlmSettings>("llm")!;
        (stored.Model, stored.AnthropicApiKey).Should().Be(("new", "kept"));
    }

    [Fact]
    public async Task Update_SecretGiven_ReplacesTheStoredOne()
    {
        var world = new World();
        await world.Store.SaveAsync("search", new SearchSettings { CrossEncoderApiKey = "old" });

        await world.Update().RunAsync(Caller, "search", Body(new { crossEncoderApiKey = "new" }));

        world.Store.Stored<SearchSettings>("search")!.CrossEncoderApiKey.Should().Be("new");
    }

    [Fact]
    public async Task Update_SecretLeftOutWithNothingStoredOrConfigured_StaysEmpty()
    {
        var world = new World();

        await world.Update().RunAsync(Caller, "embedding", Body(new { model = "m" }));

        world.Store.Stored<EmbeddingSettings>("embedding")!.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task Update_SecretLeftOutWithNothingStored_KeepsTheConfiguredOne()
    {
        var world = new World();
        world.Configured.Values[typeof(LlmSettings)] = new LlmSettings { AnthropicApiKey = "from-config" };

        // A first save of what GET showed, which left the configured key out.
        await world.Update().RunAsync(Caller, "llm", Body(new { model = "m" }));

        world.Store.Stored<LlmSettings>("llm")!.AnthropicApiKey.Should().Be("from-config");
    }

    [Fact]
    public async Task Update_EmptySecret_ClearsIt()
    {
        var world = new World();
        await world.Store.SaveAsync("search", new SearchSettings { CrossEncoderApiKey = "old" });

        await world.Update().RunAsync(Caller, "search", Body(new { crossEncoderApiKey = "" }));

        world.Store.Stored<SearchSettings>("search")!.CrossEncoderApiKey.Should().BeNull();
    }

    [Fact]
    public async Task Update_UnsentSecretsClear_NullClearsTheStoredOne()
    {
        var world = new World();
        await world.Store.SaveAsync("search", new SearchSettings { CrossEncoderApiKey = "old" });

        // The page, which holds every value: a key it sends null is one it cleared, as on a provider switch.
        await world.Update().RunAsync(Caller, "search", Body(new SearchSettings { CrossEncoderApiKey = null }), UnsentSecrets.Clear);

        world.Store.Stored<SearchSettings>("search")!.CrossEncoderApiKey.Should().BeNull();
    }

    [Fact]
    public async Task Update_UnsentSecretsClear_StillSavesASentOne()
    {
        var world = new World();

        await world.Update().RunAsync(Caller, "search", Body(new SearchSettings { CrossEncoderApiKey = "new" }), UnsentSecrets.Clear);

        world.Store.Stored<SearchSettings>("search")!.CrossEncoderApiKey.Should().Be("new");
    }

    [Fact]
    public async Task Update_Embedding_SchedulesTheIndexAndListsTheOtherModels()
    {
        var world = new World();
        var saved = new EmbeddingSettings { Model = "new-model" };
        string current = EmbeddingIdentity.For(saved);
        world.Models.GetModelsAsync(null, Arg.Any<CancellationToken>()).Returns(
            [new EmbeddingModelInfo(current.ToUpperInvariant(), 768, 10), new EmbeddingModelInfo("old-model", 384, 5)]);

        var updated = await world.Update().RunAsync(Caller, "embedding", Body(new { model = "new-model" }));

        updated.Status.Should().Be(SettingsStatus.Success);
        updated.Result!.LegacyModels.Should().Equal(new EmbeddingModelInfo("old-model", 384, 5));
        world.Indexes.Received(1).Schedule();
        await world.Audit.Received(1).LogAsync("settings.updated", "settings", "embedding", null, Arg.Any<CancellationToken>());
    }

    // ── versions: a save based on an outdated read ─────────────────────────────────────────────

    [Fact]
    public async Task Update_VersionFromBeforeAnotherSave_IsChangedAndKeepsTheOtherSave()
    {
        var world = new World();
        string read = (await world.Get().RunAsync(Caller, "chunking")).Result!.Version;
        await world.Update().RunAsync(Caller, "chunking", Body(new { maxChunkSize = 111 }));
        world.Audit.ClearReceivedCalls();

        var updated = await world.Update().RunAsync(Caller, "chunking", Body(new { maxChunkSize = 222 }), expectedVersions: [read]);

        updated.Status.Should().Be(SettingsStatus.Changed);
        updated.Detail.Should().Be("The chunking settings were changed after you read them. Read them again and make your change again.");
        world.Store.Stored<ChunkingSettings>("chunking")!.MaxChunkSize.Should().Be(111);
        world.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_EmbeddingWithAnOutdatedVersion_LeavesTheIndexesAlone()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "embedding", Body(new { model = "m" }), expectedVersions: ["outdated"]);

        updated.Status.Should().Be(SettingsStatus.Changed);
        world.Store.Rows.Should().BeEmpty();
        world.Indexes.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Update_CurrentVersion_SavesAndReturnsTheVersionTheNextReadHas()
    {
        var world = new World();
        await world.Store.SaveAsync("llm", new LlmSettings { Model = "old", AnthropicApiKey = "k" });
        string read = (await world.Get().RunAsync(Caller, "llm")).Result!.Version;

        var updated = await world.Update().RunAsync(Caller, "llm", Body(new { model = "new" }), expectedVersions: ["other", read]);

        updated.Status.Should().Be(SettingsStatus.Success);
        updated.Result!.Version.Should().NotBe(read).And.Be((await world.Get().RunAsync(Caller, "llm")).Result!.Version);
    }

    [Fact]
    public async Task Update_NothingStored_ComparesAgainstTheConfiguredValue()
    {
        var world = new World();
        world.Configured.Values[typeof(UploadSettings)] = new UploadSettings { ParallelWorkers = 7 };
        string read = (await world.Get().RunAsync(Caller, "upload")).Result!.Version;

        var updated = await world.Update().RunAsync(Caller, "upload", Body(new { parallelWorkers = 8 }), expectedVersions: [read]);

        updated.Status.Should().Be(SettingsStatus.Success);
    }

    [Fact]
    public async Task Update_NoVersionsListed_IsChanged()
    {
        var world = new World();

        var updated = await world.Update().RunAsync(Caller, "upload", Body(new { }), expectedVersions: []);

        updated.Status.Should().Be(SettingsStatus.Changed);
        world.Store.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_InvalidBodyWithAnOutdatedVersion_IsInvalid()
    {
        var updated = await new World().Update().RunAsync(Caller, "upload", Raw("[]"), expectedVersions: ["outdated"]);

        updated.Status.Should().Be(SettingsStatus.Invalid);
    }

    [Fact]
    public void VersionOf_HashesTheRevisionAndSettingsButNeverASecret()
    {
        var search = SettingsCategories.Find("search")!;

        search.VersionOf(new SearchSettings { CrossEncoderApiKey = "a" }, "7").Should()
            .Be(search.VersionOf(new SearchSettings { CrossEncoderApiKey = "b" }, "7"))
            .And.Be(search.VersionOf(new SearchSettings(), "7"))
            .And.MatchRegex("^[0-9a-f]{64}$");
        search.VersionOf(new SearchSettings { TopK = 99 }, "7").Should().NotBe(search.VersionOf(new SearchSettings(), "7"));
        search.VersionOf(new SearchSettings(), "8").Should().NotBe(search.VersionOf(new SearchSettings(), "7"));
        search.VersionOf(new SearchSettings(), null).Should().NotBe(search.VersionOf(new SearchSettings(), ""));
    }

    [Fact]
    public async Task Update_TwoKeyReplacementsFromOneRead_RefusesTheSecond()
    {
        var world = new World();
        await world.Store.SaveAsync("search", new SearchSettings { CrossEncoderApiKey = "old" });
        string read = (await world.Get().RunAsync(Caller, "search")).Result!.Version;

        var first = await world.Update().RunAsync(Caller, "search", Body(new { crossEncoderApiKey = "first" }), expectedVersions: [read]);
        var second = await world.Update().RunAsync(Caller, "search", Body(new { crossEncoderApiKey = "second" }), expectedVersions: [read]);

        first.Status.Should().Be(SettingsStatus.Success);
        second.Status.Should().Be(SettingsStatus.Changed);
        world.Store.Stored<SearchSettings>("search")!.CrossEncoderApiKey.Should().Be("first");
    }

    [Fact]
    public async Task Update_KeyClearedAfterTheRead_RefusesAReplacementFromThatRead()
    {
        var world = new World();
        await world.Store.SaveAsync("llm", new LlmSettings { AnthropicApiKey = "old" });
        string read = (await world.Get().RunAsync(Caller, "llm")).Result!.Version;
        await world.Update().RunAsync(Caller, "llm", Body(new { anthropicApiKey = "" }));

        var replaced = await world.Update().RunAsync(Caller, "llm", Body(new { anthropicApiKey = "new" }), expectedVersions: [read]);

        replaced.Status.Should().Be(SettingsStatus.Changed);
        world.Store.Stored<LlmSettings>("llm")!.AnthropicApiKey.Should().BeNull();
    }

    // ── settings.reset ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_NotAnAdministrator_IsForbiddenAndResetsNothing()
    {
        var world = new World(admin: false);

        var reset = await world.ResetOperation().RunAsync(Caller, "chunking");

        reset.Status.Should().Be(SettingsStatus.Forbidden);
        reset.Detail.Should().Be("Only administrators can reset settings.");
        world.Store.Reset.Should().BeEmpty();
        world.Audit.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_UnknownCategory_IsNotFound()
    {
        var world = new World();

        var reset = await world.ResetOperation().RunAsync(Caller, "colors");

        reset.Status.Should().Be(SettingsStatus.NotFound);
        reset.Detail.Should().Be("Unknown settings category: colors");
        world.Store.Reset.Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_Summary_RemovesTheKeyThePageStoresAndAudits()
    {
        var world = new World();

        var reset = await world.ResetOperation().RunAsync(Caller, "SUMMARY");

        reset.Result.Should().Be("summary");
        world.Store.Reset.Should().Equal("Summary");
        await world.Audit.Received(1).LogAsync("settings.reset", "settings", "summary", null, Arg.Any<CancellationToken>());
        world.Indexes.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_Embedding_SchedulesTheIndex()
    {
        var world = new World();

        await world.ResetOperation().RunAsync(Caller, "embedding");

        world.Indexes.Received(1).Schedule();
    }

    // ── random inputs ──────────────────────────────────────────────────────────────────────────

    private static readonly Gen<string?> AnyCategory = Gen.OneOf(
        Gen.Const((string?)null), Gen.Const((string?)""), Gen.Const((string?)"colors"),
        Gen.Int[0, SettingsCategories.All.Count - 1].Select(i => (string?)SettingsCategories.All[i].Name),
        Gen.Int[0, SettingsCategories.All.Count - 1].Select(i => (string?)SettingsCategories.All[i].Name.ToUpperInvariant()));

    [Fact]
    [RandomInputTestOf(typeof(GetSettings))]
    public Task Get_AnyCategory_ReadsOnlyKnownCategoriesForAdministratorsWithoutSecrets() =>
        Gen.Select(Gen.Bool, AnyCategory, Gen.Bool).SampleAsync(async t =>
        {
            var (admin, category, stored) = t;
            var world = new World(admin);
            var found = SettingsCategories.Find(category);
            if (stored && found is not null)
            {
                var value = Activator.CreateInstance(found.Type)!;
                foreach (var secret in found.Secrets)
                    secret.SetValue(value, "s3cret");
                world.Store.Rows[found.Key] = JsonSerializer.Serialize(value, found.Type);
            }

            var read = await new GetSettings(world.Store, world.Configured, world.Authorizer).RunAsync(Caller, category!);

            read.Status.Should().Be(!admin ? SettingsStatus.Forbidden : found is null ? SettingsStatus.NotFound : SettingsStatus.Success);
            if (read.Status != SettingsStatus.Success)
                return;
            read.Result!.Category.Should().Be(found!.Name);
            read.Result.Settings.Should().BeOfType(found.Type);
            found.Secrets.Select(s => s.GetValue(read.Result.Settings)).Should().OnlyContain(v => v == null);
            read.Result.Version.Should().Be(found.VersionOf(Activator.CreateInstance(found.Type)!, world.Store.RevisionOf(found.Key)));
        }, iter: 500);

    [Fact]
    [RandomInputTestOf(typeof(UpdateSettings))]
    public Task Update_AnyCategoryAndBody_SavesOnlyValidChangesKeepingUnsentSecrets() =>
        Gen.Select(Gen.Bool, AnyCategory, Gen.Int[0, 3], Gen.Bool, Gen.Int[0, 2], Gen.Enum<UnsentSecrets>(), Gen.Int[0, 3]).SampleAsync(async t =>
        {
            var (admin, category, bodyKind, storedSecret, send, unsent, versionKind) = t;
            var world = new World(admin);
            var found = SettingsCategories.Find(category);
            var secret = found?.Secrets.FirstOrDefault();
            if (found is not null && secret is not null && storedSecret)
            {
                var stored = Activator.CreateInstance(found.Type)!;
                secret.SetValue(stored, "stored");
                world.Store.Rows[found.Key] = JsonSerializer.Serialize(stored, found.Type);
            }
            // None sent, the version a read would give now, one from before another save, or an empty list.
            string? current = found is null ? null : found.VersionOf(Activator.CreateInstance(found.Type)!, world.Store.RevisionOf(found.Key));
            IReadOnlyCollection<string>? versions = versionKind switch
            {
                0 => null,
                1 => [current ?? "none"],
                2 => ["0123456789abcdef"],
                _ => [],
            };
            var body = bodyKind switch
            {
                0 => Raw("null"),
                1 => Raw("""{"noSuchProperty":1}"""),
                _ => secret is not null && send > 0 ? Raw($$"""{"{{secret.Name}}":"{{(send == 1 ? "sent" : "")}}"}""") : Raw("{}"),
            };
            int rowsBefore = world.Store.Rows.Count;

            var updated = await new UpdateSettings(world.Store, world.Configured, world.Indexes, world.Models, world.Audit, world.Authorizer)
                .RunAsync(Caller, category!, body, unsent, versions);

            var expected = !admin ? SettingsStatus.Forbidden : found is null ? SettingsStatus.NotFound
                : bodyKind < 2 ? SettingsStatus.Invalid : versionKind >= 2 ? SettingsStatus.Changed : SettingsStatus.Success;
            updated.Status.Should().Be(expected);
            if (expected != SettingsStatus.Success)
            {
                world.Store.Rows.Count.Should().Be(rowsBefore);
                world.Audit.ReceivedCalls().Should().BeEmpty();
                world.Indexes.ReceivedCalls().Should().BeEmpty();
                return;
            }
            var saved = JsonSerializer.Deserialize(world.Store.Rows[found!.Key], found.Type)!;
            updated.Result!.Version.Should().Be(found.VersionOf(saved, world.Store.RevisionOf(found.Key)));
            if (secret is not null)
                secret.GetValue(saved).Should().Be(send switch
                {
                    1 => "sent",
                    2 => null,
                    _ => unsent == UnsentSecrets.Keep && storedSecret ? "stored" : null,
                });
            world.Indexes.ReceivedCalls().Should().HaveCount(found.Name == "embedding" ? 1 : 0);
            await world.Audit.Received(1).LogAsync("settings.updated", "settings", found.Name, null, Arg.Any<CancellationToken>());
        }, iter: 1000);

    [Fact]
    [RandomInputTestOf(typeof(ResetSettings))]
    public Task Reset_AnyCategory_ResetsOnlyKnownCategoriesForAdministrators() =>
        Gen.Select(Gen.Bool, AnyCategory).SampleAsync(async t =>
        {
            var (admin, category) = t;
            var world = new World(admin);
            var found = SettingsCategories.Find(category);

            var reset = await new ResetSettings(world.Store, world.Indexes, world.Audit, world.Authorizer).RunAsync(Caller, category!);

            reset.Status.Should().Be(!admin ? SettingsStatus.Forbidden : found is null ? SettingsStatus.NotFound : SettingsStatus.Success);
            world.Store.Reset.Should().Equal(reset.Status == SettingsStatus.Success ? [found!.Key] : Array.Empty<string>());
            world.Audit.ReceivedCalls().Should().HaveCount(reset.Status == SettingsStatus.Success ? 1 : 0);
            world.Indexes.ReceivedCalls().Should().HaveCount(reset.Status == SettingsStatus.Success && found!.Name == "embedding" ? 1 : 0);
        }, iter: 500);
}
