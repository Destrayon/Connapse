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

/// <summary>The four ways #792 runs every operation.</summary>
public static class Case
{
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string UnderScoped = "under-scoped";
    public const string Preflight = "preflight";
}

/// <summary>
/// What one case is acting on: a container made for it alone, and the files it holds. Every case
/// gets its own, so surfaces never share state and a case can't see another's side effects.
/// </summary>
public sealed record CaseContext(string ContainerId, string ContainerName, IReadOnlyList<(string Id, string Name)> Files)
{
    /// <summary>The case's own source, named after its container, for the Sources operations.</summary>
    public SourceCase? Source { get; init; }

    /// <summary>The case's own user, named after its container, for the user operations.</summary>
    public UserCase? User { get; init; }

    /// <summary>The case's own pending invite, to the address named after its container, for the invite operations.</summary>
    public Guid? InviteId { get; init; }

    /// <summary>The case's own agent and its key, named after its container, for the agent operations.</summary>
    public AgentCase? Agent { get; init; }

    public string FileId => Files[0].Id;
    public string FileName => Files[0].Name;

    /// <summary>An id no container or file has: the target of a not-found case.</summary>
    public string Unknown { get; } = Guid.NewGuid().ToString();
}

/// <summary>A surface running one case: the result in the surfaces' shared words, or null where the surface has no such action.</summary>
public delegate Task<string?> Driver(SharedWebAppFixture fixture, CaseContext context, Principal principal, string @case);

