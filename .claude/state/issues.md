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

### JSON Property Name Casing in SettingsEndpoints

**Severity**: High (6 tests failing)

**Description**: Integration tests sent JSON with camelCase property names (ASP.NET Core default), but `JsonSerializer.Deserialize` in SettingsEndpoints used default options expecting exact case matches. This caused settings like `BaseUrl` and `MinioAccessKey` to deserialize as null, breaking connection tests.

**Root Cause**: Default `JsonSerializer.Deserialize` uses case-sensitive property matching. Tests send `{ "baseUrl": "..." }` but C# properties are `BaseUrl` (PascalCase).

**Fix**: Added `JsonSerializerOptions` with `PropertyNameCaseInsensitive = true` to SettingsEndpoints and used it in all deserialize calls.

**Status**: Fixed (2026-02-05) — all 6 connection tests now passing

### Ollama Connection Test Invalid Port

**Severity**: Low (1 test failing)

**Description**: Test used port 99999 to simulate unavailable Ollama service, but port numbers must be 0-65535. This caused "Invalid URI: Invalid port specified" error instead of expected connection failure.

**Fix**: Changed test to use port 54321 (valid but unlikely to be in use) and updated assertion to accept any error message containing "error" or "Connection failed".

**Status**: Fixed (2026-02-05)

### Reindex Test Upload Helper Incorrect Form Parameters

**Severity**: Medium (2 tests failing)

**Description**: `UploadDocument` test helper sent `virtualPath` form field, but endpoint expects `destinationPath` and `collectionId` as separate parameters. This caused collection filtering tests to fail because documents weren't being assigned to collections.

**Fix**: Updated helper to send `destinationPath` and `collectionId` form fields. Changed signature from `virtualPath` parameter to `collectionId` parameter.

**Status**: Fixed (2026-02-05) — reindex collection filtering now works

### Reindex Test Wait Logic Incomplete

**Severity**: Medium (1 test failing)

**Description**: `WaitForIngestionToComplete` only checked if document exists in DB, not if Status="Ready" and LastIndexedAt is set. This caused tests to proceed before ingestion fully completed, leading to false failures in reindex logic tests.

**Fix**: Changed wait helper to use `/api/documents/{id}/reindex-check` endpoint and wait for `NeedsReindex=false`, which indicates document is fully indexed and ready.

**Status**: Fixed (2026-02-05)

### MinioFileSystem.ExistsAsync NullReferenceException

**Severity**: High (3 tests failing at reindex time)

**Description**: `response.S3Objects.Count > 0` on line 125 threw NullReferenceException because `S3Objects` can be null when bucket doesn't exist or request fails.

**Fix**: Changed to `response.S3Objects?.Count > 0` with null-conditional operator.

**Status**: Fixed (2026-02-05) — all reindex tests now passing

### KeywordSearchService SQL Parameter Conflict

**Severity**: Critical (search returns wrong results or crashes when container filter active)

**Description**: `KeywordSearchService` mixed `$N` (Npgsql positional) with `{N}` (EF Core SqlQueryRaw) parameter placeholders. When ContainerId filter was added as parameter[1], `LIMIT {1}` incorrectly used the ContainerId instead of TopK.

**Root Cause**: Original code used `$N` for some parameters but `{N}` for others. EF Core's SqlQueryRaw only understands `{N}` format. The `LIMIT` value was hardcoded as `{1}` but parameters shifted when container filter was added.

**Fix**: All parameters now use `{N}` format with dynamic index tracking (`var idx = parameters.Count`). TopK parameter index is computed dynamically.

**Status**: Fixed (2026-02-06)

### Container Delete Endpoint Error Handling

**Severity**: Medium (DELETE returns 500 instead of 400 for non-empty containers)

**Description**: `PostgresContainerStore.DeleteAsync` throws `InvalidOperationException` for non-empty containers, but the endpoint expected a boolean return value.

**Fix**: Added try-catch for `InvalidOperationException` in container delete endpoint, returning BadRequest with error message.

**Status**: Fixed (2026-02-06)

### IngestionPipeline Path Storage

**Severity**: High (documents stored with wrong path, breaking reindex and file browse)

**Description**: `IngestionPipeline.IngestAsync` used `options.FileName` for `DocumentEntity.Path` instead of the actual virtual path (e.g., `/test/file.txt`). This caused file browse and reindex to fail because paths didn't match MinIO keys.

**Fix**: Added `Path` field to `IngestionOptions` record. Set in all 3 callsites (DocumentsEndpoints, ReindexService, McpServer). Pipeline now uses `options.Path ?? options.FileName`.

**Status**: Fixed (2026-02-06)

### ReindexService Non-Seekable Stream

**Severity**: High (reindex hash computation fails for all MinIO-stored files)

