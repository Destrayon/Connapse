using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Endpoints;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>Uploading many files at once, each naming its own folder, which is how the browser sends a folder (#394).</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FolderUploadTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Upload_PathsNameEachFilesFolder_EachFileLandsInItsFolder()
    {
        Guid containerId = await CreateContainerAsync();

        UploadResponse uploaded = await UploadAsync(containerId, [("a.md", "/one/"), ("b.md", "/one/two/"), ("c.md", "/")]);

        uploaded.SuccessCount.Should().Be(3);
        uploaded.Documents.Select(d => d.Path).Should().Equal("/one/a.md", "/one/two/b.md", "/c.md");
    }

    [Fact]
    public async Task Upload_LoneFileWithPathsFails_ReportsItInPlaceRatherThanFailingTheRequest()
    {
        Guid containerId = await CreateContainerAsync();

        UploadResponse uploaded = await UploadAsync(containerId, [("tool.exe", "/one/")]);

        uploaded.SuccessCount.Should().Be(0);
        uploaded.Documents.Should().ContainSingle().Which.Error.Should().Contain("Unsupported");
    }

    [Fact]
    public async Task Upload_OneFilesFolderIsRefused_TheOthersStillLand()
    {
        Guid containerId = await CreateContainerAsync();

        UploadResponse uploaded = await UploadAsync(containerId, [("a.md", "/../escape/"), ("b.md", "/fine/")]);

        uploaded.SuccessCount.Should().Be(1);
        uploaded.Documents[0].Error.Should().Contain("traversal");
        uploaded.Documents[1].Path.Should().Be("/fine/b.md");
    }

    [Fact]
    public async Task Upload_PathsDoNotMatchTheFiles_RefusesTheRequestAndStoresNothing()
    {
        Guid containerId = await CreateContainerAsync();
        using MultipartFormDataContent form = Form([("a.md", "/one/"), ("b.md", null)]);

        HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("field").GetString().Should().Be("paths");
        string listed = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files");
        JsonDocument.Parse(listed).RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Upload_WithoutPaths_EveryFileGoesInPath()
    {
        Guid containerId = await CreateContainerAsync();
        using MultipartFormDataContent form = Form([("a.md", null), ("b.md", null)]);
        form.Add(new StringContent("/shared/"), "path");

        HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);

        response.EnsureSuccessStatusCode();
        UploadResponse uploaded = (await response.Content.ReadFromJsonAsync<UploadResponse>())!;
        uploaded.Documents.Select(d => d.Path).Should().Equal("/shared/a.md", "/shared/b.md");
    }

    /// <summary>
    /// A folder on disk, sent by folderUpload.js under Node over a real socket: more files than one request takes, four
    /// levels deep, and two files the server can't take.
    /// </summary>
    [Fact]
    public async Task Upload_RealNestedFolder_EveryFileArrivesAndIsFoundBySearch()
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("connapse-folder-upload-");
        try
        {
            string folder = Path.Combine(temp.FullName, "project");
            Dictionary<string, string> markerByPath = WriteNestedFolder(folder);
            int filesInFolder = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
            Guid containerId = await CreateContainerAsync();

            IngestionProgressNotifier notifier = fixture.Factory.Services.GetRequiredService<IngestionProgressNotifier>();
            ConcurrentDictionary<string, DocumentStatus> statuses = new();
            using SemaphoreSlim statusChanged = new(0);
            void Record(DocumentStatusChangedEvent change)
            {
                statuses[change.DocumentId] = change.Status;
                statusChanged.Release();
            }
            notifier.StatusChanged += Record;
            try
            {
                using ForwardingServer server = new(fixture.AdminClient);
                JsonElement run = await UploadWithNodeAsync(folder, $"{server.Address}api/containers/{containerId}/files", "/imports/");

                run.GetProperty("total").GetInt32().Should().Be(filesInFolder);
                run.GetProperty("succeeded").GetInt32().Should().Be(markerByPath.Count);
                run.GetProperty("failures").EnumerateArray().Select(f => f.GetProperty("path").GetString())
                    .Should().BeEquivalentTo("/imports/project/src/tool.exe", "/imports/project/docs/empty.md");

                using IServiceScope scope = fixture.Factory.Services.CreateScope();
                IReadOnlyList<Document> stored = await scope.ServiceProvider.GetRequiredService<IDocumentStore>()
                    .ListAsync(containerId, take: filesInFolder + 10);
                stored.Select(d => d.Path).Should().BeEquivalentTo(markerByPath.Keys);

                using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
                while (!stored.All(d => statuses.GetValueOrDefault(d.Id) is DocumentStatus.Ready or DocumentStatus.FailedRetryable or DocumentStatus.FailedPermanent))
                    await statusChanged.WaitAsync(timeout.Token);
                stored.Should().OnlyContain(d => statuses[d.Id] == DocumentStatus.Ready);

                foreach (Document document in stored)
                {
                    string marker = markerByPath[document.Path];
                    JsonElement found = await fixture.AdminClient.GetFromJsonAsync<JsonElement>(
                        $"/api/containers/{containerId}/search?q={marker}&mode=Keyword&topK=5");
                    found.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("documentId").GetString())
                        .Should().Contain(document.Id, "{0} should be found by its own word", document.Path);
                }
            }
            finally
            {
                notifier.StatusChanged -= Record;
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    /// <summary>Writes the folder and answers each uploadable file's path in the container with the word only it holds.</summary>
    private static Dictionary<string, string> WriteNestedFolder(string folder)
    {
        string[] subfolders = ["", "docs", "docs/guides", "docs/guides/deep/down", "src"];
        Dictionary<string, string> markerByPath = [];

        void Write(string relativePath, int number)
        {
            string marker = $"folderprobe{number:D4}";
            string path = Path.Combine(folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"Notes kept in the project. The word for this file is {marker}.");
            markerByPath[$"/imports/project/{relativePath}"] = marker;
        }

        for (int i = 0; i < 105; i++)
        {
            string subfolder = subfolders[i % subfolders.Length];
            Write($"{subfolder}/note-{i}.{(i % 2 == 0 ? "md" : "txt")}".TrimStart('/'), i);
        }
        Write("docs/a name with spaces.md", 105);
        Write("docs/résumé.md", 106);

        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllBytes(Path.Combine(folder, "src", "tool.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(Path.Combine(folder, "docs", "empty.md"), []);
        return markerByPath;
    }

    private async Task<JsonElement> UploadWithNodeAsync(string folder, string url, string basePath)
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IFileTypeValidator fileTypes = scope.ServiceProvider.GetRequiredService<IFileTypeValidator>();
        string script = Path.Combine(RepositoryRoot(), "tests", "Connapse.Web.Js", "uploadRealFolder.mjs");

        ProcessStartInfo start = new("node") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string argument in new[] { script, folder, url, basePath, string.Join(',', fileTypes.SupportedExtensions) })
            start.ArgumentList.Add(argument);

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        using Process node = Process.Start(start)!;
        Task<string> output = node.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = node.StandardError.ReadToEndAsync(timeout.Token);
        await node.WaitForExitAsync(timeout.Token);

        node.ExitCode.Should().Be(0, await errors);
        return JsonDocument.Parse(await output).RootElement;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "Connapse.Web.Js")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root is above the test binaries.");
    }

    /// <summary>The test host has no socket, so this gives Node one and hands each request to the host.</summary>
    private sealed class ForwardingServer : IDisposable
    {
        private readonly HttpClient client;
        private readonly HttpListener listener = new();
        private readonly Task loop;

        public ForwardingServer(HttpClient client)
        {
            this.client = client;
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Address = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Address);
            listener.Start();
            loop = AcceptAsync();
        }

        public string Address { get; }

        private async Task AcceptAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                _ = ForwardAsync(context);
            }
        }

        private async Task ForwardAsync(HttpListenerContext context)
        {
            using MemoryStream body = new();
            await context.Request.InputStream.CopyToAsync(body);
            using HttpRequestMessage request = new(new HttpMethod(context.Request.HttpMethod), context.Request.Url!.PathAndQuery)
            {
                Content = new ByteArrayContent(body.ToArray()),
            };
            request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);

            using HttpResponseMessage response = await client.SendAsync(request);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.OutputStream);
            context.Response.Close();
        }

        public void Dispose()
        {
            listener.Close();
            loop.GetAwaiter().GetResult();
        }
    }

    private async Task<UploadResponse> UploadAsync(Guid containerId, (string Name, string? Folder)[] files)
    {
        using MultipartFormDataContent form = Form(files);
        HttpResponseMessage response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadResponse>())!;
    }

    private static MultipartFormDataContent Form((string Name, string? Folder)[] files)
    {
        MultipartFormDataContent form = [];
        foreach ((string name, string? folder) in files)
        {
            ByteArrayContent content = new(Encoding.UTF8.GetBytes($"Text of {name}."));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(content, "files", name);
            if (folder is not null)
                form.Add(new StringContent(folder), "paths");
        }
        return form;
    }

    private async Task<Guid> CreateContainerAsync()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        Container created = await scope.ServiceProvider.GetRequiredService<IContainerStore>()
            .CreateAsync(new CreateContainerRequest($"folder-{Guid.NewGuid():N}"[..20]));
        return Guid.Parse(created.Id);
    }
}
