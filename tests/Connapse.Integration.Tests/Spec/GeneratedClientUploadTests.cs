using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests.Spec;

/// <summary>
/// CON-81: the upload a client generated from the spec sends is one the server takes. The body below is what
/// Kiota's MultipartBody wrote for this route, captured from a client generated from the committed spec: a part
/// per form field, the file with its name, each part with its own content type.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class GeneratedClientUploadTests(SharedWebAppFixture fixture)
{
    private const string Boundary = "8d0d524326dd404192f407d91ea08460";

    private static readonly string KiotaBody = string.Join("\r\n",
        $"--{Boundary}",
        "Content-Type: text/plain",
        "Content-Disposition: form-data; name=\"files\"; filename=\"notes.txt\"",
        "",
        "hello world",
        $"--{Boundary}",
        "Content-Type: text/plain",
        "Content-Disposition: form-data; name=\"path\"",
        "",
        "/docs",
        $"--{Boundary}--",
        "");

    [Fact]
    public async Task Upload_TheBodyAGeneratedClientSends_StoresTheFileWhereItSays()
    {
        string name = $"kiota-upload-{Guid.NewGuid():N}"[..24];
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { name });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        string containerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        using var body = new StringContent(KiotaBody, Encoding.UTF8);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data")
        {
            Parameters = { new System.Net.Http.Headers.NameValueHeaderValue("boundary", Boundary) },
        };
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var document = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documents")[0];
        document.GetProperty("fileName").GetString().Should().Be("notes.txt");
        document.GetProperty("path").GetString().Should().Be("/docs/notes.txt");
        document.GetProperty("sizeBytes").GetInt64().Should().Be("hello world".Length);
    }
}
