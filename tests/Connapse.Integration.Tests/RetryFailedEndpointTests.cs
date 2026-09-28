using System.Net;
using System.Net.Http.Json;
using Connapse.Core;
using Connapse.Storage.Data;
using Connapse.Storage.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class RetryFailedEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task RetryFailed_Container_RequeuesOnlyItsFailedDocumentsWithAFreshBudget()
    {
        Guid containerId = Guid.NewGuid();
        Guid failed = Guid.NewGuid();
        Guid ready = Guid.NewGuid();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();
        await using (var ctx = await dbFactory.CreateDbContextAsync())
        {
            ctx.Containers.Add(new ContainerEntity
            {
                Id = containerId, Name = $"retry-{containerId:N}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            ctx.Documents.Add(new DocumentEntity
            {
                Id = failed, ContainerId = containerId, FileName = "failed.md", Path = "/failed.md", ContentHash = "",
                CreatedAt = DateTime.UtcNow, Metadata = [],
                IngestionStatus = DocumentStatus.FailedRetryable, AttemptCount = 4, ErrorMessage = "provider down",
            });
            ctx.Documents.Add(new DocumentEntity
            {
                Id = ready, ContainerId = containerId, FileName = "ready.md", Path = "/ready.md", ContentHash = "",
                CreatedAt = DateTime.UtcNow, Metadata = [], IngestionStatus = DocumentStatus.Ready,
            });
            await ctx.SaveChangesAsync();
        }

        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/retry-failed", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RetryResponse>())!.EnqueuedCount.Should().Be(1);

        await using var after = await dbFactory.CreateDbContextAsync();
        var readyAfter = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == ready);
        readyAfter.IngestionStatus.Should().Be(DocumentStatus.Ready, "only failed documents are retried");

        // The job may already have run and failed again on the missing file; what matters is that
        // this attempt began with a fresh budget and a new generation.
        var failedAfter = await after.Documents.AsNoTracking().SingleAsync(d => d.Id == failed);
        failedAfter.AttemptCount.Should().BeLessThan(4);
        failedAfter.Generation.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task RetryFailed_UnknownContainer_Returns404()
    {
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{Guid.NewGuid()}/retry-failed", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record RetryResponse(int EnqueuedCount);
}
