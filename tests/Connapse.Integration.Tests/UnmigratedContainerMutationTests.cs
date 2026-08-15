using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Storage.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// The Phase 2 backfill (#350) runs at startup and is deliberately allowed to fail without
/// blocking boot, and it skips entirely when another replica holds the advisory lock. In
/// that window a legacy external container still exists in the containers table. These
/// tests pin that its contents stay immutable, which is the guarantee ContainerWriteGuard
/// used to provide before #351 removed it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class UnmigratedContainerMutationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// Inserts a legacy external container directly, bypassing the API restriction, to
    /// simulate an install whose backfill has not yet succeeded.
    /// </summary>
    private static async Task<(Guid ContainerId, Guid DocumentId)> SeedUnmigratedAsync(
        KnowledgeDbContext ctx, ConnectorType type, string config)
    {
        var containerId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO containers (id, name, connector_type, connector_config, created_at, updated_at) VALUES ({0}, {1}, {2}, CAST({3} AS jsonb), now(), now())",
            containerId, ShortName("unmigrated"), (int)type, config);

        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO documents (id, container_id, source_id, file_name, path, content_hash, size_bytes, created_at) VALUES ({0}, {1}, NULL, 'x.md', '/x.md', '', 1, now())",
            documentId, containerId);

        return (containerId, documentId);
    }

    [Fact]
    public async Task DeleteDocument_InUnmigratedExternalContainer_IsRejected()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();

        var (containerId, documentId) = await SeedUnmigratedAsync(
            ctx, ConnectorType.S3, """{"bucketName":"b","region":"us-east-1"}""");

        var response = await fixture.AdminClient.DeleteAsync($"/api/containers/{containerId}/files/{documentId}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var fresh = await factory.CreateDbContextAsync();
        (await fresh.Documents.AnyAsync(d => d.Id == documentId))
            .Should().BeTrue("the document must survive a rejected delete");
    }

    [Fact]
    public async Task CreateFolder_InUnmigratedExternalContainer_IsRejected()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();

        var (containerId, _) = await SeedUnmigratedAsync(
            ctx, ConnectorType.AzureBlob, """{"storageAccountName":"a","containerName":"c"}""");

        var response = await fixture.AdminClient.PostAsJsonAsync(
            $"/api/containers/{containerId}/folders", new { Path = "/newfolder" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadFile_ToUnmigratedExternalContainer_IsRejected()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();

        var (containerId, _) = await SeedUnmigratedAsync(
            ctx, ConnectorType.Filesystem, """{"rootPath":"/data"}""");

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent("hello"u8.ToArray());
        content.Add(fileContent, "file", "hello.txt");

        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", content);

        // UploadService already fails closed here via the IWritableConnector cast; this
        // pins that it stays that way.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnprocessableEntity);
    }
}
