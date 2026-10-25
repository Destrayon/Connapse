using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Services;
using FluentAssertions;
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
            failing.StoreAsync(Arg.Any<Document>(), Arg.Any<CancellationToken>())
                .Returns<StoreResult>(_ => throw new InvalidOperationException("the record failed to store"));
            failing.StoreInFolderAsync(Arg.Any<Document>(), Arg.Any<Guid>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
                .Returns<Task<StoreResult?>>(async ci =>
                {
                    await ci.ArgAt<Func<CancellationToken, Task>>(2)(ci.ArgAt<CancellationToken>(3));
                    throw new InvalidOperationException("the record failed to store");
                });
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