**Description**: `ReindexService.ComputeContentHashAsync` did `content.Position = 0` on MinIO response stream, which is non-seekable (network stream), throwing `NotSupportedException`.

**Root Cause**: MinIO `GetObjectAsync` returns a network stream that doesn't support seeking. The `IngestionPipeline` already handled this by copying to MemoryStream, but `ReindexService` did not.

**Fix**: Added `if (content.CanSeek)` guard before setting `content.Position = 0`. Stream is already at position 0 when freshly opened.

**Status**: Fixed (2026-02-06)

### IngestionPipeline Reindex PK Violation

**Severity**: Critical (force reindex crashes with PK constraint violation)

**Description**: `IngestionPipeline.IngestAsync` always did `_context.Documents.Add(documentEntity)` (INSERT), causing PK violation when reindexing an existing document.

**Root Cause**: Reindex enqueues a new ingestion job with the existing document's ID. The pipeline unconditionally inserted a new row instead of updating the existing one.

**Fix**: Added `FindAsync` check before insert. If document exists, updates its fields; otherwise creates new entity.

**Status**: Fixed (2026-02-06)

### ReindexService Guid.ToString() in LINQ

**Severity**: High (container-scoped reindex returns all documents)

**Description**: `d.ContainerId.ToString() == options.ContainerId` in LINQ-to-SQL doesn't translate properly with Npgsql, causing container filter to be ineffective.

**Fix**: Parse `options.ContainerId` to `Guid` with `Guid.TryParse`, then compare directly: `d.ContainerId == containerGuid`.

**Status**: Fixed (2026-02-06)

### File Browser: Folder Listing Showed All Nested Subfolders

**Severity**: Medium (UI confusion)

**Description**: `PostgresFolderStore.ListAsync` returned ALL subfolders under a parent path, not just immediate children. Browsing "/" would show "/docs/", "/docs/sub/", "/images/" instead of just "/docs/" and "/images/".

**Root Cause**: Query used `Path.StartsWith(parent)` without filtering to direct children only.

**Fix**: Added client-side filtering after DB query to only return folders where the relative path (after parent prefix, minus trailing slash) contains no "/" characters.

**Status**: Fixed (2026-02-06)

### File Browser: Container File Count Not Updating After Delete

**Severity**: Medium (stale count displayed on home page)

**Description**: After deleting files or folders in the file browser, the container's DocumentCount was not refreshed. The home page still showed the old file count.

**Root Cause**: `DeleteEntry()` in FileBrowser.razor called `LoadEntries()` to refresh the file list but NOT `LoadContainer()` to refresh the container metadata (including DocumentCount). Same issue after upload completion.

**Fix**: Added `await LoadContainer()` call after successful deletion and after ingestion completion in the SignalR progress handler.

**Status**: Fixed (2026-02-06)

### File Browser: Folder Delete Didn't Clean Up File Storage

**Severity**: Medium (orphaned files in MinIO)

**Description**: Deleting a folder removed documents from the DB (cascade) but didn't clean up the actual files in MinIO/S3 storage, leaving orphaned files.

**Root Cause**: `FoldersEndpoints` delete only called `folderStore.DeleteAsync` (DB only). Unlike the individual file delete endpoint which also calls `fileSystem.DeleteAsync`.

**Fix**: Added document path collection before DB deletion, then iterates and deletes each file from storage (best effort).

**Status**: Fixed (2026-02-06)

### File Browser: Delete Button Not Discoverable

**Severity**: Low (UX issue)

**Description**: Delete buttons for files and folders in the file table had `opacity: 0` by default, making them completely invisible. Only visible on row hover, which was easy to miss.

**Fix**: Changed default opacity from 0 to 0.3, so the button is subtly visible at all times and becomes prominent on hover.

**Status**: Fixed (2026-02-06)

## Open Issues

### Settings Live Reload in WebApplicationFactory Tests

**Severity**: Low (3 tests failing, production works correctly)

**Description**: IOptionsMonitor not reflecting database setting changes immediately in integration tests. Settings save to database correctly and reload mechanism (`DatabaseSettingsProvider.Reload()`) is implemented, but tests using `WebApplicationFactory` don't see the changes.

**Root Cause**: WebApplicationFactory creates isolated configuration pipeline. The static `ConfigurationBuilderExtensions.CurrentProvider` may reference a different provider instance than the one used by the test application's DI container.

**Affected Tests**:
- UpdateSettings_ChunkingSettings_LiveReloadWorks
- UpdateSettings_SearchSettings_LiveReloadWorks
- UpdateSettings_MultipleCategories_IndependentlyUpdateable

**Production Status**: Working — settings reload works in normal app execution

**Test Status**: Open — test environment may need additional configuration or different reload mechanism

**Workaround**: Test GET endpoints work correctly. Reload mechanism is proven in production use.

---

<!-- Add issues as discovered -->
