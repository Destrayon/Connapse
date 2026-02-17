using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

// Load configuration
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// Get API base URL from config or use default
var apiBaseUrl = configuration["ApiBaseUrl"] ?? "https://localhost:5001";

// Create HttpClient with SSL bypass for localhost
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
};
var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromMinutes(10)
};

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLower();

try
{
    return command switch
    {
        "container" => await HandleContainer(args, httpClient, jsonOptions),
        "upload" => await HandleUpload(args, httpClient, jsonOptions),
        "search" => await HandleSearch(args, httpClient, jsonOptions),
        "reindex" => await HandleReindex(args, httpClient, jsonOptions),
        // Legacy aliases
        "ingest" => await HandleUpload(args, httpClient, jsonOptions),
        _ => Error($"Unknown command '{command}'")
    };
}
catch (Exception ex)
{
    return Error(ex.Message);
}

static void PrintUsage()
{
    Console.WriteLine("Connapse Platform CLI");
    Console.WriteLine();
    Console.WriteLine("Usage: aikp <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  container create <name> [--description \"...\"]");
    Console.WriteLine("      Create a new container");
    Console.WriteLine();
    Console.WriteLine("  container list");
    Console.WriteLine("      List all containers");
    Console.WriteLine();
    Console.WriteLine("  container delete <name>");
    Console.WriteLine("      Delete an empty container");
    Console.WriteLine();
    Console.WriteLine("  upload <path> --container <name> [--strategy <name>] [--destination <path>]");
    Console.WriteLine("      Upload file(s) to a container");
    Console.WriteLine();
    Console.WriteLine("  search \"<query>\" --container <name> [--mode <mode>] [--top <n>] [--path <folder>] [--min-score <0.0-1.0>]");
    Console.WriteLine("      Search within a container");
    Console.WriteLine();
    Console.WriteLine("  reindex --container <name> [--force] [--no-detect-changes]");
    Console.WriteLine("      Reindex documents in a container");
}

static async Task<int> HandleContainer(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  aikp container create <name> [--description \"...\"]");
        Console.WriteLine("  aikp container list");
        Console.WriteLine("  aikp container delete <name>");
        return 1;
    }

    var subCommand = args[1].ToLower();

    return subCommand switch
    {
        "create" => await ContainerCreate(args, httpClient, jsonOptions),
        "list" => await ContainerList(httpClient, jsonOptions),
        "delete" => await ContainerDelete(args, httpClient, jsonOptions),
        _ => Error($"Unknown container subcommand '{subCommand}'")
    };
}

static async Task<int> ContainerCreate(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: aikp container create <name> [--description \"...\"]");
        return 1;
    }

    var name = args[2];
    var description = GetOption(args, "--description");

    Console.Write($"Creating container '{name}'... ");

    var request = new { name, description };
    var json = JsonSerializer.Serialize(request);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("/api/containers", content);

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        var error = TryParseError(errorBody, jsonOptions);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed: {error}");
        Console.ResetColor();
        return 1;
    }

    var result = await response.Content.ReadFromJsonAsync<ContainerInfo>(jsonOptions);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Created");
    Console.ResetColor();
    Console.WriteLine($"  ID: {result?.Id}");
    Console.WriteLine($"  Name: {result?.Name}");
    return 0;
}

static async Task<int> ContainerList(HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    var response = await httpClient.GetAsync("/api/containers");
    response.EnsureSuccessStatusCode();

    var containers = await response.Content.ReadFromJsonAsync<List<ContainerInfo>>(jsonOptions);

    if (containers is null || containers.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No containers found.");
        Console.ResetColor();
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Found {containers.Count} container(s)");
    Console.ResetColor();
    Console.WriteLine();

    foreach (var c in containers)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  {c.Name}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  ({c.DocumentCount} files)");
        if (!string.IsNullOrEmpty(c.Description))
            Console.Write($"  — {c.Description}");
        Console.WriteLine();
        Console.ResetColor();
    }

    return 0;
}

