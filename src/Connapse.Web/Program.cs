using System.Reflection;
using System.Text.Json.Serialization;
using Connapse.Core;
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
using Connapse.Core.Interfaces;
using Connapse.Web;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Serialize enums as strings in all Minimal API JSON responses and request bodies.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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

var dbSettingsProvider = builder.Configuration.AddDatabaseSettings(connectionString);

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

// In-process event bus so FileBrowser can receive real-time file-list changes
// (add/delete) triggered by ConnectorWatcherService without polling.
builder.Services.AddSingleton<FileBrowserChangeNotifier>();

// Add background services
builder.Services.AddHostedService<IngestionProgressBroadcaster>();

// ConnectorWatcherService: manages FileSystemWatcher instances per Filesystem container.
// Registered as singleton so endpoints can call StartWatchingContainer() at runtime.
builder.Services.AddSingleton<ConnectorWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectorWatcherService>());

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
})
.WithHttpTransport()
.WithToolsFromAssembly()
.AddAuthorizationFilters();

if (allowAnonDiscovery)
{
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
builder.Services.AddScoped<ICloudScopeService, CloudScopeService>();
builder.Services.AddScoped<IUploadService, UploadService>();

builder.Services.AddConnapseStorage(builder.Configuration);

// Add document ingestion pipeline
builder.Services.AddDocumentIngestion();

// Add knowledge search (hybrid vector + keyword search)
builder.Services.AddKnowledgeSearch();

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
            policy.SetIsOriginAllowed(origin =>
                new Uri(origin).Host == "localhost" || new Uri(origin).Host == "127.0.0.1")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        }
    });
});

// Remove default "Server: Kestrel" header from all responses
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

// Apply pending migrations and ensure infrastructure is ready
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

    var minio = scope.ServiceProvider.GetService<MinioFileSystem>();
    if (minio is not null)
        await minio.EnsureBucketExistsAsync();

    // Ensure partial IVFFlat indexes exist for each embedding model in chunk_vectors
    var vectorColumnManager = scope.ServiceProvider.GetRequiredService<VectorColumnManager>();
    await vectorColumnManager.EnsureIndexesAsync();

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

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Health check — returns 200 when the app is fully initialized.
// Used by integration tests to replace Task.Delay(2000) startup waits.
app.MapGet("/health", () => Results.Ok());

// Map API endpoints — antiforgery is disabled for all API routes because they
// authenticate via JWT / PAT bearer tokens, not browser form submissions.
var api = app.MapGroup("").DisableAntiforgery()
    .RequireRateLimiting(RateLimitingExtensions.ApiPolicy);
api.MapAuthEndpoints();
api.MapCloudIdentityEndpoints();
api.MapAgentEndpoints();
api.MapContainersEndpoints();
api.MapDocumentsEndpoints();
api.MapFoldersEndpoints();
api.MapSearchEndpoints();
api.MapBatchesEndpoints();
api.MapSettingsEndpoints();

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
