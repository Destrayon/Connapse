using Connapse.Application.Ports;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Sources;

/// <summary>Why reading sources didn't succeed.</summary>
public enum SourceReadStatus
{
    Success,

    [Failure(403, "forbidden", "Not allowed")]
    Forbidden,

    [Failure(404, "source-not-found", "Source not found")]
    NotFound,

    [Failure(400, "invalid-page", "Invalid page")]
    InvalidPage,
}

/// <summary>
/// One page of sources. <see cref="Take"/> null means every source, which only the Sources page asks
/// for; REST always names a page.
/// </summary>
public sealed record ListSourcesQuery(int Skip = 0, int? Take = null);

/// <summary>
/// Sources the caller may see, and whether they may see each one's diagnostics: a provider's failure
/// text tends to quote the resource that failed, so only administrators get it.
/// </summary>
public sealed record SourceList(IReadOnlyList<Source> Items, bool HasMore, bool Diagnostics);

/// <summary>One source, and whether the caller may see its diagnostics.</summary>
public sealed record SourceView(Source Source, bool Diagnostics);

/// <summary>
/// Lists sources: open to viewers, a private or permission-checked source shown only to those its
/// provider would show it to. Never lists what is inside a source.
/// </summary>
[Operation("sources.list", Scope = "knowledge:read", Risk = RiskTier.Read)]
public sealed class ListSources(ISourceStore sources, ISourceVisibility visibility, IOperationAuthorizer authorizer)
{
    public const int MaxTake = 200;

    public async Task<Attempt<SourceList, SourceReadStatus>> RunAsync(OperationContext context, ListSourcesQuery query, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Viewer))
            return Attempt<SourceList, SourceReadStatus>.Fail(SourceReadStatus.Forbidden, "You don't have permission to view sources.");
        if (query.Skip < 0)
            return Attempt<SourceList, SourceReadStatus>.Fail(SourceReadStatus.InvalidPage, "skip must be >= 0");
        if (query.Take is < 1)
            return Attempt<SourceList, SourceReadStatus>.Fail(SourceReadStatus.InvalidPage, "take must be >= 1");
        if (query.Take is > MaxTake)
            return Attempt<SourceList, SourceReadStatus>.Fail(SourceReadStatus.InvalidPage, $"take must be <= {MaxTake}");

        // One more than the page, to know whether another follows.
        var listed = await sources.ListAsync(query.Skip, query.Take is int take ? take + 1 : int.MaxValue, ct);
        bool hasMore = query.Take is int pageSize && listed.Count > pageSize;
        var page = hasMore ? listed.Take(query.Take!.Value) : listed;

        var visible = await visibility.ForAsync(context.User, ct);
        bool diagnostics = await authorizer.AllowsAsync(context.User, Policies.Admin);
        return Attempt<SourceList, SourceReadStatus>.Succeed(new SourceList(page.Where(visible).ToList(), hasMore, diagnostics));
    }
}

/// <summary>Reads one source; a source the caller may not see reads as missing, so its existence isn't revealed.</summary>
[Operation("sources.get", Scope = "knowledge:read", Risk = RiskTier.Read, Mcp = McpExposure.Generic)]
public sealed class GetSource(ISourceStore sources, ISourceVisibility visibility, IOperationAuthorizer authorizer)
{
    public async Task<Attempt<SourceView, SourceReadStatus>> RunAsync(OperationContext context, Guid sourceId, CancellationToken ct = default)
    {
        if (!await authorizer.AllowsAsync(context.User, Policies.Viewer))
            return Attempt<SourceView, SourceReadStatus>.Fail(SourceReadStatus.Forbidden, "You don't have permission to view sources.");

        var source = await sources.GetAsync(sourceId, ct);
        if (source is null || !(await visibility.ForAsync(context.User, ct))(source))
            return Attempt<SourceView, SourceReadStatus>.Fail(SourceReadStatus.NotFound, $"Source {sourceId} not found");

        bool diagnostics = await authorizer.AllowsAsync(context.User, Policies.Admin);
        return Attempt<SourceView, SourceReadStatus>.Succeed(new SourceView(source, diagnostics));
    }
}
