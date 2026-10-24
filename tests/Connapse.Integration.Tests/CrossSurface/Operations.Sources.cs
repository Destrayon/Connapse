using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>A case's source: on a filesystem connection of its own, over a folder of its own.</summary>
public sealed record SourceCase(Guid Id, string Name, Guid ConnectionId, string Root);

public static partial class Operations
{
    /// <summary>MCP reaches sources only through the generic tools Phase 5 adds.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoSourceTools = new Dictionary<string, string>
    {
        ["mcp"] = "MCP has no source tools; the generic connapse_api_* tools arrive in Phase 5 of #783",
    };

    /// <summary>The Sources operations (#835), on REST and the Sources pages.</summary>
    private static readonly Operation[] SourceOperations =
    [
        new("sources_list", [Case.Valid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                // The case's source is named after its "0cs-" container, so it sorts onto the first page.
                var response = await rest.GetAsync("/api/sources?skip=0&take=100");
                return await WithPayloadAsync(response, body => Listed(body.Contains(c.Source!.Name, StringComparison.Ordinal)));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<Sources>();
                page.WaitForAssertion(() => page.FindAll("table.table, .card-body.text-center, div.alert-dismissible.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (SourcesPageError(page) is { } error)
                    return error;
                return "ok " + Listed(page.FindAll("tr .fw-medium").Any(n => n.TextContent.Trim() == c.Source!.Name));
            },
            Project: (sp, c) => SourceStateAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
        },

        new("sources_get", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/sources/{SourceTarget(c, k)}");
                return await WithPayloadAsync(response, body => Found(body.Contains(c.Source!.Name, StringComparison.Ordinal)));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<SourceEdit>(ps => ps.Add(x => x.Id, Guid.Parse(SourceTarget(c, k))));
                page.WaitForAssertion(() => page.FindAll("h1, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (page.FindAll(".alert-danger").FirstOrDefault() is { } alert)
                    return Surfaces.ErrorKind(alert.TextContent.Trim());
                return "ok " + Found(page.Find("h1").TextContent.Trim() == c.Source!.Name);
            },
            Project: (sp, c) => SourceStateAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
        },
    ];

    /// <summary>The source a case targets: an unknown one for the invalid case, the case's own otherwise.</summary>
    private static string SourceTarget(CaseContext c, string @case) => @case == Case.Invalid ? c.Unknown : c.Source!.Id.ToString();

    /// <summary>
    /// The Sources page's own failure: its dismissible message, not a row's sync error, which another
    /// test's source in the shared database may show.
    /// </summary>
    private static string? SourcesPageError(IRenderedComponent<Sources> page) =>
        page.FindAll("div.alert-dismissible.alert-danger").FirstOrDefault() is { } alert
            ? Surfaces.ErrorKind(alert.TextContent.Trim())
            : null;

    /// <summary>A filesystem connection over a fresh folder, and a source on it named after the case's container.</summary>
    private static async Task<SourceCase> ArrangeSourceAsync(SharedWebAppFixture fixture, string name)
    {
        string root = Directory.CreateTempSubdirectory("cs-source-").FullName;
        File.WriteAllText(Path.Combine(root, "doc.txt"), Body);
        Connection connection;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            connection = await scope.ServiceProvider.GetRequiredService<IConnectionStore>().CreateAsync(
                new CreateConnectionRequest($"cs-fs-{Guid.NewGuid():N}"[..20], ConnectionProvider.Filesystem, JsonSerializer.Serialize(new { allowedRoot = root })),
                createdByUserId: null);
        }
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/sources", new { name, connectionId = connection.Id, scopeJson = "{}" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new SourceCase(id, name, connection.Id, root);
    }

    private static async Task CleanupSourceAsync(SharedWebAppFixture fixture, SourceCase source)
    {
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sources = scope.ServiceProvider.GetRequiredService<ISourceStore>();
            foreach (var s in (await sources.ListAsync(take: int.MaxValue)).Where(s => s.Name.StartsWith(source.Name, StringComparison.Ordinal)))
                await sources.DeleteAsync(s.Id);
            await scope.ServiceProvider.GetRequiredService<IConnectionStore>().DeleteAsync(source.ConnectionId);
        }
        try
        {
            Directory.Delete(source.Root, recursive: true);
        }
        catch (IOException)
        {
            // A sync can still be reading it; the OS temp cleanup takes it later.
        }
    }

    /// <summary>Every source named after the case, with what decides its next sync.</summary>
    private static async Task<StateProjection> SourceStateAsync(IServiceProvider services, CaseContext c)
    {
        var mine = (await services.GetRequiredService<ISourceStore>().ListAsync(take: int.MaxValue))
            .Where(s => s.Name.StartsWith(c.ContainerName, StringComparison.Ordinal))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var lines = mine.Count == 0
            ? new List<string> { "no source" }
            : mine.Select(s => $"source {s.Name} enabled={s.Enabled} description={s.Description} status={s.LastSyncStatus} synced={s.LastSyncedAt is not null}").ToList();
        return new StateProjection(lines, mine.Select(s => s.Id.ToString()).ToList());
    }
}