static async Task<int> ContainerDelete(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: aikp container delete <name>");
        return 1;
    }

    var name = args[2];
    var containerId = await ResolveContainerId(name, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{name}' not found.");

    Console.Write($"Deleting container '{name}'... ");

    var response = await httpClient.DeleteAsync($"/api/containers/{containerId}");

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        var error = TryParseError(errorBody, jsonOptions);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed: {error}");
        Console.ResetColor();
        return 1;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Deleted");
    Console.ResetColor();
    return 0;
}

static async Task<int> HandleUpload(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: aikp upload <path> --container <name> [--strategy <name>] [--destination <path>]");
        return 1;
    }

    var path = args[1];
    var containerName = GetOption(args, "--container");
    var strategy = GetOption(args, "--strategy") ?? "Semantic";
    var destination = GetOption(args, "--destination") ?? "/";

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    var containerId = await ResolveContainerId(containerName, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{containerName}' not found.");

    if (!File.Exists(path) && !Directory.Exists(path))
        return Error($"Path '{path}' does not exist.");

    var files = new List<string>();
    if (File.Exists(path))
        files.Add(path);
    else
        files.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories));

    Console.WriteLine($"Uploading {files.Count} file(s) to container '{containerName}'");
    Console.WriteLine();

    var successful = 0;
    var failed = 0;

    foreach (var file in files)
    {
        Console.Write($"  {Path.GetFileName(file)}... ");

        try
        {
            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(file);
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            content.Add(streamContent, "files", Path.GetFileName(file));
            content.Add(new StringContent(destination), "path");
            content.Add(new StringContent(strategy), "strategy");

            var response = await httpClient.PostAsync($"/api/containers/{containerId}/files", content);
            response.EnsureSuccessStatusCode();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Uploaded");
            Console.ResetColor();
            successful++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed: {ex.Message}");
            Console.ResetColor();
            failed++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Summary: {successful} successful, {failed} failed");
    if (successful > 0)
        Console.WriteLine("Files are being processed in the background.");
    return 0;
}

static async Task<int> HandleSearch(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: aikp search \"<query>\" --container <name> [--mode <mode>] [--top <n>] [--path <folder>] [--min-score <0.0-1.0>]");
        return 1;
    }

    var query = args[1];
    var containerName = GetOption(args, "--container");
    var mode = GetOption(args, "--mode") ?? "Hybrid";
    var topK = int.Parse(GetOption(args, "--top") ?? "10");
    var folderPath = GetOption(args, "--path");
    var minScoreStr = GetOption(args, "--min-score");

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    var containerId = await ResolveContainerId(containerName, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{containerName}' not found.");

    Console.WriteLine($"Searching: \"{query}\"");
    Console.WriteLine($"Container: {containerName} | Mode: {mode} | Top: {topK}");
    if (!string.IsNullOrWhiteSpace(folderPath))
        Console.WriteLine($"Path filter: {folderPath}");
    if (!string.IsNullOrWhiteSpace(minScoreStr))
        Console.WriteLine($"Min score: {minScoreStr}");
    Console.WriteLine();

    var url = $"/api/containers/{containerId}/search?q={Uri.EscapeDataString(query)}&mode={mode}&topK={topK}";
    if (!string.IsNullOrWhiteSpace(folderPath))
        url += $"&path={Uri.EscapeDataString(folderPath)}";
    if (!string.IsNullOrWhiteSpace(minScoreStr))
        url += $"&minScore={minScoreStr}";

    var response = await httpClient.GetAsync(url);
    response.EnsureSuccessStatusCode();

    var result = await response.Content.ReadFromJsonAsync<SearchResult>(jsonOptions);

    if (result is null || result.Hits.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No results found.");
        Console.ResetColor();
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Found {result.TotalMatches} results in {result.Duration.TotalMilliseconds:F0}ms");
    Console.ResetColor();
    Console.WriteLine();

    for (int i = 0; i < result.Hits.Count; i++)
    {
        var hit = result.Hits[i];

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"[{i + 1}] Score: {hit.Score:F3}");
        Console.ResetColor();

        if (hit.Metadata.TryGetValue("FileName", out var fileName))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  | {fileName}");
            Console.ResetColor();
        }

        if (hit.Metadata.TryGetValue("ChunkIndex", out var chunkIdx))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($" (chunk {chunkIdx})");
            Console.ResetColor();
        }
        Console.WriteLine();

        var content = hit.Content;
        if (content.Length > 300)
            content = content[..297] + "...";
        Console.WriteLine($"    {content}");
        Console.WriteLine();
    }

    return 0;
}

