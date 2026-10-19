using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Connapse.Core.Interfaces;
using Connapse.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Connapse.Integration.Tests.CrossSurface;

public static partial class Operations
{
    /// <summary>Search and the file operations, each on a container made for its case.</summary>
    private static readonly Operation[] FileOperations =
    [
        new("search_knowledge", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: false, 1, true,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                string q = k == Case.Invalid ? "" : Query;
                var response = await rest.GetAsync($"/api/containers/{Target(c, k)}/search?q={Uri.EscapeDataString(q)}&mode=Keyword");
                return await WithPayloadAsync(response, body => Found(body.Contains(c.FileName, StringComparison.Ordinal)));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "search_knowledge",
                new() { ["query"] = k == Case.Invalid ? "" : Query, ["containerId"] = Target(c, k), ["mode"] = "Keyword" },
                text => Found(text.Contains(c.FileName, StringComparison.Ordinal))),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<Connapse.Web.Components.Pages.Search>();
                // The scope list loads after the first render; a container that isn't offered can't be searched here.
                page.WaitForAssertion(() => page.FindAll("select.form-select optgroup option").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (!page.FindAll("select.form-select option").Any(o => o.GetAttribute("value") == Target(c, k)))
                    return "error:not_found";
                var select = page.FindAll("select.form-select").First();
                select.Change(Target(c, k));
                page.Find("input[placeholder='Enter your search query...']").Input(k == Case.Invalid ? "" : Query);
                // The button enables once the page has the scope and a query; one that stays disabled refuses the input.
                try
                {
                    page.WaitForAssertion(() => page.Find("button.btn-primary.btn-lg").HasAttribute("disabled").Should().BeFalse(), TimeSpan.FromSeconds(3));
                }
                catch (Exception)
                {
                    return "error:validation";
                }
                await page.Find("button.btn-primary.btn-lg").ClickAsync(new());
                page.WaitForAssertion(() => page.FindAll(".search-results, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(15));
                if (PageError(page) is { } error)
                    return error;
                return "ok " + Found(page.FindAll(".result-card").Any(r => r.TextContent.Contains(c.FileName, StringComparison.Ordinal)));
            },
            Project: (sp, c) => DocumentsAsync(sp, c)),

        new("list_files", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: false, 1, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/containers/{ContainerTarget(c, k)}/files");
                return await WithPayloadAsync(response, body => Listed(body.Contains(c.FileName, StringComparison.Ordinal)));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "list_files", new() { ["containerId"] = ContainerTarget(c, k) },
                text => Listed(text.Contains(c.FileName, StringComparison.Ordinal))),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await FileBrowserAsync(ui, ContainerTarget(c, k));
                if (PageError(page) is { } error)
                    return error;
                return "ok " + Listed(page.FindAll(".entry-name").Any(n => n.TextContent.Trim() == c.FileName));
            },
            Project: (sp, c) => DocumentsAsync(sp, c)),

