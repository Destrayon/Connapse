using AIKnowledge.Core;
using AIKnowledge.Ingestion.Extensions;
using AIKnowledge.Ingestion.Pipeline;
using AIKnowledge.Search.Extensions;
using AIKnowledge.Storage.Data;
using AIKnowledge.Storage.Extensions;
using AIKnowledge.Storage.FileSystem;
using AIKnowledge.Storage.Settings;
using AIKnowledge.Web.Components;
using AIKnowledge.Web.Endpoints;
using AIKnowledge.Web.Hubs;
using AIKnowledge.Web.Mcp;
using AIKnowledge.Web.Services;
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

builder.Services.AddAIKnowledgeStorage(builder.Configuration);

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
