using System.Security.Claims;
using Connapse.Application.Containers;
using Connapse.Application.Files;
using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;
using IDocumentAccess = Connapse.Application.Ports.IDocumentAccess;

namespace Connapse.Application.Tables;

/// <summary>
/// Tells a reader of search passages about the tables in them (CON-312). Each passage that holds a table the caller may
/// read has that table's text replaced by a short note naming it, and lists it in <see cref="SearchHit.Tables"/>, so an
/// agent can query its rows instead of reading them as prose. A table the caller may not read, including one an admin
/// hid, leaves the passage exactly as stored and isn't named. When the links or the access can't be read, every hit is
/// left as it came.
/// </summary>
public sealed class PassageTables(ITablePassageLinks links, ITableStore tables, IDocumentStore documents, IDocumentAccess access)
{
    public async Task<SearchResult> ApplyAsync(ClaimsPrincipal user, SearchResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var chunkIds = result.Hits.Select(h => Guid.TryParse(h.ChunkId, out Guid id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToList();
        if (chunkIds.Count == 0)
            return result;

        try
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<TablePassageLink>> found = await links.ForChunksAsync(chunkIds, ct);
            if (found.Count == 0)
                return result;
            Dictionary<Guid, TableInfo> readable = await ReadableAsync(user, result.Hits, found, ct);
            return result with { Hits = result.Hits.Select(h => Render(h, found, readable)).ToList() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result;
        }
    }

    /// <summary>The note that stands in a passage for a table's text.</summary>
    public static string Note(TableInfo table)
    {
        string where = table.Placement?.Page is int page ? $", page {page}" : "";
        string columns = string.Join(", ", table.VisibleColumns.Select(c => c.Name));
        return $"[Table \"{table.Name}\" (id {table.Id}{where}), columns: {columns}. Its rows are in a table; query them with table_read or table_aggregate.]";
    }

    public static PassageTable Describe(TableInfo table, bool hidden = false) =>
        new(table.Id, table.ContainerId, table.Name, table.Placement?.Page, table.Placement?.Number, hidden);

    /// <summary>The linked tables the caller may read, checked per document, by id.</summary>
    private async Task<Dictionary<Guid, TableInfo>> ReadableAsync(
        ClaimsPrincipal user, IReadOnlyList<SearchHit> hits,
        IReadOnlyDictionary<Guid, IReadOnlyList<TablePassageLink>> found, CancellationToken ct)
    {
        var readable = new Dictionary<Guid, TableInfo>();
        var byDocument = hits
            .Where(h => Guid.TryParse(h.ChunkId, out Guid c) && found.ContainsKey(c) && Guid.TryParse(h.DocumentId, out _))
            .GroupBy(h => Guid.Parse(h.DocumentId));
        foreach (var group in byDocument)
        {
            if (await documents.GetAsync(group.Key.ToString(), ct) is not { } document
                || !Guid.TryParse(document.ContainerId, out Guid containerId))
                continue;
            HashSet<Guid> linked = group.SelectMany(h => found[Guid.Parse(h.ChunkId)]).Select(l => l.TableId).ToHashSet();
            var mine = (await tables.ListByDocumentAsync(containerId, group.Key, ct)).Where(t => linked.Contains(t.Id)).ToList();
            TableAccess check = await TableRules.AccessAsync(documents, access, user, containerId, mine, ct);
            if (!check.Checked)
                throw new InvalidOperationException(TableAccess.UncheckedMessage);
            foreach (TableInfo table in mine.Where(check.CanRead))
                readable[table.Id] = table;
        }
        return readable;
    }

    private static SearchHit Render(
        SearchHit hit, IReadOnlyDictionary<Guid, IReadOnlyList<TablePassageLink>> found, Dictionary<Guid, TableInfo> readable)
    {
        if (!Guid.TryParse(hit.ChunkId, out Guid chunkId) || !found.TryGetValue(chunkId, out var mine))
            return hit;
        var shown = mine.Where(l => readable.ContainsKey(l.TableId)).OrderBy(l => l.Start).ToList();
        if (shown.Count == 0)
            return hit;

        return hit with
        {
            Content = Replace(hit.Content, shown, readable),
            Tables = shown.Select(l => l.TableId).Distinct().Select(id => Describe(readable[id])).ToList(),
        };
    }

    /// <summary>
    /// The passage with each link's span replaced by its table's note. A span that doesn't fit the text as it came
    /// (a hit shown as a sentence window rather than its stored passage) is left as it is.
    /// </summary>
    internal static string Replace(string content, IReadOnlyList<TablePassageLink> ordered, IReadOnlyDictionary<Guid, TableInfo> tables)
    {
        var text = new System.Text.StringBuilder();
        int at = 0;
        foreach (TablePassageLink link in ordered)
        {
            if (link.Start < at || link.Length <= 0 || link.Start + link.Length > content.Length || !IsTableText(content.AsSpan(link.Start, link.Length)))
                continue;
            text.Append(content, at, link.Start - at).Append(Note(tables[link.TableId]));
            at = link.Start + link.Length;
        }
        return text.Append(content, at, content.Length - at).ToString();
    }

    /// <summary>Whether every non-blank line of a span is a pipe-table row, as a link's span always is in its passage.</summary>
    private static bool IsTableText(ReadOnlySpan<char> span)
    {
        foreach (ReadOnlySpan<char> line in span.EnumerateLines())
        {
            if (!line.IsWhiteSpace() && !line.Contains('|'))
                return false;
        }
        return true;
    }
}

/// <summary>
/// The tables found in one document (CON-312), by id or path, when the caller may read the document: each table the
/// caller may read, with its page and number, so it can be queried with the table tools. An admin is also shown the
/// tables hidden from everyone else, marked, to manage them. When access to the tables can't be checked, none are given.
/// Open to viewers and reading agent keys.
/// </summary>
[Operation("files.tables", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetFileTables(
    IContainerStore containers, IDocumentStore documents, IDocumentAccess access, ITableStore tables, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<IReadOnlyList<PassageTable>, FileStatus>> RunAsync(
        OperationContext context, Guid containerId, string fileRef, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Reader))
            return FileRules.Fail<IReadOnlyList<PassageTable>>(FileStatus.Forbidden, "You don't have permission to view files.");
        if (await containers.GetAsync(containerId, ct) is null)
            return FileRules.ContainerNotFound<IReadOnlyList<PassageTable>>(containerId);
        if (string.IsNullOrWhiteSpace(fileRef)
            || await FileRules.FindReadableAsync(documents, access, context, containerId, fileRef, ct) is not { } document)
            return FileRules.FileNotFound<IReadOnlyList<PassageTable>>(containerId, fileRef ?? "");

        IReadOnlyList<TableInfo> all = await tables.ListByDocumentAsync(containerId, Guid.Parse(document.Id), ct);
        TableAccess readable = await TableRules.AccessAsync(documents, access, context.User, containerId, all, ct);
        if (!readable.Checked)
            return FileRules.Fail<IReadOnlyList<PassageTable>>(FileStatus.LinksUnchecked, TableAccess.UncheckedMessage);

        bool admin = await authorizer.AllowsAsync(context.User, Policies.Admin);
        IReadOnlyList<PassageTable> shown = all
            .Where(t => readable.CanRead(t) || admin && t.Hidden && readable.CanRead(t with { Hidden = false }))
            .OrderBy(t => t.Placement?.Number ?? 0).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => PassageTables.Describe(t, t.Hidden))
            .ToList();
        return Attempt<IReadOnlyList<PassageTable>, FileStatus>.Succeed(shown);
    }
}

/// <summary>
/// Hides a whole table from every caller, or shows it again (CON-312): a hidden table isn't listed, described, read,
/// queried or named in a passage, whose text then stays as stored. The hide is kept across re-ingests of the table's
/// document. Admins only, who still see the table, marked, to show it again.
/// </summary>
[Operation("tables.hide", Scope = "admin:settings", Risk = RiskTier.Write, Mcp = McpExposure.Generic)]
public sealed class HideTable(IContainerStore containers, ITableStore tables, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<TableInfo, TableStatus>> RunAsync(
        OperationContext context, Guid containerId, Guid tableId, bool hidden, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Admin))
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.Forbidden, "Only admins can hide tables.");
        if (await containers.GetAsync(containerId, ct) is not { } container)
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.ContainerNotFound, $"Container {containerId} not found");
        if (container.Kind == ContainerKind.Memory)
            return Attempt<TableInfo, TableStatus>.Fail(TableStatus.MemoryContainer, ContainerRules.MemoryOnly(container));

        return await tables.SetHiddenAsync(containerId, tableId, hidden, ct) is { } table
            ? Attempt<TableInfo, TableStatus>.Succeed(table)
            : Attempt<TableInfo, TableStatus>.Fail(TableStatus.TableNotFound, $"Table {tableId} not found");
    }
}
