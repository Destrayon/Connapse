using System.Net.Http.Json;
using Connapse.Core.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// Self-tests for the harness (#790): each surface really drives the shared host, and the capture
/// reads back real state, audit rows and queued jobs. The cross-surface comparisons themselves
/// are #792's; these only prove the instrument works before anything is measured with it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CrossSurfaceHarnessTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Rest_CreateContainer_ReportsTheRowAndItsAuditEntry()
    {
        string name = UniqueName();

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                var response = await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name, description = "made over REST" });
                return ((int)response.StatusCode).ToString();
            },
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name));

        outcome.Result.Should().Be("201");
        outcome.State.Should().Equal("container {name} description=made over REST");
        outcome.Audit.Should().Equal("container.created container {id1} actor=user");
        outcome.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_CreateContainer_ReportsTheRow()
    {
        string name = UniqueName();
        await using var mcp = await Surfaces.McpAsync(fixture);

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                var result = await mcp.CallToolAsync("container_create",
                    new Dictionary<string, object?> { ["name"] = name, ["description"] = "made over MCP" });
                return result.IsError == true ? "error" : "ok";
            },
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name));

        outcome.Result.Should().Be("ok");
        outcome.State.Should().Equal("container {name} description=made over MCP");
    }

    [Fact]
    public async Task Rest_Upload_ReportsTheQueuedIngestionJob()
    {
        string name = UniqueName();
        var created = await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name });
        created.EnsureSuccessStatusCode();
        string containerId = (await created.Content.ReadFromJsonAsync<ContainerDto>())!.Id;

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent("harness self-test"u8.ToArray()), "files", "note.txt");
                form.Add(new StringContent("/"), "path");
                var response = await Surfaces.Rest(fixture).PostAsync($"/api/containers/{containerId}/files", form);
                return ((int)response.StatusCode).ToString();
            },
            async sp =>
            {
                var docs = await sp.GetRequiredService<IDocumentStore>().ListAsync(Guid.Parse(containerId));
                return new StateProjection(docs.Select(d => $"document {d.FileName} in {d.Path}").ToList(), docs.Select(d => d.Id).ToList());
            });

        outcome.State.Should().Equal("document note.txt in /note.txt");
        outcome.Jobs.Should().Contain("IIngestionJobs.IngestAsync");
    }

    private static async Task<StateProjection> ContainerByNameAsync(IServiceProvider services, string name)
    {
        var container = await services.GetRequiredService<IContainerStore>().GetByNameAsync(name);
        return container is null
            ? new StateProjection([$"no container {name}"], [])
            : new StateProjection([$"container {name} description={container.Description}"], [container.Id]);
    }

    private static string UniqueName() => $"harness-{Guid.NewGuid():N}"[..24];

    private static Dictionary<string, string> Placeholders(string name) => new() { [name] = "{name}" };

    private sealed record ContainerDto(string Id);
}
