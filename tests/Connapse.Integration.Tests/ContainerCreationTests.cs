using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Creating a container is now a two-field operation. This replaces the restriction tests that
/// asserted <c>POST /api/containers</c> refused every non-managed connector type: since #353 the
/// request has no connector field at all, so the refusal is a compile-time property of the DTO
/// rather than a runtime check something could regress past.
/// <para>
/// What is still worth asserting is what happens to a client that has not caught up.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class ContainerCreationTests(SharedWebAppFixture fixture)
{
    private static string ShortName(string p) => $"{p}-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task CreateContainer_NameAndDescriptionOnly_Succeeds()
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = ShortName("plain"), Description = "a container" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("AzureBlob")]
    [InlineData("Filesystem")]
    public async Task CreateContainer_LegacyBodyNamingAConnector_IgnoresItAndCreatesManagedStorage(string connectorType)
    {
        // A pre-#353 client still sends connectorType. The field no longer binds, so the extra
        // property is ignored and a managed container is created — the ordinary REST behaviour
        // for a removed field, and recorded here so it is a decision rather than a surprise.
        // It is safe precisely because there is nothing left for the value to select: no code
        // path can produce a container backed by anything but managed storage.
        var name = ShortName("legacy");

        var response = await fixture.AdminClient.PostAsJsonAsync("/api/containers",
            new { Name = name, ConnectorType = connectorType, ConnectorConfig = """{"bucketName":"b"}""" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // And the response carries no connector field back, so a client cannot conclude from it
        // that its request was honoured.
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("connectorType", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("connectorConfig", out _).Should().BeFalse();
    }
}
