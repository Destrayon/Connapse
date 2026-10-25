using System.Reflection;
using System.Text.Json.Serialization;
using Connapse.Application.Extensions;
using Connapse.Background;
using Connapse.Core;
using Connapse.Core.Extensions;
using Connapse.Identity;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Ingestion.Extensions;
using Connapse.Ingestion.Pipeline;
using Connapse.Search.Extensions;
using Connapse.Storage.Data;
using Connapse.Storage.Extensions;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Settings;
using Connapse.Storage.Vectors;
using Connapse.Web.Components;
using Connapse.Web.Endpoints;
using Connapse.Web.Hubs;
using Connapse.Web.Mcp;
using Connapse.Web.Problems;
using Connapse.Core.Interfaces;
using Connapse.Web;
using Connapse.Web.Services;
using Hangfire;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

// First, before anything touches Regex: the default match timeout is read once, at type init.
Connapse.Core.Utilities.RegexTimeout.ApplyProcessDefault();

var builder = WebApplication.CreateBuilder(args);

// The build runs this entry point to write the OpenAPI document (#857). It needs only the mapped
// endpoints, so it must not touch the database, start background services or schedule jobs: a
// build has no database.
bool generatingOpenApiDocument = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";

// Serialize enums as strings in all Minimal API JSON responses and request bodies.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // A source scope's "type" may come anywhere in the object, as clients write members in any order (#859).
    options.SerializerOptions.AllowOutOfOrderMetadataProperties = true;
});

// Persist Data Protection keys to the appdata volume so they survive container restarts.
// In dev this lands in <ContentRoot>/appdata/DataProtection-Keys; in the container it
// maps to /app/appdata/DataProtection-Keys which is on the named 'appdata' Docker volume.
var dpKeysDir = new DirectoryInfo(
    Path.Combine(builder.Environment.ContentRootPath, "appdata", "DataProtection-Keys"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(dpKeysDir)
    .SetApplicationName("Connapse");

// Support running behind a reverse proxy (e.g. nginx, Caddy, Traefik).
// This makes UseHttpsRedirection a no-op when the proxy already terminated TLS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Allow any proxy/network — restrict this if your proxy IPs are known
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add database-backed settings (overrides appsettings.json)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Database connection string not found.");

// Loading this source reads the settings table, so OpenAPI generation keeps the provider in DI
// (endpoints take it as a service) without adding it to configuration.
var dbSettingsProvider = generatingOpenApiDocument
    ? new DatabaseSettingsProvider(_ => { })
    : builder.Configuration.AddDatabaseSettings(connectionString);

// Register DatabaseSettingsProvider for settings reload
builder.Services.AddSingleton(dbSettingsProvider);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSignalR();

// Add named HttpClient for Blazor components
// BaseAddress will be set by components using NavigationManager
// This avoids conflicts with typed HttpClient registrations used by background services
builder.Services.AddHttpClient("BlazorClient");

// In-process event bus so Blazor Server components can receive ingestion progress
// notifications without creating a server-to-server SignalR client connection.
builder.Services.AddSingleton<IngestionProgressNotifier>();

// Profile-context sidebar menu. Default returns Profile + Integrations.
// Downstream apps (e.g. multi-tenant Cloud) override via services.Replace().
builder.Services.AddScoped<IProfileMenuProvider, DefaultProfileMenuProvider>();

// Pushes document status transitions to the UI. Registered against the Core interface so the
// document lifecycle in Connapse.Storage can call it without a Web reference.
builder.Services.AddSingleton<IIngestionStateBroadcaster, IngestionProgressBroadcaster>();

// Polls every enabled source and reconciles it with its remote. Replaces
// ConnectorWatcherService, which enumerated containers — after the #350 backfill moved
// external storage into `sources`, it found nothing and syncing stopped entirely.
//
// Singleton-as-hosted-service so POST /api/sources/{id}/sync can run one cycle on demand
// against the same instance. #364 registered it as a plain hosted service on the grounds
// that nothing called into it at runtime; the sync-now route is that caller.
builder.Services.AddSingleton<SourceSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceSyncService>());
builder.Services.AddSingleton<Connapse.Application.Ports.ISourceSync>(sp => sp.GetRequiredService<SourceSyncService>());
builder.Services.AddScoped<Connapse.Application.Ports.ISummaryRollup, Connapse.Web.Services.HangfireSummaryRollup>();

// Carries an existing deployment's per-user permissions across the upgrade that separated
// "enforcing" from "configured". Without it, an installation that already had filtering working
// would come back up unfiltered -- the exact failure that separation exists to prevent.
builder.Services.AddHostedService<CloudEnforcementLatch>();

// Folds BM25 keyword-statistics deltas written by the chunks triggers (#548).
builder.Services.AddHostedService<Bm25StatsFoldService>();
builder.Services.AddHostedService<VectorIndexMaintenanceService>();

// Tracks background reindex state so admins can see success/failure via the status endpoint.
builder.Services.AddSingleton<ReindexStateService>();

// Add MCP server (official SDK)
var allowAnonDiscovery = builder.Configuration.GetValue<bool>("Mcp:AllowAnonymousDiscovery");

var mcpBuilder = builder.Services.AddMcpServer(options =>
{
    var assemblyVersion = typeof(Program).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
    options.ServerInfo = new() { Name = "Connapse", Version = assemblyVersion };
    options.ServerInstructions = Connapse.Web.Mcp.McpServerConfig.McpServerInstructions;
})
.WithHttpTransport()
.WithToolsFromAssembly()
.AddAuthorizationFilters()
.WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, ct) =>
    context.Services!.GetRequiredService<Connapse.Web.Mcp.McpApprovalGate>().HandleAsync(context, () => next(context, ct), ct)));
