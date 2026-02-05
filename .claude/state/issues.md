# Known Issues

Bugs, tech debt, and workarounds. Prevents future sessions from re-discovering the same problems.

---

## Template

### Issue Title

**Severity**: Low | Medium | High | Critical

**Description**: What's wrong?

**Repro**: How to trigger?

**Workaround**: Current mitigation?

**Status**: Open | In Progress | Fixed in vX.X

---

## Expected Gotchas

### Ollama Cold Start

**Severity**: Low

**Description**: First Ollama request after startup takes 30-60s while model loads.

**Workaround**: Send warmup request on app start, or document expected delay.

### SQLite Write Contention

**Severity**: Medium

**Description**: SQLite struggles with concurrent writes → "database is locked" errors.

**Workaround**: Queue writes or use PostgreSQL for multi-user scenarios.

### Large File Chunking Memory

**Severity**: Medium

**Description**: Very large files (>100MB) can spike memory during chunking.

**Workaround**: Stream-based chunking for large files, or reject files over threshold.

---

## Fixed Issues

### DI Scope Violation: Singleton Services Consuming Scoped Services

**Severity**: Critical (app wouldn't start)

**Description**: `IngestionWorker` (singleton via `AddHostedService`) directly injected `IKnowledgeIngester` (scoped). `McpServer` (singleton) directly injected `IKnowledgeSearch` and `IDocumentStore` (both scoped). This violated DI scope rules and prevented app startup.

**Root Cause**: Scoped services depend on DbContext (also scoped). Singletons cannot hold references to scoped services because scoped instances are disposed after each request.

**Fix**: Both services now inject `IServiceScopeFactory` and create scopes when accessing scoped dependencies:
- `IngestionWorker.ProcessJobAsync()` creates a scope per job
- `McpServer.ExecuteSearchKnowledgeAsync()` and `ExecuteListDocumentsAsync()` create scopes per tool invocation

**Status**: Fixed

### FixedSizeChunker IndexOutOfRangeException

**Severity**: High (runtime crash)

**Description**: `FindNaturalBreakpoint` method accessed array indices beyond content length when target position equaled or exceeded content.Length, causing IndexOutOfRangeException.

**Root Cause**: Loop started at `target` without checking if `target < content.Length` before accessing `content[i]`.

**Fix**: Added bounds check at method start (`if (target >= content.Length) return content.Length`) and added `i < content.Length` check in all four search loops.

**Status**: Fixed in Phase 8 (2026-02-05) — discovered by unit tests before reaching production

### RecursiveChunker ArgumentOutOfRangeException

**Severity**: High (runtime crash)

**Description**: `ChunkAsync` method passed invalid `startIndex` to `IndexOf`, causing ArgumentOutOfRangeException when `currentOffset` exceeded content length.

**Root Cause**: `currentOffset` tracking could grow beyond actual content length, especially with overlap calculations.

**Fix**: Clamped `currentOffset` with `Math.Min(currentOffset, content.Length)` and added bounds check before calling `IndexOf`.

**Status**: Fixed in Phase 8 (2026-02-05) — discovered by unit tests before reaching production

### Parser Exception Handling - Cancellation Suppressed

**Severity**: Medium (cancellation didn't work)

**Description**: TextParser and OfficeParser caught `OperationCanceledException` in generic exception handler, preventing cancellation tokens from propagating properly.

**Root Cause**: Generic `catch (Exception ex)` block caught all exceptions including `OperationCanceledException` and `NotSupportedException`, returning empty documents instead of throwing.

**Fix**: Added explicit catch blocks to rethrow `OperationCanceledException` and `NotSupportedException` before the generic handler in both parsers.

**Status**: Fixed in Phase 8 (2026-02-05) — discovered by unit tests before reaching production

### DatabaseSettingsProvider - Missing Schema on Startup

**Severity**: High (integration tests all failing)

**Description**: `DatabaseSettingsProvider.Load()` was called during configuration building (before migrations run), causing exceptions when trying to query the `settings` table that doesn't exist yet in fresh database instances (e.g., Testcontainers).

**Root Cause**: Configuration building happens before app startup and migration execution. In integration tests with fresh Testcontainers, the database schema hasn't been created yet when settings are loaded.

**Fix**: Added try-catch block around settings query in `DatabaseSettingsProvider.Load()` to gracefully handle missing schema/tables. Application falls back to appsettings.json values when database settings cannot be loaded.

**Status**: Fixed (2026-02-05) — integration tests progressed from 0/14 passing to 3/14 passing

---

### Npgsql Dynamic JSON Serialization Not Enabled

**Severity**: Critical (all ingestion failing)

**Description**: Npgsql 9.x requires explicit opt-in for dynamic JSON serialization of Dictionary<string, string>. When trying to save DocumentEntity with Metadata dictionary, received error: "Type 'Dictionary`2' required dynamic JSON serialization, which requires an explicit opt-in".

**Root Cause**: Breaking change in Npgsql 9.x - dynamic JSON types now require calling `.EnableDynamicJson()` on NpgsqlDataSourceBuilder.

**Fix**: Modified ServiceCollectionExtensions.cs to create NpgsqlDataSource with EnableDynamicJson() before passing to AddDbContext.

**Status**: Fixed (2026-02-05)

### HashStream Does Not Support Seeking

**Severity**: High (all ingestion failing)

**Description**: IngestionPipeline.ComputeContentHashAsync() attempted to set stream Position on streams from MinIO, which are wrapped in HashStream that doesn't support seeking.

**Root Cause**: When IngestionWorker opens file from MinIO storage, the AWS S3 SDK returns a non-seekable HashStream. Code tried to reset position for hash computation and parsing.

**Fix**: Modified IngestAsync to detect non-seekable streams and copy to MemoryStream before processing. Added bounds check in ComputeContentHashAsync.

**Status**: Fixed (2026-02-05)

### PgVectorStore Metadata Keys Case Mismatch

**Severity**: Critical (all ingestion failing)

**Description**: PgVectorStore.UpsertAsync() expects lowercase "documentId" and "modelId" in metadata dictionary, but IngestionPipeline was passing "DocumentId" (capital D) and missing "modelId" entirely, causing ArgumentException during ingestion.

**Root Cause**: Inconsistent key naming between caller and validator. PgVectorStore checks for lowercase keys but pipeline used PascalCase.

**Fix**: Updated IngestionPipeline.cs line 177-181 to use lowercase "documentId" and added "modelId" from embedding settings.

**Status**: Fixed (2026-02-05)

### DocumentId Loss During Ingestion

**Severity**: Critical (documents never findable after upload)

**Description**: Upload endpoint generated a DocumentId and returned it to client, but IngestionPipeline generated a new Guid, saving document under different ID. Clients could never query their uploaded documents.

**Root Cause**:
- DocumentsEndpoints.cs:32 generated DocumentId for response
- IngestionJob included this ID but IngestionOptions didn't have DocumentId field
- IngestionPipeline.cs:74 generated new Guid.NewGuid() instead of using provided ID

**Fix**:
- Added DocumentId parameter to IngestionOptions record (IngestionModels.cs:4)
- Updated IngestionPipeline to use options.DocumentId if provided (IngestionPipeline.cs:74-76)
- Updated all callers: DocumentsEndpoints, ReindexService, McpServer

**Status**: Fixed (2026-02-05) — integration tests now passing (6/14)

### Integration Test Case Sensitivity

**Severity**: Low (1 test failing)

**Description**: Test searched for "artificial intelligence" (lowercase) but chunk contained "Artificial intelligence" (capital A). FluentAssertions .Contain() is case-sensitive by default.

**Fix**: Changed `.Contain()` to `.ContainEquivalentOf()` for case-insensitive matching in IngestionIntegrationTests.cs:143.

**Status**: Fixed (2026-02-05)

### SignalR Dynamic Binding with JsonElement

**Severity**: High (runtime crash during file upload)

**Description**: Blazor component used `dynamic` parameter in SignalR handler (`hubConnection.On<dynamic>("IngestionProgress", ...)`). When SignalR received JSON, it deserialized to `System.Text.Json.JsonElement`, which doesn't support dynamic property access like `progress.jobId`, causing `RuntimeBinderException`: 'System.Text.Json.JsonElement' does not contain a definition for 'jobId'.

**Root Cause**: `dynamic` keyword doesn't work with `JsonElement` — dynamic binding requires types that implement `IDynamicMetaObjectProvider`, which `JsonElement` doesn't. Properties must be accessed via `GetProperty()` methods, not dynamic syntax.

**Fix**:
- Created strongly-typed `IngestionProgressUpdate` DTO in IngestionModels.cs
- Updated Upload.razor to use `hubConnection.On<IngestionProgressUpdate>(...)` with typed handler
- Updated IngestionProgressBroadcaster to send typed DTO instead of anonymous object
- Added convention: **NEVER use `dynamic` keyword** in codebase

**Lesson**: Always use strongly-typed DTOs for SignalR, API responses, and external data. The slight overhead of creating a record is worth compile-time safety, IntelliSense, and avoiding runtime binding errors.

**Status**: Fixed (2026-02-05)

### Blazor SignalR Threading - Dispatcher Not Associated

**Severity**: Critical (runtime crash during upload progress updates)

**Description**: SignalR callback `HandleIngestionProgress` tried to call `StateHasChanged()` directly from background thread, causing `InvalidOperationException`: "The current thread is not associated with the Dispatcher. Use InvokeAsync() to switch execution to the Dispatcher when triggering rendering or component state."

**Root Cause**: SignalR messages arrive on background threads, not on the Blazor component's synchronization context. Calling `StateHasChanged()` or modifying component state from non-UI threads violates Blazor's threading model.

**Fix**: Wrapped all state mutations and `StateHasChanged()` calls in `InvokeAsync(() => { ... })` to marshal execution back to the component's thread. Changed method signature from `void` to `async Task` to properly await the marshalling.

**Code Change** (Upload.razor:264-278):
```csharp
// BEFORE (wrong - crashes)
private void HandleIngestionProgress(IngestionProgressUpdate progress)
{
    var file = uploadedFiles.FirstOrDefault(f => f.JobId == progress.JobId);
    if (file != null)
    {
        file.State = progress.State;
        StateHasChanged(); // ❌ Exception!
    }
}

// AFTER (correct - marshals to UI thread)
private async Task HandleIngestionProgress(IngestionProgressUpdate progress)
{
    await InvokeAsync(() =>
    {
        var file = uploadedFiles.FirstOrDefault(f => f.JobId == progress.JobId);
        if (file != null)
        {
            file.State = progress.State;
            StateHasChanged(); // ✅ Safe on UI thread
        }
    });
}
```

**Lesson**: All SignalR callbacks in Blazor Interactive Server components must use `InvokeAsync()` for state mutations and rendering. This is the standard pattern provided by `ComponentBase`.

**Status**: Fixed (2026-02-05)

### RemoteNavigationManager Not Initialized in Background Services

**Severity**: Critical (all document uploads failing)

**Description**: Upload endpoint accepted files successfully, but ingestion immediately failed with error: "Processing failed: 'RemoteNavigationManager' has not been initialized." The error occurred in `IngestionWorker` (background service) when processing jobs.

**Root Cause**:
- Program.cs registered a scoped `HttpClient` that resolved `NavigationManager` during service configuration
- `NavigationManager` is a scoped service that only exists in HTTP request/Blazor component contexts
- Attempting to resolve it during root-level `AddHttpClient` configuration caused: `InvalidOperationException: 'Cannot resolve scoped service 'Microsoft.AspNetCore.Components.NavigationManager' from root provider.'`
- Even if that worked, background services (like `IngestionWorker`) don't have a `NavigationManager` in their scope
- When `IngestionWorker` created a scope and resolved `IKnowledgeIngester`, which needed `OllamaEmbeddingProvider`, which needed `HttpClient`, the DI container tried to provide the scoped `HttpClient` configured with `NavigationManager`
- This caused the "RemoteNavigationManager has not been initialized" error because background services don't have a Blazor rendering context

**Fix**:
1. Changed `AddHttpClient()` from scoped to **named** client: `builder.Services.AddHttpClient("BlazorClient")` (no configuration callback)
2. Updated all Blazor components to inject `IHttpClientFactory` and `NavigationManager`
3. Components create client and set `BaseAddress` lazily on first access using a property getter pattern

**Code Pattern** (Upload.razor, Search.razor, all Settings tabs):
```csharp
@inject IHttpClientFactory HttpClientFactory
@inject NavigationManager Navigation

@code {
    private HttpClient? _httpClient;
    private HttpClient Http
    {
        get
        {
            if (_httpClient == null)
            {
                _httpClient = HttpClientFactory.CreateClient("BlazorClient");
                _httpClient.BaseAddress = new Uri(Navigation.BaseUri);
            }
            return _httpClient;
        }
    }
}
```

**Lesson**:
- Named `HttpClient` registrations prevent conflicts with typed `HttpClient` registrations (e.g., `AddHttpClient<OllamaEmbeddingProvider>()`)
- Never resolve scoped services during root-level service configuration
- Blazor components can access `NavigationManager` in their scoped context, but background services cannot
- Components should configure their `HttpClient` in their own initialization, not during global DI registration

**Status**: Fixed (2026-02-05)

## Open Issues

### Connection Test Failures

**Severity**: Medium (3 tests failing)

**Description**: Connection testing endpoints for MinIO and Ollama returning Success=False even with valid credentials from Testcontainers.

**Affected Tests**:
- TestConnection_MinioWithValidCredentials_ReturnsSuccess
- TestConnection_MinioWithInvalidCredentials_ReturnsFailure
- TestConnection_OllamaUnavailable_ReturnsFailure

**Status**: Open — peripheral feature, not blocking core functionality

### Reindex Detection Not Working

**Severity**: Medium (2 tests failing)

**Description**: Content-hash comparison not properly detecting unchanged documents during reindex, causing unnecessary reprocessing.

**Affected Tests**:
- Reindex_UnchangedDocument_SkipsReprocessing
- Reindex_ByCollection_OnlyReindexesFilteredDocuments

**Status**: Open — peripheral feature, force mode works

### Settings Live Reload Not Working

**Severity**: Medium (3 tests failing)

**Description**: IOptionsMonitor not reflecting database setting changes immediately in integration tests.

**Affected Tests**:
- UpdateSettings_ChunkingSettings_LiveReloadWorks
- UpdateSettings_SearchSettings_LiveReloadWorks
- UpdateSettings_MultipleCategories_IndependentlyUpdateable

**Status**: Open — GET settings works, live reload mechanism needs investigation

---

<!-- Add issues as discovered -->
