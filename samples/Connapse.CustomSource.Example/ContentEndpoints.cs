using Microsoft.AspNetCore.Mvc;

namespace Connapse.CustomSource.Example;

public static class ContentEndpoints
{
    public static void MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/connapse-source", Discovery).AddEndpointFilter<BearerTokenFilter>();

        RouteGroupBuilder scopes = app.MapGroup("/v1/scopes").AddEndpointFilter<BearerTokenFilter>();
        scopes.MapGet("", ListScopes);
        scopes.MapGet("/{scope}/documents", ListDocuments);
        scopes.MapGet("/{scope}/documents/{id}/content", GetContent);
    }

    private static IResult Discovery(HttpRequest request, ExampleConfig config)
    {
        string baseUrl = config.PublicBaseUrl ?? $"{request.Scheme}://{request.Host}";
        PermissionCapabilities? permissions = config.PermissionsEnabled
            ? new PermissionCapabilities(
                new CheckCapability(config.MaxCheckBatch),
                new AccessKeysCapability(0),
                config.LinkSecret is null ? null : new LinkingCapability($"{baseUrl.TrimEnd('/')}/link"))
            : null;

        return Results.Ok(new DiscoveryDocument(
            ["1"],
            config.Name,
            new Capabilities(Changes: true, Tombstones: true, MinSyncIntervalSeconds: 60, config.PageSize, permissions)));
    }

    private static IResult ListScopes(ExampleConfig config) =>
        Results.Ok(new ScopePage(config.Scopes.Select(s => new ScopeItem(s.Id, s.Name, s.Description)).ToList(), null));

    private static IResult ListDocuments(
        string scope,
        [FromQuery(Name = "page_token")] string? pageToken,
        ExampleConfig config,
        FolderCatalog catalog,
        ChangeJournal journal)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        int offset = 0;
        string cursor;
        if (string.IsNullOrEmpty(pageToken))
            cursor = journal.HeadCursor(scope);
        else if (!ListingToken.TryParse(pageToken, out offset, out cursor))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed page_token.");

        IReadOnlyList<ExampleDocument> all = catalog.List(scope);
        List<DocumentItem> page = all.Skip(offset).Take(config.PageSize)
            .Select(d => DocumentItem.From(d, config.PermissionsEnabled))
            .ToList();
        int next = offset + page.Count;

        return next < all.Count
            ? Results.Ok(new DocumentPage(page, ListingToken.Format(next, cursor), null))
            : Results.Ok(new DocumentPage(page, null, cursor));
    }

    private static IResult GetContent(string scope, string id, HttpContext http, ExampleConfig config, FolderCatalog catalog)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        ExampleDocument? document = catalog.Find(scope, id);
        if (document is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document.");

        string etag = $"\"{document.Version}\"";
        if (http.Request.Headers.IfNoneMatch.ToString() == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        return Results.Stream(File.OpenRead(document.FullPath), document.ContentType);
    }

    private static IResult UnknownScope() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown scope.");
}
