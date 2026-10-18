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
        scopes.MapGet("/{scope}/changes", ListChanges);
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

        string lastId = "";
        string cursor;
        if (string.IsNullOrEmpty(pageToken))
            cursor = journal.HeadCursor(scope);
        else if (!ListingToken.TryParse(pageToken, out lastId, out cursor))
            // A token this server did not issue is one it no longer recognises: the protocol answers 410, and
            // Connapse then starts a new listing.
            return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This page_token is not recognised; start the listing again.");

        // The listing is ordered by ID, so "after the last ID returned" is stable while files come and go.
        List<ExampleDocument> remaining = catalog.List(scope)
            .Where(d => string.CompareOrdinal(d.Id, lastId) > 0)
            .ToList();
        List<DocumentItem> page = remaining.Take(config.PageSize)
            .Select(d => DocumentItem.From(d, config.PermissionsEnabled))
            .ToList();

        return remaining.Count > page.Count
            ? Results.Ok(new DocumentPage(page, ListingToken.Format(page[^1].Id, cursor), null))
            : Results.Ok(new DocumentPage(page, null, cursor));
    }

    private static IResult GetContent(string scope, string id, HttpContext http, ExampleConfig config, FolderCatalog catalog)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        ExampleDocument? document = catalog.Find(scope, id);
        if (document is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document.");

        // Read once and label exactly those bytes, so a file replaced after the lookup cannot be served under
        // the previous version's ETag.
        byte[] bytes;
        try { bytes = File.ReadAllBytes(document.FullPath); }
        catch (FileNotFoundException) { return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such document."); }

        string etag = $"\"{FolderCatalog.VersionOf(bytes)}\"";
        http.Response.Headers.ETag = etag;
        if (http.Request.Headers.IfNoneMatch.ToString() == etag)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        return Results.Bytes(bytes, document.ContentType);
    }

    private static IResult ListChanges(
        string scope,
        [FromQuery] string? cursor,
        [FromQuery(Name = "page_token")] string? pageToken,
        ExampleConfig config,
        ChangeJournal journal)
    {
        if (!config.HasScope(scope)) return UnknownScope();

        string? from = string.IsNullOrEmpty(pageToken) ? cursor : pageToken;
        if (string.IsNullOrEmpty(from))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "cursor or page_token is required.");

        ChangeWindow? window = journal.Read(scope, from);
        if (window is null)
            return Results.Problem(statusCode: StatusCodes.Status410Gone, title: "This cursor is no longer valid; run a full listing.");

        // Serve each change as it was detected. Re-reading the file here could turn an upsert into a delete
        // that no later snapshot undoes, if the file were briefly missing and then restored unchanged.
        List<ChangeItem> items = window.Entries
            .Select(e => e.Document is null
                ? ChangeItem.Delete(e.Id)
                : ChangeItem.Upsert(DocumentItem.From(e.Document, config.PermissionsEnabled)))
            .ToList();

        return Results.Ok(new ChangePage(items, window.NextPageToken, window.NextCursor));
    }

    private static IResult UnknownScope() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown scope.");
}
