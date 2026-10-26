using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Folders;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>
/// A folder delete whose stored files can't all be removed: the records it deleted stay deleted, and no record is left
/// pointing to a file that is gone (CON-94).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FolderDeletePartialFailureTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task FolderStoreDelete_StorageFailsOnOneFile_LeavesNoRecordWithoutItsFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" })
                (await UploadAsync(containerId, name, "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using var scope = fixture.Factory.Services.CreateScope();
            var store = FolderStoreFailingDeletesOf(scope.ServiceProvider, (path, _) => path == "/docs/b.txt");

            (await store.DeleteAsync(Guid.Parse(containerId), "/docs/")).Should().BeTrue();

            var recorded = await RecordedPathsAsync(containerId);
            var stored = await StoredPathsAsync(containerId);
            recorded.Should().OnlyContain(path => stored.Contains(path), "a record whose file is gone points to nothing");
            recorded.Should().BeEmpty("the delete was saved before any file was removed");
            stored.Should().Equal(["/docs/b.txt"], "the files that could be removed were, and only the failing one is left");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task FolderStoreDelete_StorageFailsOnceOnAFile_RetriesAndRemovesIt()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            (await UploadAsync(containerId, "a.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using var scope = fixture.Factory.Services.CreateScope();
            var store = FolderStoreFailingDeletesOf(scope.ServiceProvider, (_, attempt) => attempt == 1);

            (await store.DeleteAsync(Guid.Parse(containerId), "/docs/")).Should().BeTrue();

            (await RecordedPathsAsync(containerId)).Should().BeEmpty();
            (await StoredPathsAsync(containerId)).Should().BeEmpty("a removal that failed once is tried again");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    /// <summary>A folder store whose storage refuses to delete a file when <paramref name="fails"/> says so, given
    /// the path and how many times that path has been tried.</summary>
    private static PostgresFolderStore FolderStoreFailingDeletesOf(IServiceProvider services, Func<string, int, bool> fails)
    {
        var real = services.GetRequiredService<IManagedStorageProvider>();
        var attempts = new Dictionary<string, int>();
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(Arg.Any<string>()).Returns(call =>
        {
            var connector = (IWritableConnector)real.CreateConnector(call.Arg<string>());
            var failing = Substitute.For<IWritableConnector>();
            failing.DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async delete =>
            {
                string path = delete.Arg<string>();
                int attempt;
                lock (attempts)
                    attempts[path] = attempt = attempts.GetValueOrDefault(path) + 1;
                if (fails(path, attempt))
                    throw new IOException($"Storage refused to delete {path}.");
                await connector.DeleteFileAsync(path, delete.Arg<CancellationToken>());
            });
            return failing;
        });

        return new PostgresFolderStore(
            services.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(),
            services.GetRequiredService<IContainerStore>(),
            services.GetRequiredService<IDocumentStore>(),
            storage,
            NullLogger<PostgresFolderStore>.Instance);
    }

    private async Task<string> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = $"fdfail-{Guid.NewGuid():N}"[..20] });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
    }

    private async Task<HttpResponseMessage> UploadAsync(string containerId, string fileName, string path)
    {
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"Contents of {fileName} in {path}."));
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        multipart.Add(file, "files", fileName);
        multipart.Add(new StringContent(path), "path");
        return await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", multipart);
    }

    private async Task<List<string>> StoredPathsAsync(string containerId)
    {
        var connector = fixture.Factory.Services.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);
        try
        {
            return [.. (await connector.ListFilesAsync()).Select(f => f.Path)];
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }

    private async Task<List<string>> RecordedPathsAsync(string containerId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var documents = await scope.ServiceProvider.GetRequiredService<IDocumentStore>()
            .ListAsync(Guid.Parse(containerId), take: int.MaxValue);
        return [.. documents.Select(d => d.Path)];
    }

    private record ContainerDto(string Id, string Name);
}
