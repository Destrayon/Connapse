# MCP Bulk Operations Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `bulk_delete` and `bulk_upload` MCP tools for batch file operations within a single container.

**Architecture:** Thin MCP tool wrappers that loop over existing service calls. No new interfaces or store methods. JSON string parameters deserialized internally. Max 100 items per call, continue-on-failure with per-item results.

**Tech Stack:** C#, MCP SDK (`[McpServerTool]`), NSubstitute + FluentAssertions for tests, System.Text.Json for parameter deserialization.

---

### Task 1: Add `bulk_delete` tests

**Files:**
- Create: `tests/Connapse.Core.Tests/Mcp/McpToolsBulkDeleteTests.cs`

**Step 1: Write the test file**

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsBulkDeleteTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IKnowledgeFileSystem _fileSystem;
    private readonly ILogger<McpTools> _logger;
    private readonly IServiceProvider _services;

    public McpToolsBulkDeleteTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _fileSystem = Substitute.For<IKnowledgeFileSystem>();
        _logger = Substitute.For<ILogger<McpTools>>();

        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(MakeContainer());

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IKnowledgeFileSystem)).Returns(_fileSystem);
        services.GetService(typeof(ILogger<McpTools>)).Returns(_logger);
        _services = services;
    }

    [Fact]
    public async Task BulkDelete_AllSucceed_ReturnsSummary()
    {
        var doc1 = MakeDocument("file-1", "a.txt", "/a.txt");
        var doc2 = MakeDocument("file-2", "b.txt", "/b.txt");
        var doc3 = MakeDocument("file-3", "c.txt", "/c.txt");

        _documentStore.GetAsync("file-1", Arg.Any<CancellationToken>()).Returns(doc1);
        _documentStore.GetAsync("file-2", Arg.Any<CancellationToken>()).Returns(doc2);
        _documentStore.GetAsync("file-3", Arg.Any<CancellationToken>()).Returns(doc3);

        var json = """["file-1","file-2","file-3"]""";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().Contain("Deleted 3 of 3");
        await _documentStore.Received(3).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkDelete_PartialFailure_ReportsEachResult()
    {
        var doc1 = MakeDocument("file-1", "a.txt", "/a.txt");
        _documentStore.GetAsync("file-1", Arg.Any<CancellationToken>()).Returns(doc1);
        _documentStore.GetAsync("file-2", Arg.Any<CancellationToken>()).Returns((Document?)null);

        var json = """["file-1","file-2"]""";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().Contain("Deleted 1 of 2");
        result.Should().Contain("file-2");
    }

    [Fact]
    public async Task BulkDelete_StorageCleanupFails_StillReportsSuccess()
    {
        var doc1 = MakeDocument("file-1", "a.txt", "/a.txt");
        _documentStore.GetAsync("file-1", Arg.Any<CancellationToken>()).Returns(doc1);
        _fileSystem
            .DeleteAsync("/a.txt", Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk error"));

        var json = """["file-1"]""";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().Contain("Deleted 1 of 1");
        result.Should().Contain("storage warning");
    }

    [Fact]
    public async Task BulkDelete_ContainerNotFound_ReturnsError()
    {
        var json = """["file-1"]""";
        var result = await McpTools.BulkDelete(_services, "nonexistent", json);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task BulkDelete_ExceedsLimit_ReturnsError()
    {
        var ids = Enumerable.Range(0, 101).Select(i => $"\"file-{i}\"");
        var json = $"[{string.Join(",", ids)}]";
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), json);

        result.Should().StartWith("Error:");
        result.Should().Contain("100");
    }

    [Fact]
    public async Task BulkDelete_EmptyArray_ReturnsError()
    {
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), "[]");

        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BulkDelete_InvalidJson_ReturnsError()
    {
        var result = await McpTools.BulkDelete(_services, ContainerId.ToString(), "not json");

        result.Should().StartWith("Error:");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.MinIO,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);

    private static Document MakeDocument(string id, string fileName, string path) => new(
        Id: id,
        ContainerId: ContainerId.ToString(),
        FileName: fileName,
        ContentType: "text/plain",
        Path: path,
        SizeBytes: 100,
        CreatedAt: DateTime.UtcNow,
        Metadata: new());
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~McpToolsBulkDelete" --no-restore -v minimal`
Expected: FAIL — `BulkDelete` method does not exist