builder.Services.AddScoped<Connapse.Web.Mcp.McpApprovalGate>();

if (allowAnonDiscovery)
{
    builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Connapse.Web.Mcp.AnonymousMcpDiscoveryHandler>();
    mcpBuilder.WithRequestFilters(filters =>
    {
        filters.AddCallToolFilter(next => async (context, ct) =>
        {
            if (context.User?.Identity?.IsAuthenticated != true ||
                !(context.User.IsInRole("Owner") ||
                  context.User.IsInRole("Admin") ||
                  context.User.IsInRole("Agent")))
            {
                return new ModelContextProtocol.Protocol.CallToolResult
                {
                    Content = [new ModelContextProtocol.Protocol.TextContentBlock
                    {
                        Text = "Authentication required. Provide an API key via X-Api-Key header."
                    }],
                    IsError = true
                };
            }
            return await next(context, ct);
        });
    });
}

builder.Services.AddMemoryCache();

// The REST API's shape, generated at build time into docs/api/openapi.json (#857). No route serves it.
builder.Services.AddOpenApi(options => options
    .AddSchemaTransformer<Connapse.Web.Endpoints.DeprecatedScopeJsonTransformer>()
    .AddSchemaTransformer<Connapse.Web.Problems.ProblemDetailsSchemaTransformer>()
    .AddSchemaTransformer<Connapse.Web.Endpoints.AuditDetailsSchemaTransformer>()
    .AddOperationTransformer<Connapse.Web.Problems.RateLimitResponseTransformer>()
    .AddOperationTransformer<Connapse.Web.Endpoints.MultipartFormTransformer>()
    .AddDocumentTransformer<Connapse.Web.Endpoints.DiscriminatedSubtypesTransformer>());

