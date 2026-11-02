using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests;

/// <summary>
/// The #666 setup end to end: Connapse's OpenAI provider pointed at an OpenAI-compatible endpoint serving
/// Qwen/Qwen3-Embedding-8B, with Settings > Embedding > Dimensions at 1536. The stub endpoint answers like
/// Qwen3: the asked-for size when the request carries "dimensions", its native 4096 otherwise. Settings go
/// through the settings API and the document through a real upload and ingestion.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class OpenAiCompatibleDimensionsIntegrationTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string Model = "Qwen/Qwen3-Embedding-8B";
    private const int NativeLength = 4096;
    private const int Configured = 1536;

    private readonly ConcurrentQueue<JsonObject> _requests = new();
    private WebApplication _endpoint = null!;
    private string _originalSettings = null!;
    private string _containerId = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _endpoint = builder.Build();
        _endpoint.MapPost("/v1/embeddings", async (HttpRequest request) =>
        {
            var body = (await JsonNode.ParseAsync(request.Body))!.AsObject();
            _requests.Enqueue(body);
            int length = body["dimensions"]?.GetValue<int>() ?? NativeLength;
            int count = body["input"] is JsonArray inputs ? inputs.Count : 1;
            float[] vector = Enumerable.Repeat(0.5f, length).ToArray();
            object embedding = body["encoding_format"]?.GetValue<string>() == "base64"
                ? Convert.ToBase64String(vector.SelectMany(BitConverter.GetBytes).ToArray())
                : vector;
            return Results.Json(new
            {
                @object = "list",
                data = Enumerable.Range(0, count).Select(i => new { @object = "embedding", index = i, embedding }),
                model = Model,
                usage = new { prompt_tokens = 1, total_tokens = 1 },
            });
        });
        await _endpoint.StartAsync();

        _originalSettings = await fixture.AdminClient.GetStringAsync("/api/settings/embedding");

        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"dims-666-{Guid.NewGuid().ToString("N")[..8]}" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _containerId = (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    public async Task DisposeAsync()
    {
        await fixture.AdminClient.DeleteAsync($"/api/containers/{_containerId}");
        using var restore = new StringContent(_originalSettings, Encoding.UTF8, "application/json");
        await fixture.AdminClient.PutAsync("/api/settings/embedding", restore);
        await _endpoint.DisposeAsync();
    }

    [Fact]
    public async Task Upload_AskTheModelForThisSizeOn_SendsDimensionsAndStoresVectorsOfTheConfiguredSize()
    {
        await UseQwenAsync(sendDimensions: true);

        string marker = Guid.NewGuid().ToString("N");
        string documentId = await UploadAsync(marker);
        await WaitForAsync(documentId, doc => doc.Metadata.GetValueOrDefault("Status") == "Ready");

        RequestsFor(marker).Should().NotBeEmpty()
            .And.OnlyContain(r => r["dimensions"] != null && r["dimensions"]!.GetValue<int>() == Configured);
        (await StoredLengthsAsync(documentId)).Should().NotBeEmpty().And.OnlyContain(length => length == Configured);
    }

    [Fact]
    public async Task Upload_AskTheModelForThisSizeOff_SendsNoDimensionsAndNamesTheMismatchInsteadOfStoringIt()
    {
        await UseQwenAsync(sendDimensions: false);

        string marker = Guid.NewGuid().ToString("N");
        string documentId = await UploadAsync(marker);
        DocumentDto doc = await WaitForAsync(documentId, d =>
            d.Metadata.GetValueOrDefault("Status") == "Ready" || d.Metadata.ContainsKey("ErrorMessage"));

        RequestsFor(marker).Should().NotBeEmpty().And.OnlyContain(r => !r.ContainsKey("dimensions"));
        (await StoredLengthsAsync(documentId)).Should().BeEmpty("4096-number vectors contradict Dimensions = 1536");
        doc.Metadata.GetValueOrDefault("Status").Should().NotBe("Ready");
        doc.Metadata.GetValueOrDefault("ErrorMessage").Should()
            .Contain($"returned {NativeLength}-dimension vectors").And.Contain($"Dimensions is {Configured}")
            .And.Contain("Ask the model for this size");
    }

    private async Task UseQwenAsync(bool sendDimensions)
    {
        var settings = JsonNode.Parse(_originalSettings)!.AsObject();
        settings["provider"] = "OpenAI";
        settings["model"] = Model;
        settings["dimensions"] = Configured;
        // Left out when off, which means the same, so the off case also runs against a build without the setting.
        if (sendDimensions)
            settings["sendDimensions"] = true;
        else
            settings.Remove("sendDimensions");
        settings["openAiBaseUrl"] = $"{_endpoint.Urls.First()}/v1";
        settings["openAiApiKey"] = "sk-con212-test";
        using var content = new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await fixture.AdminClient.PutAsync("/api/settings/embedding", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private IEnumerable<JsonObject> RequestsFor(string marker) =>
        _requests.Where(r => r["input"]?.ToJsonString().Contains(marker) == true);

    private async Task<string> UploadAsync(string marker)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            $"Reference {marker}. Photosynthesis converts light energy into chemical energy stored in glucose."));
        file.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", "qwen.txt");
        multipart.Add(new StringContent("/test"), "path");
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{_containerId}/files", multipart);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UploadResponse>(JsonOptions))!.Documents[0].DocumentId;
    }

    private async Task<DocumentDto> WaitForAsync(string documentId, Func<DocumentDto, bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var doc = await fixture.AdminClient.GetFromJsonAsync<DocumentDto>(
                $"/api/containers/{_containerId}/files/{documentId}", JsonOptions);
            if (doc is not null && done(doc))
                return doc;
            await Task.Delay(500);
        }
        throw new TimeoutException("Ingestion did not reach the expected state within 60 seconds");
    }

    private async Task<List<int>> StoredLengthsAsync(string documentId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        await using KnowledgeDbContext ctx = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<KnowledgeDbContext>>().CreateDbContextAsync();
        var vectors = await ctx.ChunkVectors.AsNoTracking()
            .Where(v => v.DocumentId == Guid.Parse(documentId)).Select(v => v.Embedding).ToListAsync();
        return vectors.Select(v => v.ToArray().Length).ToList();
    }

    private record ContainerDto(string Id, string Name);
    private record UploadResponse(List<UploadedDocument> Documents);
    private record UploadedDocument(string DocumentId);
    private record DocumentDto(string Id, Dictionary<string, string> Metadata);
}