**Step 3: Commit**

```bash
git add tests/Connapse.Core.Tests/Mcp/McpToolsBulkDeleteTests.cs
git commit -m "test: add bulk_delete MCP tool tests (red)"
```

---

### Task 2: Implement `bulk_delete` tool

**Files:**
- Modify: `src/Connapse.Web/Mcp/McpTools.cs`

**Step 1: Add the `BulkDelete` method to `McpTools` class**

Add this method after the existing `DeleteFile` method (after line 355), along with adding `using System.Text.Json;` to the top of the file if not already present:

```csharp
[McpServerTool(Name = "bulk_delete"),
 Description("Delete multiple files from a container in one operation. Returns per-file results.")]
public static async Task<string> BulkDelete(
    IServiceProvider services,
    [Description("Container ID or name")] string containerId,
    [Description("JSON array of file (document) IDs to delete, e.g. [\"id1\",\"id2\"]. Max 100.")] string fileIds,
    CancellationToken ct = default)
{
    List<string> ids;
    try
    {
        ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(fileIds) ?? [];
    }
    catch
    {
        return "Error: 'fileIds' must be a valid JSON array of strings.";
    }

    if (ids.Count == 0)
        return "Error: 'fileIds' array must not be empty.";

    if (ids.Count > 100)
        return "Error: Maximum 100 files per bulk_delete call.";

    var containerStore = services.GetRequiredService<IContainerStore>();
    var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
    if (resolvedId is null)
        return $"Error: Container '{containerId}' not found.";

    var documentStore = services.GetRequiredService<IDocumentStore>();
    var fileSystem = services.GetRequiredService<IKnowledgeFileSystem>();
    var logger = services.GetRequiredService<ILogger<McpTools>>();

    var succeeded = 0;
    var failures = new List<string>();

    foreach (var fileId in ids)
    {
        var document = await documentStore.GetAsync(fileId, ct);
        if (document is null || document.ContainerId != resolvedId.Value.ToString())
        {
            failures.Add($"{fileId}: not found");
            continue;
        }

        await documentStore.DeleteAsync(fileId, ct);
        succeeded++;

        try
        {
            if (!string.IsNullOrEmpty(document.Path))
                await fileSystem.DeleteAsync(document.Path, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete backing file {Path}", document.Path);
            failures.Add($"{fileId} ({document.FileName}): deleted but storage warning");
        }
    }

    var summary = $"Deleted {succeeded} of {ids.Count} file(s).";
    if (failures.Count > 0)
        summary += $"\n\nFailures:\n{string.Join("\n", failures.Select(f => $"- {f}"))}";

    return summary;
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~McpToolsBulkDelete" --no-restore -v minimal`
Expected: All 7 tests PASS

**Step 3: Commit**

```bash
git add src/Connapse.Web/Mcp/McpTools.cs
git commit -m "feat: add bulk_delete MCP tool"
```

---

### Task 3: Add `bulk_upload` tests

**Files:**
- Create: `tests/Connapse.Core.Tests/Mcp/McpToolsBulkUploadTests.cs`

**Step 1: Write the test file**