        new("upload_file", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                using var form = Upload((UploadName(k), Body));
                return await Surfaces.ResultAsync(await rest.PostAsync($"/api/containers/{Target(c, k)}/files", form));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "upload_file",
                new() { ["containerId"] = Target(c, k), ["textContent"] = Body, ["fileName"] = UploadName(k), ["path"] = "/" }),
            Ui: null,
            Project: (sp, c) => DocumentsAsync(sp, c))
        {
            AbsentReasons = new Dictionary<string, string> { ["ui"] = "the page uploads from browser JavaScript straight to the REST route" },
        },

        new("delete_file", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 1, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/containers/{Target(c, k)}/files/{FileTarget(c, k)}"));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "delete_file", new() { ["containerId"] = Target(c, k), ["fileId"] = FileTarget(c, k) }),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await FileBrowserAsync(ui, Target(c, k));
                if (PageError(page) is { } error)
                    return error;
                var row = page.FindAll("tr.file-row").FirstOrDefault(r => r.QuerySelector(".entry-name")?.TextContent.Trim() == (k == Case.Invalid ? null : c.FileName));
                if (row is null)
                    return "error:not_found";
                var delete = row.QuerySelector("button.btn-outline-danger.action-btn");
                if (delete is null)
                    return "error:forbidden";
                await delete.ClickAsync(new());
                await page.Find(".modal-footer-custom button.btn-danger").ClickAsync(new());
                return DialogResult(page);
            },
            Project: (sp, c) => DocumentsAsync(sp, c)),

        new("bulk_upload", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                using var form = Upload(BulkFiles(k).Select(n => (n, Body)).ToArray());
                return await Surfaces.ResultAsync(await rest.PostAsync($"/api/containers/{Target(c, k)}/files", form));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "bulk_upload", new()
            {
                ["containerId"] = Target(c, k),
                ["files"] = JsonSerializer.Serialize(BulkFiles(k).Select(n => new { filename = n, content = Body })),
            }),
            Ui: null,
            Project: (sp, c) => DocumentsAsync(sp, c))
        {
            AbsentReasons = new Dictionary<string, string> { ["ui"] = "the page uploads from browser JavaScript straight to the REST route" },
        },

        new("bulk_delete", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 2, false,
            Rest: null,
            Mcp: (f, c, p, k) => McpAsync(f, p, "bulk_delete", new()
            {
                ["containerId"] = Target(c, k),
                ["fileIds"] = JsonSerializer.Serialize(k == Case.Invalid ? [c.Files[0].Id, c.Unknown] : c.Files.Select(x => x.Id).ToArray()),
            }),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await FileBrowserAsync(ui, Target(c, k));
                if (PageError(page) is { } error)
                    return error;
                // The page can only select what it lists, so the invalid case's unknown file is not selectable.
                var rows = page.FindAll("tr.file-row").Where(r => c.Files.Any(x => x.Name == r.QuerySelector(".entry-name")?.TextContent.Trim())).ToList();
                if (rows.Count == 0)
                    return "error:not_found";
                if (rows[0].QuerySelector("td.col-check") is null)
                    return "error:forbidden";
                foreach (var row in rows.Take(k == Case.Invalid ? 1 : rows.Count))
                    await page.FindAll("tr.file-row").First(r => r.QuerySelector(".entry-name")?.TextContent.Trim() == row.QuerySelector(".entry-name")!.TextContent.Trim())
                        .QuerySelector("td.col-check")!.ClickAsync(new());
                await page.Find(".selection-toolbar button.btn-outline-danger").ClickAsync(new());
                await page.Find(".modal-footer-custom button.btn-danger").ClickAsync(new());
                return DialogResult(page);
            },
            Project: (sp, c) => DocumentsAsync(sp, c))
        {
            AbsentReasons = new Dictionary<string, string> { ["rest"] = "REST has no bulk delete route" },
        },

        new("get_document", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: false, 1, true,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/containers/{Target(c, k)}/files/{FileTarget(c, k)}");
                return await WithPayloadAsync(response, body => Found(body.Contains(c.FileName, StringComparison.Ordinal)));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "get_document", new() { ["containerId"] = Target(c, k), ["fileId"] = FileTarget(c, k) },
                text => Found(text.Contains(c.FileName, StringComparison.Ordinal) || text.Contains(Body, StringComparison.Ordinal))),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = await FileBrowserAsync(ui, Target(c, k));
                if (PageError(page) is { } error)
                    return error;
                var row = page.FindAll("tr.file-row").FirstOrDefault(r => r.QuerySelector(".entry-name")?.TextContent.Trim() == (k == Case.Invalid ? null : c.FileName));
                if (row is null)
                    return "error:not_found";
                await row.ClickAsync(new());
                page.WaitForAssertion(() => page.FindAll(".detail-panel").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                return "ok " + Found(page.FindAll(".detail-panel .detail-value").Any(v => v.TextContent.Trim() == c.FileName));
            },
            Project: (sp, c) => DocumentsAsync(sp, c)),
    ];
}