// Pre-flight for the Sources create form. Not an enforcement point — ConnectorFactory
// re-checks the same policy on every sync cycle, which is what catches an allowlist narrowed
// after a source already exists.
builder.Services.AddScoped<SourceScopePreflight>();
builder.Services.AddScoped<IProviderSetupReader, ProviderSetupReader>();
builder.Services.AddScoped<Connapse.Web.Services.AtlassianSiteService>();
builder.Services.AddScoped<Connapse.Web.Services.CustomSourceSetup>();
builder.Services.AddScoped<Connapse.Web.Services.CustomSourceConnectionMigration>();
builder.Services.AddScoped<Connapse.Web.Services.CustomProviderSetup>();
builder.Services.AddScoped<Connapse.Web.Services.CustomLinkService>();
builder.Services.AddScoped<Connapse.Application.Ports.ICustomAccountLinks>(sp => sp.GetRequiredService<Connapse.Web.Services.CustomLinkService>());
builder.Services.AddScoped<Connapse.Web.Services.CustomLinkRemovalJob>();
builder.Services.AddScoped<Connapse.Web.Services.AtlassianLinkAppService>();
builder.Services.AddScoped<Connapse.Web.Services.AtlassianLinkAppCheckJob>();
builder.Services.AddScoped<Connapse.Web.Services.ConnectionAdminService>();
builder.Services.AddSingleton<Connapse.Web.Services.GitHubManifestRequests>();
builder.Services.AddScoped<Connapse.Web.Services.PrivateSourceVisibility>();
builder.Services.AddScoped<Connapse.Application.Ports.ISourceVisibility>(sp => sp.GetRequiredService<Connapse.Web.Services.PrivateSourceVisibility>());
builder.Services.AddScoped<Connapse.Application.Ports.IOperationAuthorizer, Connapse.Web.Services.OperationAuthorizer>();
builder.Services.AddScoped<Connapse.Application.Ports.ISourceScopeRules, Connapse.Web.Services.SourceScopeRules>();
builder.Services.AddScoped<Connapse.Application.Ports.ISourceCreateRules, Connapse.Web.Services.SourceCreateRules>();
builder.Services.AddScoped<Connapse.Web.Services.DocumentReadGuard>();
builder.Services.AddScoped<Connapse.Web.Services.GitHubAppUsage>();

// So an MCP tool can name its caller. Tools receive an IServiceProvider and nothing else, so
// without this the MCP surface cannot resolve a principal at all — and #421 will deny what it
// cannot identify, which would silently turn every MCP search into no results.
builder.Services.AddHttpContextAccessor();

// Injected rather than read statically so the provisioning window ProviderSetupReader applies to a
// freshly created access key can be tested without waiting an hour for it.
builder.Services.AddSingleton(TimeProvider.System);

// Singleton: whether this process is in a container cannot change while it runs, and the
// resolver reads that once. See ContainerHostResolver.
builder.Services.AddSingleton<IContainerHostResolver, ContainerHostResolver>();
builder.Services.AddScoped<IUploadService, UploadService>();

builder.Services.AddConnapseStorage(builder.Configuration);

// Add document ingestion pipeline
builder.Services.AddDocumentIngestion();

// Add Hangfire background-job runtime (PostgreSQL storage + worker pools + dashboard auth)
builder.Services.AddConnapseHangfire(builder.Configuration);

// Add knowledge search (hybrid vector + keyword search)
builder.Services.AddKnowledgeSearch();

// Add the operations every surface calls (epic #783)
builder.Services.AddApplication();
builder.Services.AddConnapseProblemDetails();
// Validation runs on request records before handlers; Core declares records too, so it registers its own.
builder.Services.AddCoreValidation();
builder.Services.AddValidation();

// Add Identity, authentication, and authorization
builder.Services.AddConnapseIdentity(builder.Configuration);
builder.Services.AddConnapseAuthentication(builder.Configuration);
builder.Services.AddConnapseAuthorization();

// Provide auth state to Blazor components via cascading parameter
builder.Services.AddCascadingAuthenticationState();

// Configure settings with IOptionsMonitor for live reload
builder.Services.Configure<EmbeddingSettings>(
    builder.Configuration.GetSection("Knowledge:Embedding"));
builder.Services.Configure<ChunkingSettings>(
    builder.Configuration.GetSection("Knowledge:Chunking"));
builder.Services.Configure<SearchSettings>(
    builder.Configuration.GetSection("Knowledge:Search"));
builder.Services.Configure<LlmSettings>(
    builder.Configuration.GetSection("Knowledge:LLM"));
builder.Services.Configure<UploadSettings>(
    builder.Configuration.GetSection("Knowledge:Upload"));
builder.Services.Configure<SummarySettings>(
    builder.Configuration.GetSection("Knowledge:Summary"));

// Add rate limiting
builder.Services.AddConnapseRateLimiting(builder.Configuration);