```csharp
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Web.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Connapse.Core.Tests.Mcp;

[Trait("Category", "Unit")]
public class McpToolsBulkUploadTests
{
    private static readonly Guid ContainerId = Guid.NewGuid();

    private readonly IContainerStore _containerStore;
    private readonly IDocumentStore _documentStore;
    private readonly IIngestionQueue _ingestionQueue;
    private readonly IConnectorFactory _connectorFactory;
    private readonly IConnector _connector;
    private readonly IFolderStore _folderStore;
    private readonly IServiceProvider _services;

    public McpToolsBulkUploadTests()
    {
        _containerStore = Substitute.For<IContainerStore>();
        _documentStore = Substitute.For<IDocumentStore>();
        _ingestionQueue = Substitute.For<IIngestionQueue>();
        _connectorFactory = Substitute.For<IConnectorFactory>();
        _connector = Substitute.For<IConnector>();
        _folderStore = Substitute.For<IFolderStore>();

        var container = MakeContainer();
        _containerStore
            .GetAsync(ContainerId, Arg.Any<CancellationToken>())
            .Returns(container);

        _connectorFactory.Create(Arg.Any<Container>()).Returns(_connector);

        _folderStore
            .ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContainerStore)).Returns(_containerStore);
        services.GetService(typeof(IDocumentStore)).Returns(_documentStore);
        services.GetService(typeof(IIngestionQueue)).Returns(_ingestionQueue);
        services.GetService(typeof(IConnectorFactory)).Returns(_connectorFactory);
        services.GetService(typeof(IFolderStore)).Returns(_folderStore);
        _services = services;
    }

    [Fact]
    public async Task BulkUpload_MultipleTextFiles_AllSucceed()
    {
        var json = """
        [
            {"filename":"a.txt","content":"hello"},
            {"filename":"b.txt","content":"world"}
        ]
        """;
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 2 of 2");
        await _ingestionQueue.Received(2).EnqueueAsync(
            Arg.Is<IngestionJob>(j => j.BatchId != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkUpload_Base64File_DecodesAndUploads()
    {
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("test content"));
        var json = $"""[{{"filename":"test.bin","content":"{b64}","encoding":"base64"}}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 1");
    }

    [Fact]
    public async Task BulkUpload_InvalidBase64_ReportsPerItemError()
    {
        var json = """
        [
            {"filename":"good.txt","content":"hello"},
            {"filename":"bad.bin","content":"!!!not-base64!!!","encoding":"base64"}
        ]
        """;
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 2");
        result.Should().Contain("bad.bin");
    }

    [Fact]
    public async Task BulkUpload_SharedBatchId()
    {
        var json = """[{"filename":"a.txt","content":"x"},{"filename":"b.txt","content":"y"}]""";
        await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        var jobs = _ingestionQueue.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "EnqueueAsync")
            .Select(c => (IngestionJob)c.GetArguments()[0]!)
            .ToList();

        jobs.Should().HaveCount(2);
        jobs[0].BatchId.Should().NotBeNullOrEmpty();
        jobs[0].BatchId.Should().Be(jobs[1].BatchId);
    }

    [Fact]
    public async Task BulkUpload_WithFolderPath_CreatesCorrectPath()
    {
        var json = """[{"filename":"doc.md","content":"# Hello","folderPath":"/notes/"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 1 of 1");
        await _connector.Received(1).WriteFileAsync(
            Arg.Is<string>(p => p.Contains("notes") && p.Contains("doc.md")),
            Arg.Any<Stream>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkUpload_ContainerNotFound_ReturnsError()
    {
        var json = """[{"filename":"a.txt","content":"x"}]""";
        var result = await McpTools.BulkUpload(_services, "nonexistent", json);

        result.Should().StartWith("Error:");
        result.Should().Contain("not found");
    }

    [Fact]
    public async Task BulkUpload_ExceedsLimit_ReturnsError()
    {
        var items = Enumerable.Range(0, 101)
            .Select(i => $"{{\"filename\":\"f{i}.txt\",\"content\":\"x\"}}");
        var json = $"[{string.Join(",", items)}]";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().StartWith("Error:");
        result.Should().Contain("100");
    }

    [Fact]
    public async Task BulkUpload_EmptyArray_ReturnsError()
    {
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), "[]");

        result.Should().StartWith("Error:");
    }

    [Fact]
    public async Task BulkUpload_MissingFilename_ReportsPerItemError()
    {
        var json = """[{"content":"hello"}]""";
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), json);

        result.Should().Contain("Uploaded 0 of 1");
        result.Should().Contain("filename");
    }

    [Fact]
    public async Task BulkUpload_InvalidJson_ReturnsError()
    {
        var result = await McpTools.BulkUpload(_services, ContainerId.ToString(), "not json");

        result.Should().StartWith("Error:");
    }

    private static Container MakeContainer() => new(
        Id: ContainerId.ToString(),
        Name: "test",
        Description: null,
        ConnectorType: ConnectorType.MinIO,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~McpToolsBulkUpload" --no-restore -v minimal`
