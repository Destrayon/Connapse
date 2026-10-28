using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Connapse.Web.Tests.Endpoints;

[Trait("Category", "Unit")]
public class SettingsConcurrencyTests
{
    private const string Category = "chunking";

    private static ChunkingSettings Settings(int maxChunkSize) => new() { MaxChunkSize = maxChunkSize };

    /// <summary>A store whose UpdateAsync runs the caller's update against <paramref name="stored"/>, as the real one does under its row lock.</summary>
    private static ISettingsStore StoreHolding(ChunkingSettings? stored)
    {
        var store = Substitute.For<ISettingsStore>();
        store.UpdateAsync(Category, Arg.Any<Func<ChunkingSettings?, ChunkingSettings>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<ChunkingSettings?, ChunkingSettings>>()(stored));
        return store;
    }

    [Fact]
    public void ETagFor_SameContent_GivesTheSameStrongQuotedTag()
    {
        string first = SettingsConcurrency.ETagFor(Settings(700));
        string second = SettingsConcurrency.ETagFor(Settings(700));

        first.Should().Be(second);
        first.Should().StartWith("\"").And.EndWith("\"");
        first.Should().NotStartWith("W/");
    }

    [Fact]
    public void ETagFor_DifferentContent_GivesDifferentTags()
    {
        SettingsConcurrency.ETagFor(Settings(700)).Should().NotBe(SettingsConcurrency.ETagFor(Settings(701)));
    }

    [Theory]
    [InlineData("\"abc\"", true)]
    [InlineData("\"zzz\", \"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("\"zzz\"", false)]
    [InlineData("W/\"abc\"", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void IfMatchAllows_FollowsStrongComparison(string ifMatch, bool expected)
    {
        SettingsConcurrency.IfMatchAllows(new StringValues(ifMatch), "\"abc\"").Should().Be(expected);
    }

    [Fact]
    public void IfMatchAllows_RandomSettingsPairs_MatchExactlyWhenTheContentIsTheSame()
    {
        var random = new Random(133);
        for (int i = 0; i < 300; i++)
        {
            var read = new ChunkingSettings { MaxChunkSize = random.Next(1, 6), Overlap = random.Next(0, 3), Strategy = random.Next(2) == 0 ? "Semantic" : "FixedSize" };
            var stored = new ChunkingSettings { MaxChunkSize = random.Next(1, 6), Overlap = random.Next(0, 3), Strategy = random.Next(2) == 0 ? "Semantic" : "FixedSize" };
            bool sameContent = JsonSerializer.Serialize(read) == JsonSerializer.Serialize(stored);

            SettingsConcurrency.IfMatchAllows(SettingsConcurrency.ETagFor(read), SettingsConcurrency.ETagFor(stored))
                .Should().Be(sameContent, $"read {JsonSerializer.Serialize(read)} vs stored {JsonSerializer.Serialize(stored)}");
        }
    }

    [Fact]
    public async Task SaveAsync_WithoutIfMatch_ReplacesAsBefore()
    {
        var store = StoreHolding(Settings(1));
        var incoming = Settings(900);

        string etag = await SettingsConcurrency.SaveAsync(store, Category, incoming, StringValues.Empty, () => Settings(2), CancellationToken.None);

        await store.Received(1).SaveAsync(Category, incoming, Arg.Any<CancellationToken>());
        await store.DidNotReceiveWithAnyArgs().UpdateAsync<ChunkingSettings>(default!, default!, default);
        etag.Should().Be(SettingsConcurrency.ETagFor(incoming));
    }

    [Fact]
    public async Task SaveAsync_IfMatchOfTheStoredCopy_Saves_AndReturnsTheNewTag()
    {
        var stored = Settings(500);
        var store = StoreHolding(stored);
        var incoming = Settings(900);

        string etag = await SettingsConcurrency.SaveAsync(
            store, Category, incoming, SettingsConcurrency.ETagFor(stored), () => Settings(2), CancellationToken.None);

        var update = (Func<ChunkingSettings?, ChunkingSettings>)store.ReceivedCalls().Single().GetArguments()[1]!;
        update(stored).Should().BeSameAs(incoming);
        await store.DidNotReceiveWithAnyArgs().SaveAsync<ChunkingSettings>(default!, default!, default);
        etag.Should().Be(SettingsConcurrency.ETagFor(incoming));
    }

    [Fact]
    public async Task SaveAsync_IfMatchOfAnOlderCopy_IsRefusedWithAReloadMessage()
    {
        var store = StoreHolding(Settings(600));

        var save = () => SettingsConcurrency.SaveAsync(
            store, Category, Settings(900), SettingsConcurrency.ETagFor(Settings(500)), () => Settings(2), CancellationToken.None);

        (await save.Should().ThrowAsync<SettingsChangedException>())
            .Which.Message.Should().Contain("'chunking'").And.Contain("Reload");
        await store.DidNotReceiveWithAnyArgs().SaveAsync<ChunkingSettings>(default!, default!, default);
    }

    [Fact]
    public async Task SaveAsync_NothingStored_ComparesAgainstTheFallbackGetShowed()
    {
        var shown = Settings(512);
        var incoming = Settings(900);

        string etag = await SettingsConcurrency.SaveAsync(
            StoreHolding(null), Category, incoming, SettingsConcurrency.ETagFor(shown), () => shown, CancellationToken.None);
        etag.Should().Be(SettingsConcurrency.ETagFor(incoming));

        var stale = () => SettingsConcurrency.SaveAsync(
            StoreHolding(null), Category, incoming, SettingsConcurrency.ETagFor(Settings(1)), () => shown, CancellationToken.None);
        await stale.Should().ThrowAsync<SettingsChangedException>();
    }

    [Fact]
    public async Task SaveAsync_SomethingStored_NeverConsultsTheFallback()
    {
        var stored = Settings(500);
        int fallbackCalls = 0;

        await SettingsConcurrency.SaveAsync(
            StoreHolding(stored), Category, Settings(900), SettingsConcurrency.ETagFor(stored),
            () => { fallbackCalls++; return Settings(2); }, CancellationToken.None);

        fallbackCalls.Should().Be(0);
    }
}
