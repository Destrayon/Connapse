using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// The settings API's If-Match check: a save made from an outdated read is refused with 412,
/// so two administrators saving close together can't overwrite each other (CON-133).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SettingsConcurrencyIntegrationTests(SharedWebAppFixture fixture)
{
    private const string Url = "/api/settings/chunking";

    private async Task<(ChunkingSettings Settings, string ETag)> ReadAsync()
    {
        var response = await fixture.AdminClient.GetAsync(Url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        return ((await response.Content.ReadFromJsonAsync<ChunkingSettings>())!, response.Headers.ETag!.ToString());
    }

    private Task<HttpResponseMessage> PutAsync(ChunkingSettings settings, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Url) { Content = JsonContent.Create(settings) };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return fixture.AdminClient.SendAsync(request);
    }

    [Fact]
    public async Task UpdateSettings_IfMatchOfTheCurrentCopy_Saves_AndReturnsTheTagGetNowShows()
    {
        var (original, etag) = await ReadAsync();
        try
        {
            var response = await PutAsync(original with { MaxChunkSize = original.MaxChunkSize + 7 }, etag);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var (after, afterTag) = await ReadAsync();
            after.MaxChunkSize.Should().Be(original.MaxChunkSize + 7);
            response.Headers.ETag!.ToString().Should().Be(afterTag);
            afterTag.Should().NotBe(etag);
        }
        finally
        {
            await PutAsync(original, null);
        }
    }

    [Fact]
    public async Task UpdateSettings_IfMatchOfAnOlderCopy_Returns412_AndKeepsTheOtherAdminsChange()
    {
        var (original, staleTag) = await ReadAsync();
        try
        {
            // Another administrator saves in between.
            (await PutAsync(original with { MaxChunkSize = original.MaxChunkSize + 11 }, null))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var response = await PutAsync(original with { Overlap = original.Overlap + 3 }, staleTag);

            response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("type").GetString().Should().EndWith("#settings-changed");
            body.GetProperty("error").GetString().Should().Contain("Read them again");

            var (after, _) = await ReadAsync();
            after.MaxChunkSize.Should().Be(original.MaxChunkSize + 11);
            after.Overlap.Should().Be(original.Overlap);
        }
        finally
        {
            await PutAsync(original, null);
        }
    }

    [Fact]
    public async Task UpdateSettings_OnlyAKeyChangedSinceTheRead_Returns412()
    {
        const string llm = "/api/settings/llm";
        var read = await fixture.AdminClient.GetAsync(llm);
        var original = (await read.Content.ReadFromJsonAsync<LlmSettings>())!;
        string staleTag = read.Headers.ETag!.ToString();
        try
        {
            // Another administrator changes nothing but a key, which GET never shows.
            (await fixture.AdminClient.PutAsJsonAsync(llm, original with { OpenAiApiKey = "sk-other-admin" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            var request = new HttpRequestMessage(HttpMethod.Put, llm) { Content = JsonContent.Create(original with { Model = original.Model + "-x" }) };
            request.Headers.TryAddWithoutValidation("If-Match", staleTag);
            var response = await fixture.AdminClient.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        }
        finally
        {
            await fixture.AdminClient.PutAsJsonAsync(llm, original with { OpenAiApiKey = "" });
        }
    }

    [Theory]
    [InlineData("not-a-tag")]
    [InlineData("\"stale\", *")]
    [InlineData("*, *")]
    [InlineData("\"abc")]
    [InlineData(",")]
    [InlineData("\"abc\", junk")]
    [InlineData("junk, \"abc\"")]
    public async Task UpdateSettings_MalformedIfMatch_Returns400AndSavesNothing(string ifMatch)
    {
        var (original, etag) = await ReadAsync();

        var response = await PutAsync(original with { MaxChunkSize = original.MaxChunkSize + 5 }, ifMatch);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("field").GetString().Should().Be("If-Match");
        (await ReadAsync()).ETag.Should().Be(etag);
    }

    [Fact]
    public async Task UpdateSettings_ConcurrentSavesFromTheSameRead_ExactlyOneWins()
    {
        var (original, etag) = await ReadAsync();
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(1, 6).Select(i =>
                PutAsync(original with { MaxChunkSize = original.MaxChunkSize + i }, etag)));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed).Should().Be(5);
        }
        finally
        {
            await PutAsync(original, null);
        }
    }
}