Expected: FAIL — `BulkUpload` method does not exist

**Step 3: Commit**

```bash
git add tests/Connapse.Core.Tests/Mcp/McpToolsBulkUploadTests.cs
git commit -m "test: add bulk_upload MCP tool tests (red)"
```

---

### Task 4: Implement `bulk_upload` tool

**Files:**
- Modify: `src/Connapse.Web/Mcp/McpTools.cs`

**Step 1: Add the `BulkUploadFileItem` record and `BulkUpload` method**

Add the record inside the `Connapse.Web.Mcp` namespace (before or after the `McpTools` class), and add the method after `BulkDelete` in the `McpTools` class:

```csharp
// Add this record inside the namespace, outside the McpTools class
internal record BulkUploadFileItem(
    string? Filename { get; init; },
    string? Content { get; init; },
    string? Encoding { get; init; },
    string? FolderPath { get; init; });
```

```csharp
// Add this method inside the McpTools class
[McpServerTool(Name = "bulk_upload"),
 Description("Upload multiple files to a container in one operation. Each file is parsed, chunked, embedded, and made searchable. Returns per-file results.")]
public static async Task<string> BulkUpload(
    IServiceProvider services,
    [Description("Container ID or name")] string containerId,
    [Description("JSON array of file objects. Each object: {\"filename\":\"name.txt\", \"content\":\"...\", \"encoding\":\"text|base64\", \"folderPath\":\"/optional/\"}. Max 100.")] string files,
    CancellationToken ct = default)
{
    List<BulkUploadFileItem> items;
    try
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        items = System.Text.Json.JsonSerializer.Deserialize<List<BulkUploadFileItem>>(files, jsonOptions) ?? [];
    }
    catch
    {
        return "Error: 'files' must be a valid JSON array of file objects.";
    }

    if (items.Count == 0)
        return "Error: 'files' array must not be empty.";

    if (items.Count > 100)
        return "Error: Maximum 100 files per bulk_upload call.";

    var containerStore = services.GetRequiredService<IContainerStore>();
    var resolvedId = await ResolveContainerIdAsync(containerId, containerStore, ct);
    if (resolvedId is null)
        return $"Error: Container '{containerId}' not found.";

    var container = await containerStore.GetAsync(resolvedId.Value, ct);
    var connectorFactory = services.GetRequiredService<IConnectorFactory>();
    var connector = connectorFactory.Create(container!);
    var folderStore = services.GetRequiredService<IFolderStore>();
    var ingestionQueue = services.GetRequiredService<IIngestionQueue>();

    var batchId = Guid.NewGuid().ToString();
    var succeeded = 0;
    var failures = new List<string>();

    try
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var itemLabel = item.Filename ?? $"item[{i}]";

            if (string.IsNullOrWhiteSpace(item.Filename))
            {
                failures.Add($"{itemLabel}: missing 'filename'");
                continue;
            }

            if (string.IsNullOrEmpty(item.Content))
            {
                failures.Add($"{itemLabel}: missing 'content'");
                continue;
            }

            byte[] fileBytes;
            var isBase64 = string.Equals(item.Encoding, "base64", StringComparison.OrdinalIgnoreCase);
            if (isBase64)
            {
                try
                {
                    fileBytes = Convert.FromBase64String(item.Content);
                }
                catch
                {
                    failures.Add($"{itemLabel}: invalid base64 content");
                    continue;
                }
            }
            else
            {
                fileBytes = System.Text.Encoding.UTF8.GetBytes(item.Content);
            }

            var destinationPath = item.FolderPath ?? "/";
            var normalizedDest = PathUtilities.NormalizeFolderPath(destinationPath);
            var filePath = PathUtilities.NormalizePath($"{normalizedDest}{item.Filename}");

            try
            {
                using var stream = new MemoryStream(fileBytes);
                await connector.WriteFileAsync(filePath.TrimStart('/'), stream, ct: ct);

                await EnsureIntermediateFoldersAsync(folderStore, resolvedId.Value, normalizedDest, ct);

                var documentId = Guid.NewGuid().ToString();
                var jobId = Guid.NewGuid().ToString();

                var job = new IngestionJob(
                    JobId: jobId,
                    DocumentId: documentId,
                    Path: filePath,
                    Options: new IngestionOptions(
                        DocumentId: documentId,
                        FileName: item.Filename,
                        ContentType: null,
                        ContainerId: resolvedId.Value.ToString(),
                        Path: filePath,
                        Strategy: ChunkingStrategy.Semantic,
                        Metadata: new Dictionary<string, string>
                        {
                            ["OriginalFileName"] = item.Filename,
                            ["IngestedVia"] = "MCP",
                            ["IngestedAt"] = DateTime.UtcNow.ToString("O")
                        }),
                    BatchId: batchId);

                await ingestionQueue.EnqueueAsync(job, ct);
                succeeded++;
            }
            catch (Exception ex)
            {
                failures.Add($"{itemLabel}: {ex.Message}");
            }
        }
    }
    finally
    {
        (connector as IDisposable)?.Dispose();
    }

    var summary = $"Uploaded {succeeded} of {items.Count} file(s) to container '{container!.Name}'.";
    if (failures.Count > 0)
        summary += $"\n\nFailures:\n{string.Join("\n", failures.Select(f => $"- {f}"))}";
    else
        summary += "\n\nAll files queued for ingestion (parsing, chunking, embedding).";

    return summary;
}
```