static async Task<int> HandleReindex(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    var containerName = GetOption(args, "--container");
    var force = HasFlag(args, "--force");
    var detectChanges = !HasFlag(args, "--no-detect-changes");

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    var containerId = await ResolveContainerId(containerName, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{containerName}' not found.");

    Console.WriteLine($"Triggering reindex for container '{containerName}'...");
    if (force)
        Console.WriteLine("Mode: Force (ignoring content hashes)");
    if (!detectChanges)
        Console.WriteLine("Settings change detection: disabled");
    Console.WriteLine();

    var request = new { force, detectSettingsChanges = detectChanges };
    var json = JsonSerializer.Serialize(request);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync($"/api/containers/{containerId}/reindex", content);
    response.EnsureSuccessStatusCode();

    var result = await response.Content.ReadFromJsonAsync<ReindexResult>(jsonOptions);

    if (result is not null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Reindex complete");
        Console.ResetColor();
        Console.WriteLine($"  Total documents: {result.TotalDocuments}");
        Console.WriteLine($"  Enqueued: {result.EnqueuedCount}");
        Console.WriteLine($"  Skipped: {result.SkippedCount}");
        Console.WriteLine($"  Failed: {result.FailedCount}");

        if (result.ReasonCounts is { Count: > 0 })
        {
            Console.WriteLine("  Reasons:");
            foreach (var kvp in result.ReasonCounts)
                Console.WriteLine($"    {kvp.Key}: {kvp.Value}");
        }

        Console.WriteLine();
        if (result.EnqueuedCount > 0)
            Console.WriteLine("Reindexing is processing in the background.");
    }

    return 0;
}

// Resolve container name to ID via API
static async Task<string?> ResolveContainerId(string nameOrId, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    // If it looks like a GUID, use it directly
    if (Guid.TryParse(nameOrId, out _))
        return nameOrId;

    // Otherwise resolve by name
    var response = await httpClient.GetAsync("/api/containers");
    if (!response.IsSuccessStatusCode) return null;

    var containers = await response.Content.ReadFromJsonAsync<List<ContainerInfo>>(jsonOptions);
    var match = containers?.FirstOrDefault(c =>
        c.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));

    return match?.Id;
}

static string? GetOption(string[] args, string option)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static bool HasFlag(string[] args, string flag)
{
    return args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}

static int Error(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Error: {message}");
    Console.ResetColor();
    return 1;
}

static string TryParseError(string body, JsonSerializerOptions options)
{
    try
    {
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var errorProp))
            return errorProp.GetString() ?? body;
    }
    catch { }
    return body;
}

record ContainerInfo(string Id, string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt, int DocumentCount);
record SearchResult(List<SearchHit> Hits, int TotalMatches, TimeSpan Duration);
record SearchHit(string ChunkId, string DocumentId, string Content, float Score, Dictionary<string, string> Metadata);
record ReindexResult(
    string BatchId,
    int TotalDocuments,
    int EnqueuedCount,
    int SkippedCount,
    int FailedCount,
    Dictionary<string, int>? ReasonCounts,
    string Message);