// Add CORS policy — restrict to same-origin by default
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (allowedOrigins is { Length: > 0 })
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
        else
        {
            // Default: same-origin only (no cross-origin requests)
            //
            // TryCreate, not `new Uri`. An Origin header is not always a URL: a browser sends the
            // literal string "null" for an opaque origin, which is what a cross-site POST arrives
            // with once it has been through a redirect — exactly the shape of a SAML sign-in. The
            // constructor throws on that, and because CORS runs ahead of the exception handler the
            // throw became a 500 on any request carrying such a header, from anyone.
            //
            // An unparseable origin is refused rather than allowed. "null" is opaque by definition
            // and there is nothing to match it against.
            //
            // IdnHost, matching SamlServiceProvider: the bracketed form of an IPv6 literal arrives
            // as "[::1]" from Host, and the loopback set is written unbracketed.
            policy.SetIsOriginAllowed(origin =>
                Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                && parsed.IdnHost is "localhost" or "127.0.0.1" or "::1")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

// Remove default "Server: Kestrel" header from all responses
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Generating the OpenAPI document still starts the host, which would construct every hosted service.
if (generatingOpenApiDocument)
    builder.Services.RemoveAll<IHostedService>();

var app = builder.Build();

// Apply pending migrations and ensure infrastructure is ready
if (!generatingOpenApiDocument)
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        await db.Database.MigrateAsync();

        // Migrate Identity tables (separate migration history)
        var identityDb = scope.ServiceProvider.GetRequiredService<ConnapseIdentityDbContext>();
        await identityDb.Database.MigrateAsync();

        // Seed default roles and admin user
        var adminSeed = scope.ServiceProvider.GetRequiredService<AdminSeedService>();
        await adminSeed.SeedAsync();

        // Custom sources made before they needed a connection move onto one; it runs before the sync starts.
        try
        {
            await scope.ServiceProvider.GetRequiredService<Connapse.Web.Services.CustomSourceConnectionMigration>().RunAsync();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Custom sources could not be moved onto connections; they keep reading with their own tokens until the next start.");
        }

        var minio = scope.ServiceProvider.GetService<MinioFileSystem>();
        if (minio is not null)
        {
            try
            {
                await minio.EnsureBucketExistsAsync();
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "MinIO is not reachable — bucket check skipped. File uploads will fail until MinIO is available.");
            }
        }

    }

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// API/MCP routes return structured JSON errors — skip the Blazor error-page re-execution
// so that empty 401/403 responses are not intercepted by UseStatusCodePagesWithReExecute.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") ||
        ctx.Request.Path.StartsWithSegments("/mcp"))
    {
        var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>();
        if (feature is not null)
            feature.Enabled = false;
    }
    await next(ctx);
});
app.UseApiProblemDetails();

app.UseForwardedHeaders();

// Security headers — after UseForwardedHeaders so Request.IsHttps reflects the forwarded scheme
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "connect-src 'self' ws: wss:; img-src 'self' data:; font-src 'self'; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none';";

    if (ctx.Request.IsHttps)
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

    await next(ctx);
});

app.UseHttpsRedirection();
app.UseCors();
// Before authentication, so a rebinding page learns nothing about credentials (#846).
app.UseMcpOriginValidation(app.Configuration);

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Hangfire dashboard — gated by HangfireDashboardAuthFilter (defers to "RequireAdmin" policy).
// Its storage opens a database connection, so OpenAPI generation leaves it out.
if (!generatingOpenApiDocument)
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[]
        {
        app.Services.GetRequiredService<Hangfire.Dashboard.IDashboardAuthorizationFilter>()
    }
    });

