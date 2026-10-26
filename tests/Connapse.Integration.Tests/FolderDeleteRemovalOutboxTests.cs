using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Connapse.Storage.Data;
using Connapse.Storage.Folders;
using Connapse.Web.Services;
using FluentAssertions;
using Hangfire;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Connapse.Integration.Tests;

/// <summary>
/// A stored file a folder delete couldn't remove is saved as owed with the delete, and a background job removes it
/// later, reading only the database, so a restart in between loses nothing (CON-100).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class FolderDeleteRemovalOutboxTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Past the delete's own attempt, so the job takes the entries it left.</summary>
    private static readonly TimeSpan PastGrace = PostgresFolderStore.InlineRemovalGrace + TimeSpan.FromMinutes(1);

    [Fact]
    public async Task FolderStoreDelete_StorageFailsEveryRemoval_JobAfterRestartRemovesTheFiles()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            foreach (string name in new[] { "a.txt", "b.txt" })
                (await UploadAsync(containerId, name, "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var store = FolderStore(scope.ServiceProvider, StorageFailingDeletes());
                (await store.DeleteAsync(Guid.Parse(containerId), "/docs/")).Should().BeTrue();
            }

            (await StoredPathsAsync(containerId)).Should().BeEquivalentTo(["/docs/a.txt", "/docs/b.txt"]);
            (await OwedPathsAsync(containerId)).Should().BeEquivalentTo(["/docs/a.txt", "/docs/b.txt"],
                "the removals are saved with the delete");

            // After a restart nothing from the delete is left in memory: a fresh scope's job has only the database.
            using (var restarted = fixture.Factory.Services.CreateScope())
                await Job(restarted.ServiceProvider, restarted.ServiceProvider.GetRequiredService<IManagedStorageProvider>(), PastGrace)
                    .RunAsync(CancellationToken.None);

            (await StoredPathsAsync(containerId)).Should().BeEmpty("the job removed the files the delete couldn't");
            (await OwedPathsAsync(containerId)).Should().BeEmpty("a removed file is no longer owed");
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task FolderStoreDelete_StorageUnreachable_JobRemovesTheFilesLater()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            (await UploadAsync(containerId, "a.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var unreachable = Substitute.For<IManagedStorageProvider>();
                unreachable.CreateConnector(Arg.Any<string>()).Returns(_ => throw new IOException("Storage is down."));
                (await FolderStore(scope.ServiceProvider, unreachable).DeleteAsync(Guid.Parse(containerId), "/docs/")).Should().BeTrue();
            }

            (await OwedPathsAsync(containerId)).Should().Equal(["/docs/a.txt"]);

            using (var scope = fixture.Factory.Services.CreateScope())
                await Job(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>(), PastGrace)
                    .RunAsync(CancellationToken.None);

            (await StoredPathsAsync(containerId)).Should().BeEmpty();
            (await OwedPathsAsync(containerId)).Should().BeEmpty();
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task StoredFileRemovalJob_StorageStillFails_KeepsTheEntryAndWaitsLonger()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            (await UploadAsync(containerId, "a.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using var scope = fixture.Factory.Services.CreateScope();
            var failing = StorageFailingDeletes();
            (await FolderStore(scope.ServiceProvider, failing).DeleteAsync(Guid.Parse(containerId), "/docs/")).Should().BeTrue();

            await Job(scope.ServiceProvider, failing, PastGrace).RunAsync(CancellationToken.None);

            var entry = (await OwedAsync(containerId)).Should().ContainSingle().Subject;
            entry.Attempts.Should().Be(1);
            entry.LastError.Should().Contain("refused");
            entry.NextAttemptAt.Should().BeAfter(DateTime.UtcNow + PastGrace, "a failed attempt is retried later, not on the next run");
            (await StoredPathsAsync(containerId)).Should().Equal(["/docs/a.txt"]);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public async Task StoredFileRemovalJob_PathUploadedAgain_KeepsTheNewFile()
    {
        var containerId = await CreateContainerAsync();
        try
        {
            (await UploadAsync(containerId, "a.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using (var scope = fixture.Factory.Services.CreateScope())
                (await FolderStore(scope.ServiceProvider, StorageFailingDeletes())
                    .DeleteAsync(Guid.Parse(containerId), "/docs/")).Should().BeTrue();

            (await UploadAsync(containerId, "a.txt", "/docs")).StatusCode.Should().Be(HttpStatusCode.OK);

            using (var scope = fixture.Factory.Services.CreateScope())
                await Job(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IManagedStorageProvider>(), PastGrace)
                    .RunAsync(CancellationToken.None);

            (await StoredPathsAsync(containerId)).Should().Equal(["/docs/a.txt"], "a record points to the path again");
            (await OwedPathsAsync(containerId)).Should().BeEmpty("the newer upload's file is not owed a removal");
        }
        finally
        {
            using var scope = fixture.Factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IFolderStore>().DeleteAsync(Guid.Parse(containerId), "/docs/");
            await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}");
        }
    }

    [Fact]
    public void StoredFileRemovalJob_IsARecurringJobInHangfiresStorage()
    {
        // Hangfire keeps recurring jobs in its database, so the job is scheduled again after a restart.
        using var connection = fixture.Factory.Services.GetRequiredService<JobStorage>().GetConnection();
        connection.GetRecurringJobs().Should().Contain(j => j.Id == StoredFileRemovalJob.RecurringJobId);
    }

    private static IManagedStorageProvider StorageFailingDeletes()
    {
        var storage = Substitute.For<IManagedStorageProvider>();
        storage.CreateConnector(Arg.Any<string>()).Returns(_ =>
        {
            var failing = Substitute.For<IWritableConnector>();
            failing.DeleteFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task>(delete => throw new IOException($"Storage refused to delete {delete.Arg<string>()}."));
            return failing;
        });
        return storage;
    }

    private static PostgresFolderStore FolderStore(IServiceProvider services, IManagedStorageProvider storage) => new(
        services.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>(),
        services.GetRequiredService<IContainerStore>(),
        services.GetRequiredService<IDocumentStore>(),
        storage,
        NullLogger<PostgresFolderStore>.Instance);

    private static StoredFileRemovalJob Job(IServiceProvider services, IManagedStorageProvider storage, TimeSpan ahead) => new(
        services.GetRequiredService<IStoredFileRemovalStore>(),
        services.GetRequiredService<IDocumentStore>(),
        storage,
        new ShiftedTime(ahead),
        NullLogger<StoredFileRemovalJob>.Instance);

    private async Task<List<Storage.Data.Entities.StoredFileRemovalEntity>> OwedAsync(string containerId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        Guid id = Guid.Parse(containerId);
        return await context.StoredFileRemovals.AsNoTracking().Where(r => r.ContainerId == id).ToListAsync();
    }

    private async Task<List<string>> OwedPathsAsync(string containerId) =>
        [.. (await OwedAsync(containerId)).Select(r => r.Path)];

    private async Task<string> CreateContainerAsync()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { Name = $"fdout-{Guid.NewGuid():N}"[..20] });
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

    private sealed class ShiftedTime(TimeSpan ahead) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + ahead;
    }

    private record ContainerDto(string Id, string Name);
}