/// <summary>
/// One operation the surfaces share, with the cases that apply to it, a driver per surface, and
/// what it changed. A null driver is a surface without the action; <see cref="AbsentReasons"/>
/// says why, and the comparison records it as an outcome like any other.
/// </summary>
public sealed record Operation(
    string Name,
    IReadOnlyList<string> Cases,
    bool Writes,
    int FilesToArrange,
    bool WaitForIngestion,  // the valid and under-scoped cases need the files ready (searched or read in full)
    Driver? Rest,
    Driver? Mcp,
    Driver? Ui,
    Func<IServiceProvider, CaseContext, Task<StateProjection>> Project)
{
    public IReadOnlyDictionary<string, string> AbsentReasons { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether the case needs its own source (on its own filesystem connection) as well as its container.</summary>
    public bool ArrangesSource { get; init; }

    /// <summary>Whether the case needs a user of its own to act on.</summary>
    public bool ArrangesUser { get; init; }

    /// <summary>Whether the case needs a pending invite of its own to act on.</summary>
    public bool ArrangesInvite { get; init; }

    /// <summary>Whether the case needs an agent of its own, with a key, to act on.</summary>
    public bool ArrangesAgent { get; init; }

    /// <summary>Anything the case's source needs before the action, such as a failed document to retry.</summary>
    public Func<SharedWebAppFixture, CaseContext, string, Task>? Prepare { get; init; }

    /// <summary>Who runs a case: a Viewer for an under-scoped write, a user with no role for an under-scoped read, the admin otherwise.</summary>
    public Principal PrincipalFor(string @case) =>
        @case != Case.UnderScoped ? Principal.Admin : Writes ? Principal.Viewer : Principal.NoRole;

    public Driver? DriverFor(string surface) => surface switch
    {
        "rest" => Rest,
        "mcp" => Mcp,
        "ui" => Ui,
        _ => throw new ArgumentOutOfRangeException(nameof(surface)),
    };

    public override string ToString() => Name;
}

/// <summary>
/// The 12 operations MCP exposes, which are the ones offered on more than one surface. REST-only
/// capabilities belong to #791's manifest, not here.
/// </summary>
public static partial class Operations
{
    private const string Body = "the cross-surface harness reads this sentence";
    private const string Query = "harness";

    /// <summary>Every operation: the container ones here, and the file and search ones in Operations.Files.cs.</summary>
    public static IReadOnlyList<Operation> All => [.. ContainerOperations, .. FileOperations, .. SourceOperations, .. UserOperations, .. InviteOperations, .. AgentOperations];

    private static readonly Operation[] ContainerOperations =
    [
        new("container_create", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.PostAsJsonAsync("/api/containers", new { name = NewName(c, k), description = "made here" }));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "container_create", new() { ["name"] = NewName(c, k), ["description"] = "made here" }),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<Home>();
                page.WaitForAssertion(() => page.FindAll(".container-grid, .empty-state, .alert").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                IElement? Open() => page.FindAll("button.btn-primary").FirstOrDefault(b => b.TextContent.Contains("Container", StringComparison.Ordinal));
                if (Open() is null)
                    return "error:forbidden";
                await ClickAsync(page, () => Open()!);
                page.Find("input[placeholder='my-project']").Input(NewName(c, k));
                page.Find("input[placeholder='What is this container for?']").Change("made here");
                await ClickAsync(page, () => page.Find(".modal-footer-custom button.btn-primary"));
                return DialogResult(page);
            },
            Project: (sp, c) => ContainerByNameAsync(sp, NewName(c, Case.Valid), NewName(c, Case.Invalid), NewName(c, Case.UnderScoped))),

        new("container_list", [Case.Valid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync("/api/containers?skip=0&take=50");
                return await WithPayloadAsync(response, body => Listed(body.Contains(c.ContainerName, StringComparison.Ordinal)));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "container_list", [], text => Listed(text.Contains(c.ContainerName, StringComparison.Ordinal))),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<Home>();
                page.WaitForAssertion(() => page.FindAll(".container-grid, .empty-state, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (PageError(page) is { } error)
                    return error;
                return "ok " + Listed(page.FindAll(".container-card-name").Any(n => n.TextContent.Trim() == c.ContainerName));
            },
            Project: (sp, c) => ContainerByNameAsync(sp, c.ContainerName)),

        new("container_delete", [Case.Valid, Case.Invalid, Case.UnderScoped, Case.Preflight], Writes: true, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                return await Surfaces.ResultAsync(await rest.DeleteAsync($"/api/containers/{ContainerTarget(c, k)}"));
            },
            Mcp: async (f, c, p, k) =>
            {
                return await McpAsync(f, p, "container_delete", new() { ["containerId"] = ContainerTarget(c, k) });
            },
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<Home>();
                page.WaitForAssertion(() => page.FindAll(".container-grid, .empty-state, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                IElement? Card() => page.FindAll(".container-card").FirstOrDefault(card => card.QuerySelector(".container-card-name")?.TextContent.Trim() == (k == Case.Invalid ? null : c.ContainerName));
                if (Card() is null)
                    return "error:not_found";
                if (Card()!.QuerySelector(".container-delete-btn") is null)
                    return "error:forbidden";
                await ClickAsync(page, () => Card()!.QuerySelector(".container-delete-btn")!);
                await ClickAsync(page, () => page.Find(".modal-footer-custom button.btn-danger"));
                return DialogResult(page);
            },
            Project: (sp, c) => ContainerByNameAsync(sp, c.ContainerName)),

        new("container_describe", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: false, 0, false,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/containers/{ContainerTarget(c, k)}");
                return await WithPayloadAsync(response, body => Found(body.Contains(c.ContainerName, StringComparison.Ordinal)));
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "container_describe", new() { ["containerId"] = ContainerTarget(c, k) },
                text => Found(text.Contains(c.ContainerName, StringComparison.Ordinal))),
            Ui: async (f, c, p, k) =>
            {
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, ContainerTarget(c, k)));
                page.WaitForAssertion(() => page.FindAll(".file-table, .empty-folder, .alert-danger, .breadcrumb-link + .breadcrumb-sep").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (PageError(page) is { } error)
                    return error;
                return "ok " + Found(page.FindAll("button.breadcrumb-link").Any(b => b.TextContent.Trim() == c.ContainerName));
            },
            Project: (sp, c) => ContainerByNameAsync(sp, c.ContainerName)),

        // The files are ready first, so every surface reads the same breakdown rather than a moment of ingestion.
        new("container_stats", [Case.Valid, Case.Invalid, Case.UnderScoped], Writes: false, 1, true,
            Rest: async (f, c, p, k) =>
            {
                using var rest = await Surfaces.RestAsync(f, p);
                var response = await rest.GetAsync($"/api/containers/{ContainerTarget(c, k)}/stats");
                return await WithPayloadAsync(response, body =>
                {
                    var root = JsonDocument.Parse(body).RootElement;
                    var docs = root.GetProperty("documents");
                    return $"documents={docs.GetProperty("total")} ready={docs.GetProperty("ready")} processing={docs.GetProperty("processing")}"
                        + $" failed={docs.GetProperty("failed")} chunks={root.GetProperty("totalChunks")}";
                });
            },
            Mcp: (f, c, p, k) => McpAsync(f, p, "container_stats", new() { ["containerId"] = ContainerTarget(c, k) },
                text => McpStats(text)),
            Ui: async (f, c, p, k) =>
            {
                // The file browser's header shows the container's stats, from the same operation as REST and MCP.
                using var ui = await Surfaces.UiAsync(f, p);
                var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, ContainerTarget(c, k)));
                page.WaitForAssertion(() => page.FindAll(".container-stats, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
                if (PageError(page) is { } error)
                    return error;
                string Stat(string name) => Number(page.Find($".container-stats [data-stat='{name}']").TextContent, @"([\d,]+)").Replace(",", "");
                return $"ok documents={Stat("documents")} ready={Stat("ready")} processing={Stat("processing")} failed={Stat("failed")} chunks={Stat("chunks")}";
            },
            Project: (sp, c) => DocumentsAsync(sp, c)),
    ];

    /// <summary>Makes a case's own container, with its files, as the admin over REST.</summary>
    public static async Task<CaseContext> ArrangeAsync(SharedWebAppFixture fixture, Operation operation, string @case)
    {
        // Lists page by name, so a case's container must sort first: "0cs-" puts it before every other
        // test container, and a countdown puts the newest case before earlier ones.
        long countdown = 9_999_999_999_999 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string name = $"0cs-{countdown:D13}-{Guid.NewGuid():N}"[..28];
        var created = await fixture.AdminClient.PostAsJsonAsync("/api/containers", new { name, description = "cross-surface case" });
        created.EnsureSuccessStatusCode();
        string id = (await created.Content.ReadFromJsonAsync<IdBody>())!.Id;

        var files = new List<(string, string)>();
        for (int i = 0; i < operation.FilesToArrange; i++)
            files.Add(await UploadAsync(fixture, id, $"doc{i + 1}.txt"));
        // Set up before the action's window, so its upload isn't counted as the delete's doing.
        if (operation.Name == "container_delete" && @case == Case.Preflight)
            files.Add(await UploadAsync(fixture, id, "keep.txt"));
        var context = new CaseContext(id, name, files);
        if (operation.ArrangesSource)
            context = context with { Source = await ArrangeSourceAsync(fixture, name) };
        if (operation.ArrangesUser)
            context = context with { User = await ArrangeUserAsync(fixture, name) };
        if (operation.ArrangesInvite)
            context = context with { InviteId = await ArrangeInviteAsync(fixture, context) };
        if (operation.ArrangesAgent)
            context = context with { Agent = await ArrangeAgentAsync(fixture, name) };
        if (operation.Prepare is { } prepare)
            await prepare(fixture, context, @case);
        if (operation.WaitForIngestion && @case is Case.Valid or Case.UnderScoped)
            foreach (var (fileId, _) in files)
                await WaitForReadyAsync(fixture, id, fileId);
        return context;
    }

    // ── Case inputs ─────────────────────────────────────────────────────────

    /// <summary>The container a case targets: an unknown one for the preflight case (the container is missing), the case's own otherwise.</summary>
    private static string Target(CaseContext c, string @case) => @case == Case.Preflight ? c.Unknown : c.ContainerId;

    /// <summary>For operations whose only input is the container: the invalid case names one that doesn't exist.</summary>
    private static string ContainerTarget(CaseContext c, string @case) => @case == Case.Invalid ? c.Unknown : c.ContainerId;

    private static string FileTarget(CaseContext c, string @case) => @case == Case.Invalid ? c.Unknown : c.FileId;

    /// <summary>A name for a create: the valid one, an illegal one, or the existing container's for the duplicate preflight.</summary>
    private static string NewName(CaseContext c, string @case) => @case switch
    {
        Case.Invalid => "Not A Valid Name!",
        Case.Preflight => c.ContainerName,
        _ => $"{c.ContainerName}-{@case[..2]}",
    };

    private static string UploadName(string @case) => @case == Case.Invalid ? "tool.exe" : "note.txt";

    private static string[] BulkFiles(string @case) => @case == Case.Invalid ? ["ok.txt", "tool.exe"] : ["one.txt", "two.txt"];

    /// <summary>
    /// Removes the containers a case left, with their files, so tests that list containers by name
    /// still find their own on the first page: a case's "0cs-" container sorts before all of them.
    /// </summary>
    public static async Task CleanupAsync(SharedWebAppFixture fixture, CaseContext c)
    {
        if (c.Source is { } source)
            await CleanupSourceAsync(fixture, source);
        if (c.User is { } user)
            await CleanupUserAsync(fixture, user);
        await CleanupInvitesAsync(fixture, c);
        await CleanupAgentsAsync(fixture, c);
        IReadOnlyList<Connapse.Core.Document> documents;
        foreach (string name in new[] { c.ContainerName, NewName(c, Case.Valid), NewName(c, Case.UnderScoped) })
        {
            Connapse.Core.Container? container;
            using (var scope = fixture.Factory.Services.CreateScope())
            {
                container = await scope.ServiceProvider.GetRequiredService<IContainerStore>().GetByNameAsync(name);
                if (container is null)
                    continue;
                documents = await scope.ServiceProvider.GetRequiredService<IDocumentStore>().ListAsync(Guid.Parse(container.Id));
            }
            foreach (var document in documents)
                await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}/files/{document.Id}");
            await fixture.AdminClient.DeleteAsync($"/api/containers/{container.Id}");
        }
    }

    // ── Surface helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds an element and clicks it in one step on the renderer's thread, so no render can come
    /// between the two. A page that re-renders by itself (the file browser refreshes ingestion
    /// status) otherwise drops the handler of an element found a moment before, which bUnit reports
    /// as an unknown event handler; retrying lost that race too while files were still ingesting (#900).
    /// </summary>
    internal static Task ClickAsync<T>(IRenderedComponent<T> page, Func<IElement> find)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.InvokeAsync(() => find().ClickAsync(new()));

    private static async Task<string?> McpAsync(SharedWebAppFixture fixture, Principal principal, string tool,
        Dictionary<string, object?> arguments, Func<string, string>? payload = null)
    {
        try
        {
            await using var mcp = await Surfaces.McpAsync(fixture, principal);
            var result = await mcp.CallToolAsync(tool, arguments);
            string kind = Surfaces.Result(result);
            if (payload is null || !kind.StartsWith("ok", StringComparison.Ordinal))
                return kind;
            string text = string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
            return $"{kind} {payload(text)}";
        }
        catch (Exception ex) when (Surfaces.McpRefusal(ex) is { } refused)
        {
            return refused;
        }
    }

    /// <summary>A read's result: the kind, and on success what the body says about the case's resource.</summary>
    private static async Task<string> WithPayloadAsync(HttpResponseMessage response, Func<string, string> payload)
    {
        string kind = await Surfaces.ResultAsync(response);
        if (!kind.StartsWith("ok", StringComparison.Ordinal))
            return kind;
        return $"ok {payload(await response.Content.ReadAsStringAsync())}";
    }

    private static string Listed(bool listed) => listed ? "listed" : "not-listed";

    private static string Found(bool found) => found ? "found" : "not-found";

    /// <summary>
    /// container_stats' text as the same fields REST returns. MCP prints the status breakdown only
    /// when some document isn't ready, so "Documents: 1" alone means one ready, none in flight.
    /// </summary>
    private static string McpStats(string text)
    {
        string documents = Number(text, @"^Documents: (\d+)");
        bool breakdown = System.Text.RegularExpressions.Regex.IsMatch(text, @"^Documents: \d+ \(", System.Text.RegularExpressions.RegexOptions.Multiline);
        string ready = breakdown ? Number(text, @"(\d+) ready") : documents;
        string processing = breakdown ? Number(text, @"(\d+) processing") : "0";
        string failed = breakdown ? Number(text, @"(\d+) failed") : "0";
        return $"documents={documents} ready={ready} processing={processing} failed={failed} chunks={Number(text, @"^Chunks: ([\d,]+)").Replace(",", "")}";
    }

    /// <summary>The number a pattern's first group captures, or "none".</summary>
    private static string Number(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m
            ? m.Groups[1].Value
            : "none";

    /// <summary>A dialog's result: closed is ok, an alert inside the still-open dialog is its failure.</summary>
    private static string DialogResult<T>(IRenderedComponent<T> page) where T : Microsoft.AspNetCore.Components.IComponent
    {
        page.WaitForAssertion(() => (page.FindAll(".modal-content-custom").Count == 0
            || page.FindAll(".modal-content-custom .alert-danger").Count > 0).Should().BeTrue(), TimeSpan.FromSeconds(10));
        return page.FindAll(".modal-content-custom").Count == 0
            ? "ok"
            : Surfaces.ErrorKind(page.Find(".modal-content-custom .alert-danger").TextContent.Trim());
    }

    /// <summary>A page-level failure (an alert outside any dialog), as an error kind, or null.</summary>
    private static string? PageError<T>(IRenderedComponent<T> page) where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".alert-danger").FirstOrDefault(a => a.Closest(".modal-content-custom") is null) is { } alert
            ? Surfaces.ErrorKind(alert.TextContent.Trim())
            : null;

    private static Task<IRenderedComponent<FileBrowser>> FileBrowserAsync(UiSurface ui, string containerId)
    {
        var page = ui.Context.Render<FileBrowser>(ps => ps.Add(x => x.ContainerId, containerId));
        page.WaitForAssertion(() => page.FindAll(".file-table, .empty-folder, .alert-danger").Should().NotBeEmpty(), TimeSpan.FromSeconds(10));
        return Task.FromResult(page);
    }

    private static MultipartFormDataContent Upload(params (string Name, string Text)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, text) in files)
        {
            // As a browser sends it: a text file arrives as text/plain.
            var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            form.Add(part, "files", name);
        }
        form.Add(new StringContent("/"), "path");
        return form;
    }

    private static async Task<(string Id, string Name)> UploadAsync(SharedWebAppFixture fixture, string containerId, string name)
    {
        using var form = Upload((name, Body));
        var response = await fixture.AdminClient.PostAsync($"/api/containers/{containerId}/files", form);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string id = json.RootElement.EnumerateObject().Select(p => p.Value).SelectMany(Ids).First();
        return (id, name);

        static IEnumerable<string> Ids(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Array => e.EnumerateArray().SelectMany(Ids),
            JsonValueKind.Object => e.EnumerateObject().SelectMany(p =>
                p.Name.Equals("documentId", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String ? [p.Value.GetString()!] : Ids(p.Value)),
            _ => [],
        };
    }

    private static async Task WaitForReadyAsync(SharedWebAppFixture fixture, string containerId, string fileId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            string body = await fixture.AdminClient.GetStringAsync($"/api/containers/{containerId}/files/{fileId}");
            if (body.Contains("\"Ready\"", StringComparison.Ordinal))
                return;
            if (body.Contains("\"Failed", StringComparison.Ordinal))
                throw new InvalidOperationException($"Ingestion failed for {fileId}: {body}");
            await Task.Delay(250);
        }
        throw new TimeoutException($"{fileId} was not Ready within 90 s");
    }

    // ── Projections ─────────────────────────────────────────────────────────

    private static async Task<StateProjection> ContainerByNameAsync(IServiceProvider services, params string[] names)
    {
        var store = services.GetRequiredService<IContainerStore>();
        var lines = new List<string>();
        var ids = new List<string>();
        foreach (string name in names.Distinct())
        {
            var container = await store.GetByNameAsync(name);
            if (container is null)
                continue;
            lines.Add($"container {name} description={container.Description}");
            ids.Add(container.Id);
        }
        return new StateProjection(lines.Count == 0 ? ["no container"] : lines, ids);
    }

    /// <summary>
    /// The container's documents, each with a hash of the bytes stored for it, so a surface that
    /// saves different content under the same name doesn't compare equal.
    /// </summary>
    private static async Task<StateProjection> DocumentsAsync(IServiceProvider services, CaseContext c)
    {
        var docs = await services.GetRequiredService<IDocumentStore>().ListAsync(Guid.Parse(c.ContainerId));
        var storage = services.GetRequiredService<IManagedStorageProvider>().CreateConnector(c.ContainerId);
        var lines = new List<string>();
        foreach (var d in docs)
        {
            await using var stream = await storage.ReadFileAsync(d.Path);
            byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
            lines.Add($"document {d.FileName} content={Convert.ToHexString(hash)[..12]}");
        }
        return new StateProjection(lines.OrderBy(x => x, StringComparer.Ordinal).ToList(), docs.Select(d => d.Id).ToList());
    }

    private sealed record IdBody(string Id);
}
