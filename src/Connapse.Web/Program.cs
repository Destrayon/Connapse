using Connapse.Core;
using Connapse.Ingestion.Extensions;
using Connapse.Ingestion.Pipeline;
using Connapse.Search.Extensions;
using Connapse.Storage.Data;
using Connapse.Storage.Extensions;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Settings;
using Connapse.Web.Components;
using Connapse.Web.Endpoints;
using Connapse.Web.Hubs;
using Connapse.Web.Mcp;
using Connapse.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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

// Add background services
builder.Services.AddHostedService<IngestionProgressBroadcaster>();

// Add MCP server
builder.Services.AddSingleton<McpServer>();

builder.Services.AddConnapseStorage(builder.Configuration);

// Add document ingestion pipeline
builder.Services.AddDocumentIngestion();

// Add knowledge search (hybrid vector + keyword search)
builder.Services.AddKnowledgeSearch();

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
builder.Services.Configure<WebSearchSettings>(
    builder.Configuration.GetSection("Knowledge:WebSearch"));
builder.Services.Configure<StorageSettings>(
    builder.Configuration.GetSection("Knowledge:Storage"));

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

var app = builder.Build();

// Apply pending migrations and ensure infrastructure is ready
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
    await db.Database.MigrateAsync();

    var minio = scope.ServiceProvider.GetService<MinioFileSystem>();
    if (minio is not null)
        await minio.EnsureBucketExistsAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseCors();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Map API endpoints
app.MapContainersEndpoints();
app.MapDocumentsEndpoints();
app.MapFoldersEndpoints();
app.MapSearchEndpoints();
app.MapBatchesEndpoints();
app.MapSettingsEndpoints();
app.MapMcpEndpoints();

// Map SignalR hub
app.MapHub<IngestionHub>("/hubs/ingestion");

app.Run();

// Make Program accessible to WebApplicationFactory for integration tests
public partial class Program { }
