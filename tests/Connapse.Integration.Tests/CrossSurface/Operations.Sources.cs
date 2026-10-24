using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

        // A rename: the page's Edit source form, and REST's PATCH with just the new name. The invalid case
        // renames to a blank name; preflight renames a source that doesn't exist.
        new("sources_update", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PatchAsJsonAsync($"/api/sources/{UpdateTarget(c, k)}", new { name = UpdateName(c, k) }));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<SourceEdit>(ps => ps.Add(x => x.Id, Guid.Parse(UpdateTarget(c, k))));
                page.WaitForAssertion(() => page.FindAll("h1, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (page.FindAll(".alert-danger").FirstOrDefault() is { } loadError)
                    return Surfaces.ErrorKind(loadError.TextContent.Trim());
                page.Find("#source-name").Change(UpdateName(c, k));
                await ClickAsync(() => page.Find("button.btn-primary"));
                // A blank name is refused beside the field, before anything is sent.
                page.WaitForAssertion(() => page.FindAll(".alert-success, .alert-danger, #source-name-error").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                return page.FindAll(".alert-danger, #source-name-error").FirstOrDefault() is { } alert ? Surfaces.ErrorKind(alert.TextContent.Trim()) : "ok";
            },
            Project: (sp, c) => SourceStateAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
        },

        // A new source on the case's connection: an empty name is invalid, the case's own source's name a duplicate.
        new("sources_create", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PostAsJsonAsync("/api/sources", new { name = NewSourceName(c, k), connectionId = c.Source!.ConnectionId, scopeJson = "{}" }));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await SourcesPageAsync(ui);
                if (SourcesPageError(page) is { } error)
                    return error;
                if (page.FindAll("button.btn-primary").FirstOrDefault(b => b.TextContent.Contains("New source", StringComparison.Ordinal)) is null)
                    return "error:forbidden";
                await ClickAsync(() => page.FindAll("button.btn-primary").First(b => b.TextContent.Contains("New source", StringComparison.Ordinal)));
                page.Find("#source-connection").Change(c.Source!.ConnectionId.ToString());
                page.Find("#source-name").Change(NewSourceName(c, k));
                await ClickAsync(() => page.FindAll("#new-source-dialog button.btn-primary").First(b => b.TextContent.Contains("Create source", StringComparison.Ordinal)));
                page.WaitForAssertion(() => (page.FindAll("#new-source-dialog").Count == 0
                    || page.FindAll("#new-source-dialog .alert-danger").Count > 0).Should().BeTrue(), TimeSpan.FromSeconds(10));
                return page.FindAll("#new-source-dialog .alert-danger").FirstOrDefault() is { } alert
                    ? Surfaces.ErrorKind(alert.TextContent.Trim())
                    : "ok";
            },
            Project: (sp, c) => SourceStateAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
        },

        // The name typed to confirm, as the page asks: the invalid case types a wrong one; preflight names an unknown source.
        new("sources_delete", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string target = k == Case.Preflight ? c.Unknown : c.Source!.Id.ToString();
                string confirm = k == Case.Invalid ? "not-the-name" : c.Source!.Name;
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/sources/{target}?confirmName={Uri.EscapeDataString(confirm)}"));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await SourcesPageAsync(ui);
                // The page lists only what exists, so an unknown source has no row to delete.
                if (k == Case.Preflight || SourceRow(page, c) is null)
                    return SourcesPageError(page) ?? "error:not_found";
                if (RowButton(page, c, "Delete") is null)
                    return "error:forbidden";
                await ClickAsync(() => RowButton(page, c, "Delete")!);
                page.Find("#delete-source-confirm").Input(k == Case.Invalid ? "not-the-name" : c.Source!.Name);
                if (page.Find("#delete-source-submit").HasAttribute("disabled"))
                    return "error:validation";
                await ClickAsync(() => page.Find("#delete-source-submit"));
                page.WaitForAssertion(() => page.FindAll("#delete-source-dialog").Should().BeEmpty(), TimeSpan.FromSeconds(10));
                return SourcesPageError(page) ?? "ok";
            },
            Project: (sp, c) => SourceStateAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
        },

        // Pausing: PATCH with enabled=false, and the row's "Pause syncing".
        new("sources_pause", [Case.Valid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PatchAsJsonAsync($"/api/sources/{c.Source!.Id}", new { enabled = false }));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await SourcesPageAsync(ui);
                if (SourcesPageError(page) is { } error)
                    return error;
                if (RowButton(page, c, "Pause syncing") is null)
                    return SourceRow(page, c) is null ? "error:not_found" : "error:forbidden";
                await ClickAsync(() => RowButton(page, c, "Pause syncing")!);
                page.WaitForAssertion(() => page.FindAll("div.alert-dismissible").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                return SourcesPageError(page) ?? "ok";
            },
            Project: (sp, c) => SourceStateAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
        },

        // One sync cycle now: REST's POST .../sync and the row's Sync button. Preflight names a paused source, which neither syncs.
        new("sources_sync", [Case.Valid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PostAsync($"/api/sources/{c.Source!.Id}/sync", null));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await SourcesPageAsync(ui);
                if (SourcesPageError(page) is { } error)
                    return error;
                if (RowButton(page, c, "Sync") is null)
                    return SourceRow(page, c) is null ? "error:not_found" : "error:forbidden";
                await ClickAsync(() => RowButton(page, c, "Sync")!);
                page.WaitForAssertion(() => page.FindAll("div.alert-dismissible").Should().NotBeEmpty(), TimeSpan.FromSeconds(30));
                return SourcesPageError(page) ?? "ok";
            },
            Project: (sp, c) => SourceStateWithDocumentsAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
            Prepare = async (f, c, k) =>
            {
                if (k == Case.Preflight)
                    (await f.AdminClient.PatchAsJsonAsync($"/api/sources/{c.Source!.Id}", new { enabled = false })).EnsureSuccessStatusCode();
            },
        },

        // Retrying the documents whose ingestion failed: REST's POST .../retry-failed and the row's Retry failed.
        new("sources_retry", [Case.Valid, Case.UnderScoped], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PostAsync($"/api/sources/{c.Source!.Id}/retry-failed", null));
            },
            Mcp: null,
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await SourcesPageAsync(ui);
                if (SourcesPageError(page) is { } error)
                    return error;
                if (RowButton(page, c, "Retry failed") is null)
                    return SourceRow(page, c) is null ? "error:not_found" : "error:forbidden";
                await ClickAsync(() => RowButton(page, c, "Retry failed")!);
                page.WaitForAssertion(() => page.FindAll("div.alert-dismissible").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                return SourcesPageError(page) ?? "ok";
            },
            Project: (sp, c) => SourceStateWithDocumentsAsync(sp, c))
        {
            AbsentReasons = NoSourceTools,
            ArrangesSource = true,
            Prepare = (f, c, k) => SeedFailedDocumentAsync(f, c.Source!),
        },
    ];

    /// <summary>Out of attempts, as a document that kept failing is: a retry must give it a fresh budget.</summary>
    private const int ExhaustedAttempts = 4;

    /// <summary>
    /// Syncs the source, waits for its document to settle, then marks it failed and out of attempts:
    /// a real failure needs a broken parser or model, and this is the state one leaves.
    /// </summary>
    private static async Task SeedFailedDocumentAsync(SharedWebAppFixture fixture, SourceCase source)
    {
        (await fixture.AdminClient.PostAsync($"/api/sources/{source.Id}/sync", null)).EnsureSuccessStatusCode();
        await using var conn = await OpenAsync(fixture.Factory.Services);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            await using var read = new Npgsql.NpgsqlCommand("SELECT ingestion_status FROM documents WHERE source_id = @id", conn);
            read.Parameters.AddWithValue("id", source.Id);
            string? status = (string?)await read.ExecuteScalarAsync();
            if (status is "Ready" or "FailedRetryable" or "FailedPermanent")
                break;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The source's document never settled (status {status ?? "missing"})");
            await Task.Delay(250);
        }
        await using var fail = new Npgsql.NpgsqlCommand(
            $"UPDATE documents SET ingestion_status = 'FailedRetryable', attempt_count = {ExhaustedAttempts} WHERE source_id = @id", conn);
        fail.Parameters.AddWithValue("id", source.Id);
        await fail.ExecuteNonQueryAsync();
    }

    private static async Task<Npgsql.NpgsqlConnection> OpenAsync(IServiceProvider services)
    {
        string connection = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>().GetConnectionString("DefaultConnection")!;
        var conn = new Npgsql.NpgsqlConnection(connection);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// The source's state, plus its documents: how many it owns, and whether a failed one was given a
    /// fresh attempt budget. Read as yes or no, since a worker may claim the retried job at once.
    /// </summary>
    private static async Task<StateProjection> SourceStateWithDocumentsAsync(IServiceProvider services, CaseContext c)
    {
        var projection = await SourceStateAsync(services, c);
        await using var conn = await OpenAsync(services);
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT id, attempt_count FROM documents WHERE source_id = @id", conn);
        cmd.Parameters.AddWithValue("id", c.Source!.Id);
        var documents = new List<(string Id, int Attempts)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                documents.Add((reader.GetGuid(0).ToString(), reader.GetInt32(1)));
        // The documents' ids are resources too, so a job naming one compares by token, not by id.
        return new StateProjection(
            [.. projection.State, $"documents owned={documents.Count} out of attempts={documents.Count(d => d.Attempts >= ExhaustedAttempts)}"],
            [.. projection.ResourceIds, .. documents.Select(d => d.Id)]);
    }

    /// <summary>A create's name: empty for the invalid case, the existing source's for the duplicate preflight, a new one otherwise.</summary>
    private static string NewSourceName(CaseContext c, string @case) => @case switch
    {
        Case.Invalid => "",
        Case.Preflight => c.Source!.Name,
        _ => $"{c.Source!.Name}-nw",
    };

    /// <summary>The Sources page once its list (or its refusal) has rendered.</summary>
    private static Task<IRenderedComponent<Sources>> SourcesPageAsync(UiSurface ui)
    {
        var page = ui.Context.Render<Sources>();
        page.WaitForAssertion(() => page.FindAll("table.table, .card-body.text-center, div.alert-dismissible.alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return Task.FromResult(page);
    }

    private static AngleSharp.Dom.IElement? SourceRow(IRenderedComponent<Sources> page, CaseContext c) =>
        page.FindAll("tr").FirstOrDefault(r => r.QuerySelector(".fw-medium")?.TextContent.Trim() == c.Source!.Name);

    private static AngleSharp.Dom.IElement? RowButton(IRenderedComponent<Sources> page, CaseContext c, string label) =>
        SourceRow(page, c)?.QuerySelectorAll("button").FirstOrDefault(b => b.TextContent.Contains(label, StringComparison.Ordinal));

    /// <summary>The name a rename gives the case's source; it keeps the case's prefix, so the projection still finds it.</summary>
    private static string Renamed(CaseContext c) => $"{c.Source!.Name}-up";

    /// <summary>The source a case targets: an unknown one for the invalid case, the case's own otherwise.</summary>
    private static string SourceTarget(CaseContext c, string @case) => @case == Case.Invalid ? c.Unknown : c.Source!.Id.ToString();

    /// <summary>The source a rename targets: an unknown one for preflight, the case's own otherwise.</summary>
    private static string UpdateTarget(CaseContext c, string @case) => @case == Case.Preflight ? c.Unknown : c.Source!.Id.ToString();

    /// <summary>The name a rename sends: blank for the invalid case, which every surface must refuse (#862).</summary>
    private static string UpdateName(CaseContext c, string @case) => @case == Case.Invalid ? "   " : Renamed(c);

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
                await DeleteEventuallyAsync(sources, s.Id);
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

    /// <summary>
    /// Deletes a source, retrying a Postgres deadlock: a retried document can still be re-ingesting.
    /// Best effort, as the snapshot tests' cleanup is: a source still locked is left for the next run.
    /// </summary>
    private static async Task DeleteEventuallyAsync(ISourceStore sources, Guid id)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await sources.DeleteAsync(id);
                return;
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                // A deadlock victim: the other transaction has finished or will, so try again.
            }
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