**Step 2: Run all tests to verify they pass**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~McpToolsBulk" --no-restore -v minimal`
Expected: All 17 tests PASS (7 bulk_delete + 10 bulk_upload)

**Step 3: Commit**

```bash
git add src/Connapse.Web/Mcp/McpTools.cs
git commit -m "feat: add bulk_upload MCP tool"
```

---

### Task 5: Run full test suite and verify build

**Step 1: Build the solution**

Run: `dotnet build Connapse.sln --no-restore -v minimal`
Expected: Build succeeded, 0 errors

**Step 2: Run full test suite**

Run: `dotnet test Connapse.sln --no-restore -v minimal`
Expected: All tests pass (existing + 17 new)

**Step 3: Final commit (if any fixups needed)**

```bash
git add -A
git commit -m "chore: fixups from full test run"
```

---

### Notes for implementer

- **`BulkUploadFileItem` record:** Uses `init` properties with `JsonPropertyNameCaseInsensitive = true` so JSON keys like `filename`, `Filename`, `FILENAME` all work.
- **`IConnector.WriteFileAsync` signature:** Check the exact parameter order — it's `(string path, Stream content, string? contentType = null, CancellationToken ct = default)`. The `ct:` named argument is used because `contentType` is skipped.
- **`ResolveContainerIdAsync`:** Already exists as a private helper in `McpTools` — reuse it directly.
- **`EnsureIntermediateFoldersAsync`:** Already exists as an `internal static` method in `McpTools` — reuse it directly.
- **No `using System.Text.Json;`** needed if you fully qualify `System.Text.Json.JsonSerializer` and `System.Text.Json.JsonSerializerOptions` (which the plan does).
- **`System.Text.Encoding`** is already available via `using System.Text;` at the top of McpTools.cs.
