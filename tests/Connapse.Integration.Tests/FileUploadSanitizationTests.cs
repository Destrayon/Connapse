using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FileUploadSanitizationTests(SharedWebAppFixture fixture)
{
    // ── HTTP upload ────────────────────────────────────────────────

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("folder/file.txt")]
    [InlineData(@"..\secret.txt")]
    public async Task UploadFile_TraversalFilename_Returns400(string maliciousFilename)
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"sanitize-test-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("test content"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            content.Add(fileContent, "files", maliciousFilename);

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Fact]
    public async Task UploadFile_CleanFilename_Returns200()
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"sanitize-ok-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("valid content"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            content.Add(fileContent, "files", "valid-file.txt");

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("script.bat")]
    [InlineData("archive.zip")]
    public async Task UploadFile_UnsupportedExtension_Returns400(string fileName)
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"ext-test-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("test content"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "files", fileName);

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("unsupported_file_type");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Fact]
    public async Task UploadFile_MultiDotSupportedExtension_Returns200()
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"ext-ok-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("report content"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            content.Add(fileContent, "files", "report.v2.txt");

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Fact]
    public async Task UploadFile_BatchWithUnsupportedExtension_RejectsEntireBatch()
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"ext-batch-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();

            var goodFile = new ByteArrayContent(Encoding.UTF8.GetBytes("good content"));
            goodFile.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            content.Add(goodFile, "files", "good.txt");

            var badFile = new ByteArrayContent(Encoding.UTF8.GetBytes("bad content"));
            badFile.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(badFile, "files", "bad.exe");

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("unsupported_file_type");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Fact]
    public async Task UploadFile_FilenameLongerThan255Chars_Returns400()
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"len-test-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("test content"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            var longName = new string('a', 252) + ".txt"; // 256 chars
            content.Add(fileContent, "files", longName);

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("filename_too_long");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    [Fact]
    public async Task UploadFile_FilenameAtExactly255Chars_Returns200()
    {
        var createResp = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"len-ok-{Guid.NewGuid():N}"[..20] });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResp.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        try
        {
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("test content"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            var exactName = new string('a', 251) + ".txt"; // 255 chars
            content.Add(fileContent, "files", exactName);

            var response = await fixture.AdminClient.PostAsync(
                $"/api/containers/{container!.Id}/files", content);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private record ContainerDto(string Id, string Name, string? Description, int DocumentCount = 0);
}
