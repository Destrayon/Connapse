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
            async () => await Surfaces.ResultAsync(await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name, description = "made over REST" })),
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name));

        outcome.Result.Should().Be("ok", "the id the response names is the container it created");
        outcome.State.Should().Equal("container {name} description=made over REST");
        outcome.Audit.Should().ContainSingle().Which.Should().StartWith("container.created container {id1} actor={admin} details=");
        outcome.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_CreateContainer_ReportsTheRow()
    {
        string name = UniqueName();
        await using var mcp = await Surfaces.McpAsync(fixture);

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () => Surfaces.Result(await mcp.CallToolAsync("container_create",
                new Dictionary<string, object?> { ["name"] = name, ["description"] = "made over MCP" })),
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name));

        outcome.Result.Should().Be("ok", "the same result REST gives");
        outcome.State.Should().Equal("container {name} description=made over MCP");
    }

    [Fact]
    public async Task RestAndMcp_DuplicateCreate_ReportTheSameError()
    {
        string name = UniqueName();
        (await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name })).EnsureSuccessStatusCode();
        await using var mcp = await Surfaces.McpAsync(fixture);

        string rest = await Surfaces.ResultAsync(await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name }));
        string viaMcp = Surfaces.Result(await mcp.CallToolAsync("container_create", new Dictionary<string, object?> { ["name"] = name }));

        rest.Should().Be("error:conflict");
        viaMcp.Should().Be(rest);
    }

    [Fact]
    public async Task Capture_IgnoresAuditRowsWrittenBeforeTheAction()
    {
        string name = UniqueName();
        (await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name })).EnsureSuccessStatusCode();

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () => await Surfaces.ResultAsync(await Surfaces.Rest(fixture).GetAsync("/api/containers")),
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name));

        outcome.State.Should().ContainSingle();
        outcome.Audit.Should().BeEmpty("the container.created row was written before the action began");
    }

    [Fact]
    public async Task Capture_FindsTheAuditRowOfADeletedResourceByItsKnownId()
    {
        string name = UniqueName();
        var created = await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name });
        created.EnsureSuccessStatusCode();
        string containerId = (await created.Content.ReadFromJsonAsync<ContainerDto>())!.Id;

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () => await Surfaces.ResultAsync(await Surfaces.Rest(fixture).DeleteAsync($"/api/containers/{containerId}")),
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name),
            knownIds: [containerId]);

        outcome.Result.Should().Be("ok");
        outcome.State.Should().Equal("no container {name}");
        outcome.Audit.Should().ContainSingle().Which.Should().StartWith("container.deleted container {id1} actor={admin}");
    }

    [Fact]
    public async Task Rest_Upload_ReportsTheQueuedIngestionJobWithItsArguments()
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
                return await Surfaces.ResultAsync(await Surfaces.Rest(fixture).PostAsync($"/api/containers/{containerId}/files", form));
            },
            async sp =>
            {
                var docs = await sp.GetRequiredService<IDocumentStore>().ListAsync(Guid.Parse(containerId));
                return new StateProjection(docs.Select(d => $"document {d.FileName} in {d.Path}").ToList(), docs.Select(d => d.Id).ToList());
            },
            knownIds: [containerId]);

        outcome.State.Should().Equal("document note.txt in /note.txt");
        outcome.Jobs.Should().ContainSingle().Which.Should()
            .StartWith("""IIngestionJobs.IngestAsync("{id1}", {"DocumentId":"{id1}","FileName":"note.txt",""")
            .And.Contain("\"ContainerId\":\"{id2}\"").And.Contain("\"UploadedAt\":\"{time}\"");
    }

    [Fact]
    public async Task Capture_IncludesJobsForOtherDocumentsOfTheContainer()
    {
        string name = UniqueName();
        var created = await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name });
        created.EnsureSuccessStatusCode();
        string containerId = (await created.Content.ReadFromJsonAsync<ContainerDto>())!.Id;

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent("first"u8.ToArray()), "files", "a.txt");
                form.Add(new ByteArrayContent("second"u8.ToArray()), "files", "b.txt");
                form.Add(new StringContent("/"), "path");
                return await Surfaces.ResultAsync(await Surfaces.Rest(fixture).PostAsync($"/api/containers/{containerId}/files", form));
            },
            async sp =>
            {
                // Lists only a.txt, as a projection blind to a misdirected job would.
                var docs = (await sp.GetRequiredService<IDocumentStore>().ListAsync(Guid.Parse(containerId))).Where(d => d.FileName == "a.txt").ToList();
                return new StateProjection(docs.Select(d => $"document {d.FileName}").ToList(), docs.Select(d => d.Id).ToList());
            },
            knownIds: [containerId]);

        outcome.Jobs.Should().HaveCount(2, "the job for b.txt is found through its container");
        outcome.Jobs.Should().ContainSingle(j => j.Contains("\"FileName\":\"b.txt\""))
            .Which.Should().StartWith("""IIngestionJobs.IngestAsync("{id3}",""", "b.txt gets its own token, not a raw id");
    }

    [Fact]
    public async Task Capture_FlagsASuccessThatNamesTheWrongId()
    {
        string name = UniqueName();
        string stranger = Guid.NewGuid().ToString();

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                await Surfaces.ResultAsync(await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name }));
                return $"ok id={stranger}";
            },
            sp => ContainerByNameAsync(sp, name),
            Placeholders(name));

        outcome.Result.Should().Be($"ok wrong-id={stranger}");
    }

    [Fact]
    public async Task Capture_FlagsAnUploadThatNamesItsContainerInsteadOfTheDocument()
    {
        string name = UniqueName();
        var created = await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name });
        created.EnsureSuccessStatusCode();
        string containerId = (await created.Content.ReadFromJsonAsync<ContainerDto>())!.Id;

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent("doc"u8.ToArray()), "files", "doc.txt");
                form.Add(new StringContent("/"), "path");
                await Surfaces.Rest(fixture).PostAsync($"/api/containers/{containerId}/files", form);
                return $"ok id={containerId}";
            },
            async sp =>
            {
                var docs = await sp.GetRequiredService<IDocumentStore>().ListAsync(Guid.Parse(containerId));
                return new StateProjection(docs.Select(d => $"document {d.FileName}").ToList(), docs.Select(d => d.Id).ToList());
            },
            knownIds: [containerId]);

        outcome.Result.Should().Be("ok wrong-id={id2}", "the container is touched, but it isn't what an upload answers with");
    }

    [Fact]
    public void McpResult_ReadsTheIdLineNotAUuidInAFilename()
    {
        const string doc = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var result = new ModelContextProtocol.Protocol.CallToolResult
        {
            Content = [new ModelContextProtocol.Protocol.TextContentBlock
            {
                Text = $"File 'notes ID: 11111111-1111-1111-1111-111111111111.txt' uploaded.\nDocument ID: {doc}\nJob ID: 7",
            }],
        };

        Surfaces.Result(result).Should().Be($"ok id={doc}");
    }

    [Fact]
    public async Task RestAndMcp_DeleteNonEmptyContainer_ReportTheirErrors()
    {
        string name = UniqueName();
        var created = await Surfaces.Rest(fixture).PostAsJsonAsync("/api/containers", new { name });
        created.EnsureSuccessStatusCode();
        string containerId = (await created.Content.ReadFromJsonAsync<ContainerDto>())!.Id;
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("keep"u8.ToArray()), "files", "keep.txt");
            form.Add(new StringContent("/"), "path");
            (await Surfaces.Rest(fixture).PostAsync($"/api/containers/{containerId}/files", form)).EnsureSuccessStatusCode();
        }
        await using var mcp = await Surfaces.McpAsync(fixture);

        string rest = await Surfaces.ResultAsync(await Surfaces.Rest(fixture).DeleteAsync($"/api/containers/{containerId}"));
        string viaMcp = Surfaces.Result(await mcp.CallToolAsync("container_delete", new Dictionary<string, object?> { ["containerId"] = containerId }));

        rest.Should().Be("error:validation");
        // Known gap, measured rather than fixed here: MCP's container_delete throws on a non-empty
        // container instead of returning its "is not empty" message, so the SDK answers with a
        // generic error. #792's comparisons list it.
        viaMcp.Should().Be("error:unknown");
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
