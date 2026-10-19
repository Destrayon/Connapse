using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// #792's migration guard: what every Sources action means today, recorded before the Sources
/// migration starts so a change in it shows up as a snapshot diff. These snapshots are the
/// semantic <see cref="Outcome"/> (result, the source row, audit rows, jobs), never the wire shape,
/// which a later ticket changes on purpose. A changed .verified file is reviewed like test code.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class SourcesSnapshotTests(SharedWebAppFixture fixture) : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("cs-sources-").FullName;
    private readonly List<string> _names = [];
    private readonly List<Guid> _connections = [];

    public static TheoryData<string, string> Rows() => new()
    {
        { "list", Case.Valid }, { "list", Case.UnderScoped },
        { "get", Case.Valid }, { "get", Case.Invalid }, { "get", Case.UnderScoped },
        { "create", Case.Valid }, { "create", Case.Invalid }, { "create", Case.UnderScoped }, { "create", Case.Preflight },
        { "update", Case.Valid }, { "update", Case.Invalid }, { "update", Case.UnderScoped }, { "update", Case.Preflight },
        { "delete", Case.Valid }, { "delete", Case.Invalid }, { "delete", Case.UnderScoped }, { "delete", Case.Preflight },
        { "sync", Case.Valid }, { "sync", Case.UnderScoped }, { "sync", Case.Preflight },
        { "retry-failed", Case.Valid }, { "retry-failed", Case.UnderScoped }, { "retry-failed", Case.Preflight },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task Sources_Action_Case_MatchesCharacterizationSnapshot(string action, string @case)
    {
        File.WriteAllText(Path.Combine(_root, "doc.txt"), "a file the source mirrors");
        Guid connectionId = await SeedConnectionAsync();
        string name = $"cs-src-{Guid.NewGuid():N}"[..24];
        _names.AddRange([name, $"{name}-renamed"]);
        Guid? sourceId = action == "create" ? null : await CreateSourceAsync(connectionId, name);
        string unknown = Guid.NewGuid().ToString();
        // Viewer for an action that changes something, no role for a read: neither is enough.
        var principal = @case != Case.UnderScoped ? Principal.Admin : action is "list" or "get" ? Principal.NoRole : Principal.Viewer;
        // A source that doesn't exist: get's invalid case, and every preflight but create's (which names a missing connection).
        string target = (@case == Case.Invalid && action == "get") || @case == Case.Preflight ? unknown : sourceId?.ToString() ?? "";

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                using var rest = await Surfaces.RestAsync(fixture, principal);
                var response = action switch
                {
                    "list" => await rest.GetAsync("/api/sources"),
                    "get" => await rest.GetAsync($"/api/sources/{target}"),
                    "create" => await rest.PostAsJsonAsync("/api/sources", new
                    {
                        name = @case == Case.Invalid ? "" : name,
                        connectionId = @case == Case.Preflight ? Guid.Parse(unknown) : connectionId,
                        scopeJson = "{}",
                    }),
                    "update" => await rest.PatchAsJsonAsync($"/api/sources/{target}", new { name = @case == Case.Invalid ? "" : $"{name}-renamed" }),
                    // The name typed to confirm, as the Sources page asks: the invalid case types a wrong one.
                    "delete" => await rest.DeleteAsync($"/api/sources/{target}?confirmName={Uri.EscapeDataString(@case == Case.Invalid ? "not-the-name" : name)}"),
                    "sync" => await rest.PostAsync($"/api/sources/{target}/sync", null),
                    "retry-failed" => await rest.PostAsync($"/api/sources/{target}/retry-failed", null),
                    _ => throw new ArgumentOutOfRangeException(nameof(action)),
                };
                string result = await Surfaces.ResultAsync(response);
                if (action is "list" or "get" && result.StartsWith("ok", StringComparison.Ordinal))
                    result = $"{result} {((await response.Content.ReadAsStringAsync()).Contains(name, StringComparison.Ordinal) ? "found" : "not-found")}";
                return result;
            },
            sp => SourcesNamedAsync(sp, name, $"{name}-renamed"),
            new Dictionary<string, string>
            {
                [$"{name}-renamed"] = "{name}-renamed",
                [name] = "{name}",
                [_root] = "{root}",
                [JsonSerializer.Serialize(_root)[1..^1]] = "{root}",  // as it appears inside a job's JSON arguments
                [connectionId.ToString()] = "{connection}",
            },
            knownIds: [.. sourceId is { } id ? [id.ToString()] : Array.Empty<string>(), connectionId.ToString(), unknown],
            responseIds: [.. sourceId is { } sid ? [sid.ToString()] : Array.Empty<string>()]);

        // A synced file's path uses the OS separator ({root}\doc.txt on Windows, {root}/doc.txt on Linux CI).
        outcome = outcome with { Jobs = outcome.Jobs.Select(j => j.Replace("{root}\\\\", "{root}/", StringComparison.Ordinal)).ToList() };
        await Verify(outcome).UseDirectory("Snapshots").UseParameters(action, @case);
    }

    public static TheoryData<string, string> PageRows() => new()
    {
        { "list", Case.Valid }, { "list", Case.UnderScoped },
        { "sync", Case.Valid }, { "sync", Case.UnderScoped },
        { "pause", Case.Valid }, { "pause", Case.UnderScoped },
        { "delete", Case.Valid }, { "delete", Case.Invalid }, { "delete", Case.UnderScoped },
    };

    /// <summary>
    /// The same guard for the Sources page's own actions, which the migration also moves. Creating a
    /// source there is a wizard per provider, so create is guarded through REST above.
    /// </summary>
    [Theory]
    [MemberData(nameof(PageRows))]
    public async Task Sources_PageAction_Case_MatchesCharacterizationSnapshot(string action, string @case)
    {
        File.WriteAllText(Path.Combine(_root, "doc.txt"), "a file the source mirrors");
        Guid connectionId = await SeedConnectionAsync();
        string name = $"cs-src-{Guid.NewGuid():N}"[..24];
        _names.AddRange([name, $"{name}-renamed"]);
        Guid sourceId = await CreateSourceAsync(connectionId, name);
        var principal = @case != Case.UnderScoped ? Principal.Admin : action == "list" ? Principal.NoRole : Principal.Viewer;

        var outcome = await SideEffects.CaptureAsync(fixture,
            async () =>
            {
                using var ui = await Surfaces.UiAsync(fixture, principal);
                var page = ui.Context.Render<Connapse.Web.Components.Pages.Sources>();
                // The rows load after the table's header renders; a source that never appears isn't shown to this user.
                AngleSharp.Dom.IElement? Row() => page.FindAll("tr").FirstOrDefault(r => r.QuerySelector(".fw-medium")?.TextContent.Trim() == name);
                try
                {
                    page.WaitForAssertion(() => Row().Should().NotBeNull(), TimeSpan.FromSeconds(10));
                }
                catch (Exception)
                {
                    // Not listed: reported below as not found.
                }
                var row = Row();
                if (action == "list")
                    return row is null ? "ok not-found" : "ok found";
                if (row is null)
                    return "error:not_found";
                var button = row.QuerySelectorAll("button").FirstOrDefault(b => b.TextContent.Contains(action switch
                {
                    "sync" => "Sync",
                    "pause" => "Pause syncing",
                    _ => "Delete",
                }, StringComparison.Ordinal));
                if (button is null)
                    return "error:forbidden";
                await button.ClickAsync(new());
                if (action == "delete")
                {
                    page.Find("#delete-source-confirm").Input(@case == Case.Invalid ? "not-the-name" : name);
                    if (page.Find("#delete-source-submit").HasAttribute("disabled"))
                        return "error:validation";
                    await page.Find("#delete-source-submit").ClickAsync(new());
                }
                // Done when nothing is left syncing or awaiting confirmation; an alert is the action's failure.
                page.WaitForAssertion(() => page.FindAll(".spinner-border-sm").Should().BeEmpty(), TimeSpan.FromSeconds(15));
                return page.FindAll(".alert-danger").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : "ok";
            },
            sp => SourcesNamedAsync(sp, name),
            new Dictionary<string, string>
            {
                [name] = "{name}",
                [_root] = "{root}",
                [JsonSerializer.Serialize(_root)[1..^1]] = "{root}",
                [connectionId.ToString()] = "{connection}",
            },
            knownIds: [sourceId.ToString(), connectionId.ToString()],
            responseIds: [sourceId.ToString()]);

        outcome = outcome with { Jobs = outcome.Jobs.Select(j => j.Replace("{root}\\\\", "{root}/", StringComparison.Ordinal)).ToList() };
        await Verify(outcome).UseDirectory("Snapshots").UseParameters(action, @case);
    }

    private async Task<Guid> SeedConnectionAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
        var connection = await connections.CreateAsync(
            new CreateConnectionRequest($"cs-fs-{Guid.NewGuid():N}"[..20], ConnectionProvider.Filesystem, JsonSerializer.Serialize(new { allowedRoot = _root })),
            createdByUserId: null);
        _connections.Add(connection.Id);
        return connection.Id;
    }

    private async Task<Guid> CreateSourceAsync(Guid connectionId, string name)
    {
        var response = await fixture.AdminClient.PostAsJsonAsync("/api/sources", new { name, connectionId, scopeJson = "{}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<StateProjection> SourcesNamedAsync(IServiceProvider services, params string[] names)
    {
        var sources = await services.GetRequiredService<ISourceStore>().ListAsync(take: int.MaxValue);
        var mine = sources.Where(s => names.Contains(s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        return new StateProjection(
            mine.Count == 0 ? ["no source"] : mine.Select(s => $"source {s.Name} enabled={s.Enabled} connection={s.ConnectionId}").ToList(),
            mine.Select(s => s.Id.ToString()).ToList());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes the sources and connections a row made, then its folder, so tests that list them don't see these.</summary>
    public async Task DisposeAsync()
    {
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
            foreach (var source in (await sources.ListAsync(take: int.MaxValue)).Where(x => _names.Contains(x.Name)))
                await sources.DeleteAsync(source.Id);
            var connections = scope.ServiceProvider.GetRequiredService<IConnectionStore>();
            foreach (Guid id in _connections)
                await connections.DeleteAsync(id);
        }
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A sync can still be reading it; the OS temp cleanup takes it later.
        }
    }
}
