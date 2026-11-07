using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Uploads over a real socket. The shared host's in-memory server has no body limit of its own, so only a host on
/// Kestrel shows what a client meets (CON-266). Its ingestion runs on the shared host's workers, from the same queue.
/// The files aren't real PDFs, so ingestion refuses each one at its first bytes and only the upload is measured.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class LargeUploadTests(SharedWebAppFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private const long MiB = 1024 * 1024;
    private const long Limit = 256 * MiB;
    private const long TwoGiB = 2048 * MiB;

    private WebApplicationFactory<Program> _kestrel = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _kestrel = fixture.Factory.WithWebHostBuilder(host => host.UseSetting("Hangfire:DisableServer", "true"));
        _kestrel.UseKestrel(0);
        _kestrel.StartServer();
        // The default client follows redirects, and to do so it copies each request body into memory first.
        _kestrel.ClientOptions.AllowAutoRedirect = false;
        _kestrel.ClientOptions.HandleCookies = false;
        _client = _kestrel.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.AdminToken);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _kestrel.DisposeAsync();
    }

    [Theory]
    [InlineData(40 * MiB)]
    [InlineData(Limit)]
    public async Task Upload_FileWithinTheLimit_IsStoredWhole(long bytes)
    {
        Guid containerId = await CreateContainerAsync();

        using HttpResponseMessage response = await UploadAsync(containerId, ("big.pdf", bytes));

        await ExpectStoredAsync(containerId, response, bytes);
    }

    [Fact]
    public async Task Upload_FileWithAFolderOfItsOwn_IsStoredWhole()
    {
        Guid containerId = await CreateContainerAsync();

        using HttpResponseMessage response = await UploadAsync(containerId, folder: "/docs/deep", files: ("big.pdf", 40 * MiB));

        await ExpectStoredAsync(containerId, response, 40 * MiB);
    }

    [Fact]
    public async Task Upload_LargeFile_TakesAFractionOfItsSizeInMemory()
    {
        Guid containerId = await CreateContainerAsync();
        const long bytes = 150 * MiB;
        long before = GC.GetTotalAllocatedBytes();

        using HttpResponseMessage response = await UploadAsync(containerId, ("big.pdf", bytes));

        long allocated = GC.GetTotalAllocatedBytes() - before;
        output.WriteLine($"{allocated / MiB} MiB allocated in this process");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        allocated.Should().BeLessThan(bytes / 2);
    }

    [Fact]
    public async Task Upload_FileJustOverTheLimit_IsRefusedByName()
    {
        Guid containerId = await CreateContainerAsync();

        using HttpResponseMessage response = await UploadAsync(containerId, ("big.pdf", Limit + 1));

        await ExpectRefusedAsync(response, "The file is 256.1 MB, over the 256 MB limit.");
        await ExpectEmptyAsync(containerId);
    }

    [Fact]
    public async Task Upload_BodyFarOverTheLimit_IsRefusedBeforeItIsRead()
    {
        Guid containerId = await CreateContainerAsync();

        using HttpResponseMessage response = await UploadAsync(containerId, ("big.pdf", 300 * MiB));

        await ExpectRefusedAsync(response, "This upload is 300.1 MB, over the 256 MB limit. Send several files in separate requests.");
        await ExpectEmptyAsync(containerId);
    }

    [Fact]
    public async Task Upload_ChunkedBodyOverTheLimit_IsRefusedWithAReason()
    {
        Guid containerId = await CreateContainerAsync();
        using MultipartFormDataContent form = new();
        form.Add(new StreamContent(new FilledStream(300 * MiB, seekable: false)), "files", "big.pdf");

        using HttpResponseMessage response = await _client.PostAsync($"/api/containers/{containerId}/files", form);

        await ExpectRefusedAsync(response, "This upload is over the 256 MB limit. Send several files in separate requests.");
        await ExpectEmptyAsync(containerId);
    }

    [Fact]
    public async Task Upload_BodyPastWhatARefusalReads_IsCutOff()
    {
        Guid containerId = await CreateContainerAsync();
        using TcpClient socket = await SendHeadOfRefusedUploadAsync(containerId);
        NetworkStream stream = socket.GetStream();
        byte[] megabyte = new byte[MiB];
        long sent = 0;

        using CancellationTokenSource giveUp = new(TimeSpan.FromMinutes(2));
        Func<Task> sendUntilCut = async () =>
        {
            while (sent < TwoGiB)
            {
                await stream.WriteAsync(megabyte, giveUp.Token);
                sent += MiB;
            }
        };

        await sendUntilCut.Should().ThrowAsync<IOException>();
        sent.Should().BeLessThan(UploadBodyLimit.MaxDrainBytes + 100 * MiB);
    }

    [Fact]
    public async Task Upload_AfterAnAdminLowersTheLimit_FollowsItWithoutARestart()
    {
        Guid containerId = await CreateContainerAsync();
        UploadSettings original = (await _client.GetFromJsonAsync<UploadSettings>("/api/settings/upload"))!;
        (await _client.PutAsJsonAsync("/api/settings/upload", original with { MaxFileBytes = 2 * MiB })).EnsureSuccessStatusCode();
        try
        {
            using HttpResponseMessage single = await UploadAsync(containerId, ("over.pdf", 3 * MiB));
            await ExpectRefusedAsync(single, "The file is 3 MB, over the 2 MB limit.");

            using HttpResponseMessage body = await UploadAsync(containerId, ("far-over.pdf", 40 * MiB));
            await ExpectRefusedAsync(body, "This upload is 40.1 MB, over the 2 MB limit. Send several files in separate requests.");

            using HttpResponseMessage mixed = await UploadAsync(containerId, ("fits.pdf", MiB), ("over.pdf", 3 * MiB));
            mixed.StatusCode.Should().Be(HttpStatusCode.OK);
            using JsonDocument result = JsonDocument.Parse(await mixed.Content.ReadAsStringAsync());
            JsonElement[] documents = [.. result.RootElement.GetProperty("documents").EnumerateArray()];
            result.RootElement.GetProperty("successCount").GetInt32().Should().Be(1);
            documents[1].GetProperty("error").GetString().Should().Be("The file is 3 MB, over the 2 MB limit.");
        }
        finally
        {
            (await _client.PutAsJsonAsync("/api/settings/upload", original)).EnsureSuccessStatusCode();
        }
    }

    /// <summary>Opens a connection, states a body far over the limit, and reads the refusal before sending any of it.</summary>
    private async Task<TcpClient> SendHeadOfRefusedUploadAsync(Guid containerId)
    {
        Uri server = _client.BaseAddress!;
        TcpClient socket = new();
        await socket.ConnectAsync(server.Host, server.Port);
        NetworkStream stream = socket.GetStream();
        string head = $"POST /api/containers/{containerId}/files HTTP/1.1\r\nHost: {server.Authority}\r\n"
            + $"Authorization: Bearer {fixture.AdminToken}\r\nContent-Type: multipart/form-data; boundary=x\r\n"
            + $"Content-Length: {TwoGiB}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));

        byte[] buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer);
        Encoding.ASCII.GetString(buffer, 0, read).Should().StartWith("HTTP/1.1 413");
        return socket;
    }

    private Task<HttpResponseMessage> UploadAsync(Guid containerId, params (string Name, long Bytes)[] files) =>
        UploadAsync(containerId, null, files);

    private async Task<HttpResponseMessage> UploadAsync(Guid containerId, string? folder, params (string Name, long Bytes)[] files)
    {
        using MultipartFormDataContent form = new();
        foreach ((string name, long bytes) in files)
        {
            StreamContent file = new(new FilledStream(bytes));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(file, "files", name);
            if (folder is not null)
                form.Add(new StringContent(folder), "paths");
        }
        return await _client.PostAsync($"/api/containers/{containerId}/files", form);
    }

    private async Task ExpectStoredAsync(Guid containerId, HttpResponseMessage response, long bytes)
    {
        string body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"{(int)response.StatusCode}: {body}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument result = JsonDocument.Parse(body);
        JsonElement document = result.RootElement.GetProperty("documents")[0];
        document.TryGetProperty("error", out JsonElement error).Should().BeTrue();
        error.ValueKind.Should().Be(JsonValueKind.Null);

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IConnector connector = scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId.ToString());
        await using Stream content = await connector.ReadFileAsync(document.GetProperty("path").GetString()!.TrimStart('/'));
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer)) > 0)
            total += read;
        total.Should().Be(bytes);
    }

    private async Task ExpectRefusedAsync(HttpResponseMessage response, string detail)
    {
        string body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"{(int)response.StatusCode}: {body}");
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using JsonDocument problem = JsonDocument.Parse(body);
        problem.RootElement.GetProperty("error").GetString().Should().Be("file_too_large");
        problem.RootElement.GetProperty("detail").GetString().Should().Be(detail);
    }

    private async Task ExpectEmptyAsync(Guid containerId)
    {
        using HttpResponseMessage listed = await _client.GetAsync($"/api/containers/{containerId}/files");
        listed.EnsureSuccessStatusCode();
        using JsonDocument result = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    private async Task<Guid> CreateContainerAsync()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        Container created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"large-{Guid.NewGuid():N}"[..20]));
        return Guid.Parse(created.Id);
    }

    /// <summary>A long body that takes no memory, so the test's own buffers say nothing about the server's.</summary>
    private sealed class FilledStream(long length, bool seekable = true) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, length - Position);
            buffer.AsSpan(offset, read).Fill(0x25);
            Position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
