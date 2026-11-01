using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Serialization.Multipart;

namespace Connapse.Integration.Tests.Spec;

/// <summary>
/// CON-81: an upload a client generated from the spec sends is one the server takes. With the form flattened, Kiota
/// generates the upload as <c>PostAsync(MultipartBody)</c>, which puts the body on the request through the adapter's
/// multipart writer. This does the same with the Kiota runtime connapse-cli pins, against the real endpoint, for one
/// file and for several, which go as repeated <c>files</c> parts.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class GeneratedClientUploadTests(SharedWebAppFixture fixture)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Upload_AsAGeneratedClientSendsIt_StoresEveryFileWhereItSays(int files)
    {
        string name = $"kiota-upload-{Guid.NewGuid():N}"[..24];
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { name });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        string containerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // A generated client registers these serializers when it is built: multipart for the form, text for its parts.
        ApiClientBuilder.RegisterDefaultSerializer<Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory>();
        ApiClientBuilder.RegisterDefaultSerializer<Microsoft.Kiota.Serialization.Text.TextSerializationWriterFactory>();
        ApiClientBuilder.RegisterDefaultSerializer<Microsoft.Kiota.Serialization.Form.FormSerializationWriterFactory>();
        ApiClientBuilder.RegisterDefaultSerializer<MultipartSerializationWriterFactory>();
        var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: fixture.AdminClient)
        {
            BaseUrl = fixture.AdminClient.BaseAddress!.ToString().TrimEnd('/'),
        };
        var form = new MultipartBody();
        for (int i = 1; i <= files; i++)
            form.AddOrReplacePart("files", "text/plain", new MemoryStream(Encoding.UTF8.GetBytes($"note {i}")), $"note{i}.txt");
        form.AddOrReplacePart("path", "text/plain", "/docs");

        // What the generated FilesRequestBuilder builds for PostAsync(MultipartBody).
        var request = new RequestInformation(Method.POST, "{+baseurl}/api/containers/{containerId}/files",
            new Dictionary<string, object> { ["containerId"] = containerId });
        request.Headers.TryAdd("Accept", "application/json");
        request.SetContentFromParsable(adapter, "multipart/form-data", form);
        using var message = (await adapter.ConvertToNativeRequestAsync<HttpRequestMessage>(request))!;

        var response = await fixture.AdminClient.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var documents = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documents").EnumerateArray().ToList();
        documents.Select(d => d.GetProperty("path").GetString())
            .Should().BeEquivalentTo(Enumerable.Range(1, files).Select(i => $"/docs/note{i}.txt"));
        documents.Should().OnlyContain(d => d.GetProperty("sizeBytes").GetInt64() == "note 1".Length);
    }
}