// Register recurring jobs idempotently at startup. AddOrUpdate is safe to call repeatedly.
if (!generatingOpenApiDocument)
    using (var scope = app.Services.CreateScope())
    {
        var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
        // Every 5 minutes: scan for containers with stale summaries (per-doc summaries newer
        // than container summary) and enqueue a rollup for those that have settled (no in-flight
        // PerDocSummary jobs). This replaces per-PerDocSummary scheduling: bursts of N uploads
        // converge to 1 rollup instead of N. Pattern matches Postgres materialized view refresh
        // and Algolia derived-index updates — sweep-based, not write-through, for expensive
        // aggregates over frequently-changing base data.
        recurringJobManager.AddOrUpdate<Connapse.Background.Jobs.ISummaryJobs>(
            recurringJobId: "summary-sweep-stale-containers",
            methodCall: s => s.SweepStaleContainersAsync(default),
            cronExpression: "*/5 * * * *");

        // Every 10 minutes: re-enqueue documents whose ingestion job was lost. Status lives on the
        // document row, so a document whose job died would otherwise sit Queued or Processing for good.
        recurringJobManager.AddOrUpdate<Connapse.Background.Jobs.IIngestionJobs>(
            recurringJobId: "ingestion-requeue-stuck-documents",
            methodCall: j => j.RequeueStuckDocumentsAsync(default),
            cronExpression: "*/10 * * * *");

        // Daily: ask Atlassian whether it still accepts the stored linking app, so a revoked secret
        // shows on the Providers page before someone's sign-in fails.
        recurringJobManager.AddOrUpdate<Connapse.Web.Services.AtlassianLinkAppCheckJob>(
            recurringJobId: Connapse.Web.Services.AtlassianLinkAppCheckJob.RecurringJobId,
            methodCall: j => j.RunAsync(default),
            cronExpression: Cron.Daily());

        // Every minute: tell custom providers' middle men about deleted users' links until each acknowledges.
        recurringJobManager.AddOrUpdate<Connapse.Web.Services.CustomLinkRemovalJob>(
            recurringJobId: Connapse.Web.Services.CustomLinkRemovalJob.RecurringJobId,
            methodCall: j => j.RunAsync(default),
            cronExpression: Cron.Minutely());

        // The orphaned-grant sweep was removed in #463, but Hangfire keeps recurring-job definitions
        // in the database independently of the code that registered them. Left in place, the old
        // entry would fire every half hour into a job type that no longer exists.
        recurringJobManager.RemoveIfExists("grant-reconcile-orphaned");
    }

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Health check — returns 200 when the app is fully initialized.
// Used by integration tests to replace Task.Delay(2000) startup waits.
app.MapGet("/health", () => Results.Ok()).Produces(StatusCodes.Status200OK);

// Server info — lets CLI detect whether it's talking to Cloud or self-hosted
app.MapGet("/api/v1/info", () => Results.Json(new Connapse.Web.Endpoints.ServerInfo(
    "self-hosted",
    typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0",
    ["mcp", "connectors", "agents"])))
    .AllowAnonymous().RequireRateLimiting(RateLimitingExtensions.ApiPolicy)
    .Produces<Connapse.Web.Endpoints.ServerInfo>();

// Map API endpoints — antiforgery is disabled for all API routes because they
// authenticate via JWT / PAT bearer tokens, not browser form submissions.
var api = app.MapGroup("").DisableAntiforgery()
    .RequireRateLimiting(RateLimitingExtensions.ApiPolicy)
    .WithProblemResults();
api.MapAuthEndpoints();
api.MapCloudIdentityEndpoints();
api.MapCustomLinkEndpoints();
api.MapAgentEndpoints();
api.MapContainersEndpoints();
api.MapSourcesEndpoints();
api.MapAuditEndpoints();
api.MapOAuthClientEndpoints();
api.MapDocumentsEndpoints();
api.MapFoldersEndpoints();
api.MapSearchEndpoints();
api.MapBatchesEndpoints();
api.MapSettingsEndpoints();
api.MapGitHubAppEndpoints();
api.MapAtlassianEndpoints();
api.MapAtlassianLinkAppEndpoints();

// Map OAuth 2.1 endpoints (discovery, token, registration)
app.MapOAuthEndpoints();

// Map built-in Identity API endpoints (register, login, refresh, 2FA, etc.)
// Auth rate limit protects anonymous endpoints (login, register) from brute force.
// Note: both the parent API policy and this auth policy apply (ASP.NET Core stacks them).
// The auth policy (10/min per IP) is the binding constraint for anonymous callers.
api.MapGroup("/api/v1/identity")
    .RequireRateLimiting(RateLimitingExtensions.AuthPolicy)
    .MapIdentityApi<ConnapseUser>();

// Map MCP server (Streamable HTTP + legacy SSE transport)
var mcpEndpoint = app.MapMcp("/mcp")
    .RequireRateLimiting(RateLimitingExtensions.McpPolicy);

if (!allowAnonDiscovery)
    mcpEndpoint.RequireAuthorization("RequireAgent");

// Map SignalR hub
app.MapHub<IngestionHub>("/hubs/ingestion");

app.Run();

// Make Program accessible to WebApplicationFactory for integration tests
public partial class Program { }
