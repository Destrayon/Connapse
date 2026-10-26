using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>
/// An upload whose record fails to store after its file is written leaves no stored file behind (CON-88). The file
/// goes to the real managed storage; only the record store is made to fail.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FailedUploadCleanupTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData(null)]
    [InlineData("/docs")]
    public async Task Upload_RecordFailsAfterTheFileIsStored_LeavesNoStoredFile(string? path)
    {
        var containerId = await CreateContainerAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var services = scope.ServiceProvider;
            var documents = services.GetRequiredService<IDocumentStore>();
            var failing = Substitute.For<IDocumentStore>();
            failing.GetByPathAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => documents.GetByPathAsync(ci.ArgAt<Guid>(0), ci.ArgAt<string>(1), ci.ArgAt<CancellationToken>(2)));
            // The real store runs, and fails right after the file is written, so its transaction rolls back.
            failing.StoreInFolderAsync(Arg.Any<Document>(), Arg.Any<Guid?>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns(ci => documents.StoreInFolderAsync(ci.ArgAt<Document>(0), ci.ArgAt<Guid?>(1), async token =>
                {
                    await ci.ArgAt<Func<CancellationToken, Task>>(2)(token);
                    throw new InvalidOperationException("the record failed to store");
                }, ci.ArgAt<CancellationToken>(3)));
            failing.RemoveUnrecordedFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns(ci => documents.RemoveUnrecordedFileAsync(ci.ArgAt<Guid>(0), ci.ArgAt<string>(1),
                    ci.ArgAt<Func<CancellationToken, Task>>(2), ci.ArgAt<CancellationToken>(3)));
            var uploads = new UploadService(
                services.GetRequiredService<IContainerStore>(),
                services.GetRequiredService<IManagedStorageProvider>(),
                services.GetRequiredService<IFolderStore>(),
                services.GetRequiredService<IIngestionQueue>(),
                failing,
                services.GetRequiredService<IFileTypeValidator>(),
                services.GetRequiredService<IAuditLogger>(),
                NullLogger<UploadService>.Instance);

            var act = () => uploads.UploadAsync(new UploadRequest(
                Guid.Parse(containerId), "orphan.txt", new MemoryStream(Encoding.UTF8.GetBytes("never recorded")),
                Guid.NewGuid(), path, "text/plain", IngestedVia: "API"));

            await act.Should().ThrowAsync<InvalidOperationException>();
            (await StoredPathsAsync(containerId)).Should().BeEmpty("a file no record points to must not stay in storage");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task RemoveUnrecordedFile_ARecordHoldsThePath_KeepsTheFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            (await documents.StoreInFolderAsync(DocumentAt(containerId, "/kept.txt"), null,
                token => WriteAsync(containerId, "/kept.txt", "kept", token))).Should().NotBeNull();

            (await documents.RemoveUnrecordedFileAsync(Guid.Parse(containerId), "/kept.txt",
                token => DeleteAsync(containerId, "/kept.txt", token))).Should().BeFalse();

            (await StoredPathsAsync(containerId)).Should().Equal("/kept.txt");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task RemoveUnrecordedFile_WhileAnotherUploadToThePathIsWriting_WaitsAndKeepsThatUploadsFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var documents = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var written = new TaskCompletionSource();
            var release = new TaskCompletionSource();

            // Upload B has written its file and stalls before storing its record, so no record holds the path yet.
            var upload = documents.StoreInFolderAsync(DocumentAt(containerId, "/same.txt"), null, async token =>
            {
                await WriteAsync(containerId, "/same.txt", "from B", token);
                written.SetResult();
                await release.Task;
            });
            await written.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The failed upload A cleans up the same path meanwhile.
            var cleanup = Task.Run(async () =>
            {
                using var cleanupScope = fixture.Factory.Services.CreateScope();
                return await cleanupScope.ServiceProvider.GetRequiredService<IDocumentStore>().RemoveUnrecordedFileAsync(
                    Guid.Parse(containerId), "/same.txt", token => DeleteAsync(containerId, "/same.txt", token));
            });
            await WaitUntilAnAdvisoryLockIsAwaitedAsync();
            cleanup.IsCompleted.Should().BeFalse("the cleanup must wait for the upload writing to the same path");

            release.SetResult();
            (await upload.WaitAsync(TimeSpan.FromSeconds(30))).Should().NotBeNull();
            (await cleanup.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeFalse("upload B's record now holds the path");

            (await ReadAsync(containerId, "/same.txt")).Should().Be("from B");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    private static Document DocumentAt(string containerId, string path) =>
        new(Guid.NewGuid().ToString(), containerId, path[(path.LastIndexOf('/') + 1)..], "text/plain", path, 4, DateTime.UtcNow, []);

    private IWritableConnector Connector(string containerId) =>
        (IWritableConnector)fixture.Factory.Services.GetRequiredService<IManagedStorageProvider>().CreateConnector(containerId);

    private async Task WriteAsync(string containerId, string path, string text, CancellationToken ct)
    {
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await Connector(containerId).WriteFileAsync(path, content, "text/plain", ct);
    }

    private Task DeleteAsync(string containerId, string path, CancellationToken ct) =>
        Connector(containerId).DeleteFileAsync(path, ct);

    private async Task<string> ReadAsync(string containerId, string path)
    {
        using var reader = new StreamReader(await Connector(containerId).ReadFileAsync(path));
        return await reader.ReadToEndAsync();
    }

    private async Task WaitUntilAnAdvisoryLockIsAwaitedAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = await factory.CreateDbContextAsync();
            var waiting = await context.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted AND locktype = 'advisory'")
                .ToListAsync();
            if (waiting[0] > 0)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The cleanup never waited on the path lock.");
    }

    private async Task<string> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = $"upfail-{Guid.NewGuid():N}"[..20] });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions))!.Id;
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

    private record ContainerDto(string Id, string Name);
}
