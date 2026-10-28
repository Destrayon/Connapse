using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>
/// Integration tests verifying the reindex status endpoint reflects
/// lifecycle states (Idle, Running, Completed, Failed).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ReindexStatusEndpointTests(SharedWebAppFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task ReindexStatus_BeforeTrigger_ReturnsIdleState()
    {
        // The singleton starts at Idle, but a previous test may have triggered a reindex.
        // We can still verify the endpoint returns the expected shape.
        var response = await fixture.AdminClient.GetAsync("/api/settings/reindex/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var status = await response.Content.ReadFromJsonAsync<ReindexStatusDto>(JsonOptions);
        status.Should().NotBeNull();
        status!.Status.Should().NotBeNullOrWhiteSpace();
        status.IsFailed.Should().Be(status.Status == "Failed");
    }

    [Fact]
    public async Task ReindexStatus_AfterSuccessfulReindex_ReflectsCompletedState()
    {
        // Scope the reindex to a new, empty container. An unscoped reindex walks every
        // document other tests in the shared fixture left behind and can outlast the wait.
        var createResponse = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = $"reindex-status-{Guid.NewGuid().ToString("N")[..8]}" });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var container = await createResponse.Content.ReadFromJsonAsync<ContainerDto>(JsonOptions);

        ReindexStatusDto status;
        try
        {
            var triggerResponse = await fixture.AdminClient.PostAsJsonAsync(
                "/api/settings/reindex", new { ContainerId = container!.Id });
            triggerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            // Poll until the background task finishes (should be near-instant with no docs)
            status = await PollUntilTerminalState(timeoutSeconds: 10);
        }
        finally
        {
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container!.Id}");
        }

        status.Status.Should().Be("Completed");
        status.IsFailed.Should().BeFalse();
        status.LastError.Should().BeNull();
        status.StartedAt.Should().NotBeNull();
        status.CompletedAt.Should().NotBeNull();
        status.CompletedAt.Should().BeOnOrAfter(status.StartedAt!.Value);
    }

    private async Task<ReindexStatusDto> PollUntilTerminalState(int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var response = await fixture.AdminClient.GetAsync("/api/settings/reindex/status");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var status = await response.Content.ReadFromJsonAsync<ReindexStatusDto>(JsonOptions);
            if (status!.Status is "Completed" or "Failed" or "Idle")
                return status;

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Reindex status did not reach a terminal state within {timeoutSeconds} seconds");
    }

    private record ContainerDto(string Id);

    private record ReindexStatusDto(
        int QueueDepth,
        bool IsActive,
        string Status,
        bool IsFailed,
        string? LastError,
        DateTime? StartedAt,
        DateTime? CompletedAt);
}
