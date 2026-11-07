using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// CON-265: SFTP is how a machine's folders reach Connapse now that Filesystem sources are gone, so this drives one
/// through the real host over REST against a real server: connect, add a source, sync, and pick up a new file.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public sealed class SftpSourceSyncTests(SharedWebAppFixture fixture)
{
    private sealed record Synced(int Upserted, string? Error);

    [Fact]
    public async Task SyncSource_OverSftp_ConnectsAndIndexesTheFolder()
    {
        SftpServerFixture server = await fixture.SftpAsync();
        string root = $"/sftp-sync-{Guid.NewGuid():N}";
        await server.WriteFileAsync($"{root}/first.md", "indexed over sftp");
        Connection connection = await fixture.CreateSftpConnectionAsync($"sftp-{Guid.NewGuid():N}"[..20], root);

        var tested = await fixture.AdminClient.PostAsJsonAsync($"/api/v1/connections/{connection.Id}/test", new { });
        (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean().Should().BeTrue();

        var created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", new
        {
            name = $"sftp-{Guid.NewGuid():N}"[..20],
            connectionId = connection.Id,
            scope = new { type = "sftp" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        Guid sourceId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await SyncAsync(sourceId)).Should().Be(new Synced(1, null));

        await server.WriteFileAsync($"{root}/second.md", "added after the first sync");
        (await SyncAsync(sourceId)).Should().Be(new Synced(1, null));
    }

    private async Task<Synced> SyncAsync(Guid sourceId)
    {
        var response = await fixture.AdminClient.PostAsync($"/api/sources/{sourceId}/sync", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Synced>())!;
    }
}
