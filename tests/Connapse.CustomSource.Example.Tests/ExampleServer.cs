using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Connapse.CustomSource.Example.Tests;

/// <summary>Runs the example server in-process over a fresh data folder.</summary>
public sealed class ExampleServer : IDisposable
{
    public const string Token = "test-token-0123456789abcdef";
    public const string LinkSecret = "test-link-secret-0123456789abcdef0123456789";
    public const string Issuer = "https://connapse.test";
    public const string ProviderId = "provider-1";
    public const string AliceId = "connapse-alice";
    public const string BobId = "connapse-bob";

    private readonly WebApplicationFactory<Program> _factory;

    public ExampleServer(ExampleConfig? config = null, IReadOnlyDictionary<string, string>? files = null)
    {
        DataDirectory = Directory.CreateTempSubdirectory("connapse-example-").FullName;
        File.WriteAllText(
            Path.Combine(DataDirectory, "example.json"),
            JsonSerializer.Serialize(config ?? DefaultConfig(), ExampleConfig.JsonOptions));
        foreach ((string path, string content) in files ?? new Dictionary<string, string>())
            WriteFile(path, content);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("DataDirectory", DataDirectory));
    }

    public string DataDirectory { get; }

    public IServiceProvider Services => _factory.Services;

    public static ExampleConfig DefaultConfig() => new()
    {
        Name = "Test bridge",
        Token = Token,
        PageSize = 2,
        Scopes = [new ScopeConfig("eng", "Engineering", "Engineering docs"), new ScopeConfig("ops", "Operations", null)],
        Grants = new() { ["alice"] = ["folder:eng/", "folder:eng/design"], ["bob"] = ["folder:eng/", "folder:ops/"] },
        Denies = new() { ["alice"] = ["eng/design/secret.md"] },
        PresetLinks = new() { [AliceId] = "alice", [BobId] = "bob" },
        Users = ["alice", "bob"],
        LinkSecret = LinkSecret,
        ConnapseIssuer = Issuer,
        ProviderId = ProviderId,
    };

    /// <param name="relativePath">Path under <c>scopes/</c>, starting with the scope ID.</param>
    public void WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(DataDirectory, "scopes", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void DeleteFile(string relativePath) => File.Delete(Path.Combine(DataDirectory, "scopes", relativePath));

    public HttpClient Client(bool authorized = true)
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authorized)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(DataDirectory, recursive: true); }
        catch (IOException) { }
    }
}
