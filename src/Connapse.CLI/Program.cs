using System.Net.Http.Headers;
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

if (args.Length == 0)
{
    Console.WriteLine("Connapse Platform CLI");
    Console.WriteLine();
    Console.WriteLine("Usage: aikp <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  ingest <path> [--collection <id>] [--strategy <name>] [--destination <path>]");
    Console.WriteLine("      Add file or folder to knowledge base");
    Console.WriteLine();
    Console.WriteLine("  search \"<query>\" [--mode <mode>] [--top <n>] [--collection <id>]");
    Console.WriteLine("      Search knowledge base");
    Console.WriteLine();
    Console.WriteLine("  reindex [--collection <id>] [--force] [--no-detect-changes]");
    Console.WriteLine("      Trigger reindexing of documents");
    Console.WriteLine("      --force                Skip content-hash comparison, reindex all");
    Console.WriteLine("      --no-detect-changes    Don't detect chunking/embedding settings changes");
    return 0;
}

var command = args[0].ToLower();

try
{
    return command switch
    {
        "ingest" => await HandleIngest(args, httpClient),
        "search" => await HandleSearch(args, httpClient),
        "reindex" => await HandleReindex(args, httpClient),
        _ => Error($"Unknown command '{command}'")
    };
}
catch (Exception ex)
{
    return Error(ex.Message);
}

static async Task<int> HandleIngest(string[] args, HttpClient httpClient)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: aikp ingest <path> [--collection <id>] [--strategy <name>] [--destination <path>]");
        return 1;
    }

    var path = args[1];
    var collection = GetOption(args, "--collection");
    var strategy = GetOption(args, "--strategy") ?? "Semantic";
    var destination = GetOption(args, "--destination") ?? "uploads";

    if (!File.Exists(path) && !Directory.Exists(path))
    {
        return Error($"Path '{path}' does not exist");
    }

    var files = new List<string>();
    if (File.Exists(path))
        files.Add(path);
    else
        files.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories));

    Console.WriteLine($"Found {files.Count} file(s) to ingest");
    Console.WriteLine();

    var successful = 0;
    var failed = 0;

    foreach (var file in files)
    {
        Console.Write($"Uploading {Path.GetFileName(file)}... ");

        try
        {
            using var content = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(file);
            using var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            content.Add(streamContent, "files", Path.GetFileName(file));
            content.Add(new StringContent(destination), "destinationPath");
            content.Add(new StringContent(strategy), "strategy");
            if (!string.IsNullOrWhiteSpace(collection))
                content.Add(new StringContent(collection), "collectionId");

            var response = await httpClient.PostAsync("/api/documents", content);
            response.EnsureSuccessStatusCode();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Uploaded");
            Console.ResetColor();
            successful++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"✗ Failed: {ex.Message}");
            Console.ResetColor();
            failed++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Summary: {successful} successful, {failed} failed");
    Console.WriteLine();
    Console.WriteLine("Files are being processed in the background. Use the web UI to monitor progress.");
    return 0;
}

static async Task<int> HandleSearch(string[] args, HttpClient httpClient)
{
    if (args.Length < 2)
    {
        Console.WriteLine("Usage: aikp search \"<query>\" [--mode <mode>] [--top <n>] [--collection <id>]");
        return 1;
    }

    var query = args[1];
    var mode = GetOption(args, "--mode") ?? "Hybrid";
    var topK = int.Parse(GetOption(args, "--top") ?? "10");
    var collection = GetOption(args, "--collection");

    Console.WriteLine($"Searching for: \"{query}\"");
    Console.WriteLine($"Mode: {mode} | Top: {topK}");
    if (!string.IsNullOrWhiteSpace(collection))
        Console.WriteLine($"Collection: {collection}");
    Console.WriteLine();

    var url = $"/api/search?q={Uri.EscapeDataString(query)}&mode={mode}&topK={topK}";
    if (!string.IsNullOrWhiteSpace(collection))
        url += $"&collectionId={Uri.EscapeDataString(collection)}";

    var response = await httpClient.GetAsync(url);
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var result = JsonSerializer.Deserialize<SearchResult>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (result == null || result.Hits.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No results found");
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
        Console.WriteLine($"[{i + 1}] Score: {hit.Score:F3}");
        Console.ResetColor();

        if (hit.Metadata.ContainsKey("source"))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"    Source: {hit.Metadata["source"]} ");
            Console.ResetColor();
        }

        if (hit.Metadata.ContainsKey("FileName"))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"| File: {hit.Metadata["FileName"]} ");
            Console.ResetColor();
        }

        if (hit.Metadata.ContainsKey("ChunkIndex"))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"| Chunk: {hit.Metadata["ChunkIndex"]}");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine();
        }

        var content = hit.Content;
        if (content.Length > 300)
            content = content.Substring(0, 297) + "...";

        Console.WriteLine($"    {content}");
        Console.WriteLine();
    }

    return 0;
}

static async Task<int> HandleReindex(string[] args, HttpClient httpClient)
{
    var collection = GetOption(args, "--collection");
    var force = HasFlag(args, "--force");
    var detectChanges = !HasFlag(args, "--no-detect-changes");

    Console.WriteLine("Triggering reindex...");
    if (!string.IsNullOrWhiteSpace(collection))
        Console.WriteLine($"Collection: {collection}");
    if (force)
        Console.WriteLine("Mode: Force (ignoring content hashes)");
    if (!detectChanges)
        Console.WriteLine("Settings change detection: disabled");
    Console.WriteLine();

    var request = new { collectionId = collection, force, detectSettingsChanges = detectChanges };
    var json = JsonSerializer.Serialize(request);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("/api/documents/reindex", content);
    response.EnsureSuccessStatusCode();

    var resultJson = await response.Content.ReadAsStringAsync();
    var result = JsonSerializer.Deserialize<ReindexResult>(resultJson, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (result != null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✓ Reindex complete");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"Batch ID: {result.BatchId}");
        Console.WriteLine($"Total documents evaluated: {result.TotalDocuments}");
        Console.WriteLine($"Enqueued for reindexing: {result.EnqueuedCount}");
        Console.WriteLine($"Skipped (unchanged): {result.SkippedCount}");
        Console.WriteLine($"Failed: {result.FailedCount}");

        if (result.ReasonCounts != null && result.ReasonCounts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Breakdown by reason:");
            foreach (var kvp in result.ReasonCounts)
            {
                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        Console.WriteLine();
        if (result.EnqueuedCount > 0)
            Console.WriteLine("Reindexing is happening in the background. Use the web UI to monitor progress.");
        else
            Console.WriteLine("No documents needed reindexing.");
    }

    return 0;
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
