using System.Net;
using FluentAssertions;

namespace Connapse.Integration.Tests;

/// <summary>Routes under: /api/batches/</summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class BatchesEndpointsTests(SharedWebAppFixture fixture)
{
    [Theory]
    [InlineData("999999999")]
    [InlineData("not-a-job-id")]
    public async Task GetStatus_NoSuchJob_Returns404(string id)
    {
        var response = await fixture.AdminClient.GetAsync($"/api/batches/{id}/status");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
