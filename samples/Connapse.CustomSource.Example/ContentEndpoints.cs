namespace Connapse.CustomSource.Example;

public static class ContentEndpoints
{
    public static void MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/connapse-source", Discovery).AddEndpointFilter<BearerTokenFilter>();

        RouteGroupBuilder scopes = app.MapGroup("/v1/scopes").AddEndpointFilter<BearerTokenFilter>();
        scopes.MapGet("", ListScopes);
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
}
