using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using NuGet.Versioning;

// Load configuration
var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

// Get API base URL from config or use default
var apiBaseUrl = configuration["ApiBaseUrl"] ?? "https://localhost:5001";

// Load stored credentials — override base URL if stored
var credentials = LoadCredentials();
if (credentials?.ApiBaseUrl is not null)
    apiBaseUrl = credentials.ApiBaseUrl;

// Create HttpClient with SSL bypass scoped to localhost only
var serverUri = new Uri(apiBaseUrl);
var isLocalhost = serverUri.Host is "localhost" or "127.0.0.1" or "::1";
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = isLocalhost
        ? (message, cert, chain, errors) => true
        : null
};
var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri(apiBaseUrl),
    Timeout = TimeSpan.FromMinutes(10)
};

// Inject stored API key into all requests
if (credentials?.ApiKey is not null)
    httpClient.DefaultRequestHeaders.Add("X-Api-Key", credentials.ApiKey);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    TypeInfoResolver = CliJsonContext.Default
};

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

var command = args[0].ToLower();

int exitCode;
try
{
    exitCode = command switch
    {
        "help" or "--help" or "-h" => Help(),
        "version" or "--version" => HandleVersion(),
        "update" or "--update"  => await HandleUpdate(args),
        "auth"    => await HandleAuth(args, httpClient, jsonOptions, apiBaseUrl),
        "container" => await HandleContainer(args, httpClient, jsonOptions),
        "files"   => await HandleFiles(args, httpClient, jsonOptions),
        "upload"  => await HandleUpload(args, httpClient, jsonOptions),
        "search"  => await HandleSearch(args, httpClient, jsonOptions),
        "reindex" => await HandleReindex(args, httpClient, jsonOptions),
        // Legacy aliases
        "ingest"  => await HandleUpload(args, httpClient, jsonOptions),
        _ => Error($"Unknown command '{command}'")
    };
}
catch (Exception ex)
{
    return Error(ex.Message);
}

// Passive update check — runs once per day, silent on failure
if (command is not ("version" or "update"))
    await CheckForUpdateNotification();

return exitCode;

static int Help() { PrintUsage(); return 0; }

static bool IsHelpFlag(string arg) => arg is "--help" or "-h";
static bool IsSubcommandHelp(string[] args) => args.Length >= 2 && IsHelpFlag(args[1]);
static bool NeedsSubcommandUsage(string[] args) => args.Length < 2 || IsHelpFlag(args[1]);

static void PrintUsage()
{
    Console.WriteLine($"Connapse Platform CLI v{GetCurrentVersion()}");
    Console.WriteLine();
    Console.WriteLine("Usage: connapse <command> [options]");
    Console.WriteLine();
    Console.WriteLine("General:");
    Console.WriteLine("  version");
    Console.WriteLine("      Show the installed version");
    Console.WriteLine();
    Console.WriteLine("  update [--check] [--pre]");
    Console.WriteLine("      Update to the latest release (--check to preview, --pre to include pre-releases)");
    Console.WriteLine();
    Console.WriteLine("Authentication:");
    Console.WriteLine("  auth login [--url <server-url>] [--no-browser]");
    Console.WriteLine("      Authenticate via browser (or --no-browser for email/password prompt)");
    Console.WriteLine();
    Console.WriteLine("  auth logout");
    Console.WriteLine("      Clear stored credentials");
    Console.WriteLine();
    Console.WriteLine("  auth whoami");
    Console.WriteLine("      Show current identity and server");
    Console.WriteLine();
    Console.WriteLine("  auth pat create <name> [--expires <yyyy-MM-dd>]");
    Console.WriteLine("      Create a new personal access token");
    Console.WriteLine();
    Console.WriteLine("  auth pat list");
    Console.WriteLine("      List your personal access tokens");
    Console.WriteLine();
    Console.WriteLine("  auth pat revoke <id>");
    Console.WriteLine("      Revoke a personal access token by ID");
    Console.WriteLine();
    Console.WriteLine("Knowledge:");
    Console.WriteLine("  container create <name> [--description \"...\"]");
    Console.WriteLine("      Create a new container");
    Console.WriteLine();
    Console.WriteLine("  container list");
    Console.WriteLine("      List all containers");
    Console.WriteLine();
    Console.WriteLine("  container delete <name>");
    Console.WriteLine("      Delete an empty container");
    Console.WriteLine();
    Console.WriteLine("  container stats <name>");
    Console.WriteLine("      Show document counts, chunk count, storage size, and embedding info");
    Console.WriteLine();
    Console.WriteLine("  files list --container <name> [--path <folder>]");
    Console.WriteLine("      List files and folders in a container");
    Console.WriteLine();
    Console.WriteLine("  files delete --container <name> --file <id>");
    Console.WriteLine("      Delete a file by ID");
    Console.WriteLine();
    Console.WriteLine("  files get --container <name> --file <id-or-path>");
    Console.WriteLine("      Print the full text content of a file");
    Console.WriteLine();
    Console.WriteLine("  upload <path> --container <name> [--strategy <name>] [--destination <path>]");
    Console.WriteLine("      Upload file(s) to a container");
    Console.WriteLine();
    Console.WriteLine("  search \"<query>\" --container <name> [--mode <mode>] [--top <n>] [--path <folder>] [--min-score <0.0-1.0>]");
    Console.WriteLine("      Search within a container");
    Console.WriteLine();
    Console.WriteLine("  reindex --container <name> [--force] [--no-detect-changes]");
    Console.WriteLine("      Reindex documents in a container");
    Console.WriteLine();
    Console.WriteLine("Run 'connapse auth login' to authenticate before using knowledge commands.");
}

// ---------------------------------------------------------------------------
// Auth command handler
// ---------------------------------------------------------------------------

static async Task<int> HandleAuth(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions, string defaultBaseUrl)
{
    if (NeedsSubcommandUsage(args))
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  connapse auth login [--url <server-url>] [--no-browser]");
        Console.WriteLine("  connapse auth logout");
        Console.WriteLine("  connapse auth whoami");
        Console.WriteLine("  connapse auth pat create <name> [--expires <yyyy-MM-dd>]");
        Console.WriteLine("  connapse auth pat list");
        Console.WriteLine("  connapse auth pat revoke <id>");
        return IsSubcommandHelp(args) ? 0 : 1;
    }

    var subCommand = args[1].ToLower();

    return subCommand switch
    {
        "login" => await AuthLogin(args, jsonOptions, defaultBaseUrl),
        "logout" => await AuthLogout(httpClient),
        "whoami" => await AuthWhoami(httpClient, jsonOptions),
        "pat" => await HandleAuthPat(args, httpClient, jsonOptions),
        _ => Error($"Unknown auth subcommand '{subCommand}'")
    };
}

static async Task<int> AuthLogin(string[] args, JsonSerializerOptions jsonOptions, string defaultBaseUrl)
{
    var serverUrl = (GetOption(args, "--url") ?? defaultBaseUrl).TrimEnd('/');

    if (HasFlag(args, "--no-browser"))
        return await AuthLoginPassword(serverUrl, jsonOptions);

    return await AuthLoginBrowser(serverUrl, jsonOptions);
}

// Browser-based login using PKCE loopback redirect (RFC 8252 + RFC 7636)
static async Task<int> AuthLoginBrowser(string serverUrl, JsonSerializerOptions jsonOptions)
{
    Console.WriteLine($"Logging in to {serverUrl}");

    var oldCredentials = LoadCredentials();

    // Generate PKCE values
    var codeVerifier = GenerateCodeVerifier();
    var codeChallenge = ComputeS256Challenge(codeVerifier);
    var state = GenerateState();

    // Start local loopback listener on an OS-assigned random port
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var redirectUri = $"http://127.0.0.1:{port}/callback";

    var authUrl = $"{serverUrl}/cli/authorize" +
        $"?redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        $"&code_challenge_method=S256" +
        $"&state={Uri.EscapeDataString(state)}" +
        $"&machine_name={Uri.EscapeDataString(Environment.MachineName)}";

    Console.WriteLine("Opening browser to complete authentication...");
    try
    {
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
    }
    catch
    {
        // Ignore — user can open manually
    }
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"If your browser did not open, visit:");
    Console.WriteLine($"  {authUrl}");
    Console.ResetColor();
    Console.WriteLine("Waiting for browser authentication (timeout: 2 minutes)...");

    // Wait for the browser to redirect back to the local callback
    string receivedCode;
    string receivedState;
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try
    {
        (receivedCode, receivedState) = await WaitForCallbackAsync(listener, cts.Token);
    }
    catch (OperationCanceledException)
    {
        listener.Stop();
        return Error("Authentication timed out. Run 'connapse auth login' to try again.");
    }
    finally
    {
        listener.Stop();
    }

    // Validate state to prevent CSRF
    if (receivedCode == "__denied__")
        return Error("Authorization was denied in the browser.");

    if (!string.Equals(receivedState, state, StringComparison.Ordinal))
        return Error("State parameter mismatch — possible CSRF. Run 'connapse auth login' to try again.");

    // Exchange the auth code for a PAT via the server
    Console.Write("Exchanging authorization code... ");

    var loginHandler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };
    using var loginClient = new HttpClient(loginHandler)
    {
        BaseAddress = new Uri(serverUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };

    var exchangeRequest = new { code = receivedCode, codeVerifier, redirectUri };
    var exchangeJson = JsonSerializer.Serialize(exchangeRequest);
    var exchangeContent = new StringContent(exchangeJson, Encoding.UTF8, "application/json");

    HttpResponseMessage exchangeResponse;
    try
    {
        exchangeResponse = await loginClient.PostAsync("/api/v1/auth/cli/exchange", exchangeContent);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    if (!exchangeResponse.IsSuccessStatusCode)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed ({(int)exchangeResponse.StatusCode})");
        Console.ResetColor();
        return 1;
    }

    var exchangeResult = await exchangeResponse.Content.ReadFromJsonAsync<CliExchangeResponse>(jsonOptions);
    if (exchangeResult is null)
        return Error("Unexpected response from server.");

    var creds = new CliCredentials(exchangeResult.Token, serverUrl, exchangeResult.Email, exchangeResult.PatId);
    SaveCredentials(creds);

    // Revoke the old PAT to prevent accumulation on repeated logins
    if (oldCredentials?.PatId is not null)
    {
        try
        {
            loginClient.DefaultRequestHeaders.Add("X-Api-Key", exchangeResult.Token);
            await loginClient.DeleteAsync($"/api/v1/auth/pats/{oldCredentials.PatId}");
        }
        catch { /* Non-fatal — old PAT may already be revoked or server unreachable */ }
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Authenticated");
    Console.ResetColor();
    Console.WriteLine($"  Logged in as: {exchangeResult.Email}");
    Console.WriteLine($"  Server: {serverUrl}");
    Console.WriteLine($"  Token: {exchangeResult.PatId}");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("Credentials stored in ~/.connapse/credentials.json");
    Console.ResetColor();
    return 0;
}

// Fallback: email + password login (for headless/SSH environments)
static async Task<int> AuthLoginPassword(string serverUrl, JsonSerializerOptions jsonOptions)
{
    Console.WriteLine($"Logging in to {serverUrl}");

    var oldCredentials = LoadCredentials();

    Console.Write("Email: ");
    var email = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(email))
        return Error("Email is required.");

    Console.Write("Password: ");
    var password = ReadPassword();
    Console.WriteLine();
    if (string.IsNullOrWhiteSpace(password))
        return Error("Password is required.");

    var loginHandler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    };
    using var loginClient = new HttpClient(loginHandler)
    {
        BaseAddress = new Uri(serverUrl),
        Timeout = TimeSpan.FromSeconds(30)
    };

    Console.Write("Authenticating... ");

    var tokenRequest = new { email, password };
    var tokenJson = JsonSerializer.Serialize(tokenRequest);
    var tokenContent = new StringContent(tokenJson, Encoding.UTF8, "application/json");

    HttpResponseMessage tokenResponse;
    try
    {
        tokenResponse = await loginClient.PostAsync("/api/v1/auth/token", tokenContent);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    if (!tokenResponse.IsSuccessStatusCode)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(tokenResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? "Invalid email or password."
            : $"Failed ({(int)tokenResponse.StatusCode})");
        Console.ResetColor();
        return 1;
    }

    var tokenResult = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(jsonOptions);
    if (tokenResult is null)
        return Error("Unexpected response from server.");

    loginClient.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", tokenResult.AccessToken);

    var patName = $"CLI ({Environment.MachineName})";
    var patRequest = new { name = patName, scopes = (string[]?)null, expiresAt = (DateTime?)null };
    var patJson = JsonSerializer.Serialize(patRequest);
    var patContent = new StringContent(patJson, Encoding.UTF8, "application/json");

    HttpResponseMessage patResponse;
    try
    {
        patResponse = await loginClient.PostAsync("/api/v1/auth/pats", patContent);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed to create access token: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    if (!patResponse.IsSuccessStatusCode)
    {
        var errBody = await patResponse.Content.ReadAsStringAsync();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed to create access token ({(int)patResponse.StatusCode}): {errBody}");
        Console.ResetColor();
        return 1;
    }

    var pat = await patResponse.Content.ReadFromJsonAsync<PatCreateResponse>(jsonOptions);
    if (pat is null)
        return Error("Unexpected response when creating access token.");

    var creds = new CliCredentials(pat.Token, serverUrl, email, pat.Id);
    SaveCredentials(creds);

    // Revoke the old PAT to prevent accumulation on repeated logins
    // loginClient already has the JWT bearer token set, so it can authorize the delete
    if (oldCredentials?.PatId is not null)
    {
        try
        {
            await loginClient.DeleteAsync($"/api/v1/auth/pats/{oldCredentials.PatId}");
        }
        catch { /* Non-fatal — old PAT may already be revoked or server unreachable */ }
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Authenticated");
    Console.ResetColor();
    Console.WriteLine($"  Logged in as: {email}");
    Console.WriteLine($"  Server: {serverUrl}");
    Console.WriteLine($"  Token: {pat.Id} ({patName})");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("Credentials stored in ~/.connapse/credentials.json");
    Console.ResetColor();
    return 0;
}

static async Task<int> AuthLogout(HttpClient httpClient)
{
    var path = GetCredentialsPath();
    if (!File.Exists(path))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Not logged in (no credentials found).");
        Console.ResetColor();
        return 0;
    }

    var credentials = LoadCredentials();
    if (credentials?.PatId is not null)
    {
        Console.Write("Revoking CLI token... ");
        try
        {
            var response = await httpClient.DeleteAsync($"/api/v1/auth/pats/{credentials.PatId}");
            if (response.IsSuccessStatusCode)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Revoked");
                Console.ResetColor();
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Already revoked");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Could not revoke ({(int)response.StatusCode}) — clearing credentials anyway");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Could not reach server ({ex.Message}) — clearing credentials anyway");
            Console.ResetColor();
        }
    }

    File.Delete(path);
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Logged out. Credentials cleared.");
    Console.ResetColor();
    return 0;
}

static async Task<int> AuthWhoami(HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    var credentials = LoadCredentials();
    if (credentials is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Not logged in. Run 'connapse auth login' to authenticate.");
        Console.ResetColor();
        return 1;
    }

    Console.WriteLine($"  Email:  {credentials.UserEmail}");
    Console.WriteLine($"  Server: {credentials.ApiBaseUrl}");
    Console.WriteLine($"  Token:  {credentials.ApiKey[..Math.Min(12, credentials.ApiKey.Length)]}...");

    // Verify against server by calling a simple authenticated endpoint
    Console.Write("  Status: ");
    try
    {
        var response = await httpClient.GetAsync("/api/v1/auth/pats");
        if (response.IsSuccessStatusCode)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Connected");
            Console.ResetColor();
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Token invalid or revoked — run 'connapse auth login' again");
            Console.ResetColor();
            return 1;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Server returned {(int)response.StatusCode}");
            Console.ResetColor();
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Cannot reach server: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    return 0;
}

// ---------------------------------------------------------------------------
// Auth PAT sub-commands
// ---------------------------------------------------------------------------

static async Task<int> HandleAuthPat(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  connapse auth pat create <name> [--expires <yyyy-MM-dd>]");
        Console.WriteLine("  connapse auth pat list");
        Console.WriteLine("  connapse auth pat revoke <id>");
        return 1;
    }

    var subCommand = args[2].ToLower();

    return subCommand switch
    {
        "create" => await PatCreate(args, httpClient, jsonOptions),
        "list" => await PatList(httpClient, jsonOptions),
        "revoke" => await PatRevoke(args, httpClient, jsonOptions),
        _ => Error($"Unknown pat subcommand '{subCommand}'")
    };
}

static async Task<int> PatCreate(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 4)
    {
        Console.WriteLine("Usage: connapse auth pat create <name> [--expires <yyyy-MM-dd>]");
        return 1;
    }

    EnsureAuthenticated();

    var name = args[3];
    DateTime? expiresAt = null;

    var expiresStr = GetOption(args, "--expires");
    if (expiresStr is not null)
    {
        if (!DateTime.TryParse(expiresStr, out var parsed))
            return Error($"Invalid date '{expiresStr}'. Use yyyy-MM-dd format.");
        expiresAt = parsed.ToUniversalTime();
    }

    Console.Write($"Creating PAT '{name}'... ");

    var request = new { name, scopes = (string[]?)null, expiresAt };
    var json = JsonSerializer.Serialize(request);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    var response = await httpClient.PostAsync("/api/v1/auth/pats", content);

    if (!response.IsSuccessStatusCode)
    {
        var errBody = await response.Content.ReadAsStringAsync();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed ({(int)response.StatusCode}): {errBody}");
        Console.ResetColor();
        return 1;
    }

    var pat = await response.Content.ReadFromJsonAsync<PatCreateResponse>(jsonOptions);
    if (pat is null)
        return Error("Unexpected response from server.");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Created");
    Console.ResetColor();
    Console.WriteLine($"  ID:      {pat.Id}");
    Console.WriteLine($"  Name:    {pat.Name}");
    Console.WriteLine($"  Expires: {(pat.ExpiresAt.HasValue ? pat.ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd") : "Never")}");
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("  Token (copy now — shown only once):");
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"  {pat.Token}");
    Console.ResetColor();
    return 0;
}

static async Task<int> PatList(HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    EnsureAuthenticated();

    var response = await httpClient.GetAsync("/api/v1/auth/pats");
    response.EnsureSuccessStatusCode();

    var pats = await response.Content.ReadFromJsonAsync<List<PatListItem>>(jsonOptions);

    if (pats is null || pats.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No personal access tokens found.");
        Console.ResetColor();
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Personal access tokens ({pats.Count}):");
    Console.ResetColor();
    Console.WriteLine();

    foreach (var pat in pats)
    {
        var status = pat.IsRevoked ? "REVOKED" : pat.ExpiresAt.HasValue && pat.ExpiresAt < DateTime.UtcNow ? "EXPIRED" : "Active";
        var statusColor = status == "Active" ? ConsoleColor.Green
            : status == "REVOKED" ? ConsoleColor.Red
            : ConsoleColor.Yellow;

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  {pat.Name}");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  [{pat.Id}]");
        Console.ResetColor();
        Console.Write("  ");
        Console.ForegroundColor = statusColor;
        Console.WriteLine(status);
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"    Prefix: {pat.TokenPrefix}...  Created: {pat.CreatedAt.ToLocalTime():yyyy-MM-dd}");
        if (pat.ExpiresAt.HasValue)
            Console.Write($"  Expires: {pat.ExpiresAt.Value.ToLocalTime():yyyy-MM-dd}");
        if (pat.LastUsedAt.HasValue)
            Console.Write($"  Last used: {pat.LastUsedAt.Value.ToLocalTime():yyyy-MM-dd}");
        Console.WriteLine();
        Console.ResetColor();
    }

    return 0;
}

static async Task<int> PatRevoke(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 4)
    {
        Console.WriteLine("Usage: connapse auth pat revoke <id>");
        return 1;
    }

    EnsureAuthenticated();

    var idStr = args[3];
    if (!Guid.TryParse(idStr, out _))
        return Error($"'{idStr}' is not a valid PAT ID (expected GUID format).");

    Console.Write($"Revoking PAT {idStr}... ");

    var response = await httpClient.DeleteAsync($"/api/v1/auth/pats/{idStr}");

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Not found or already revoked.");
        Console.ResetColor();
        return 1;
    }

    if (!response.IsSuccessStatusCode)
    {
        var errBody = await response.Content.ReadAsStringAsync();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed ({(int)response.StatusCode}): {errBody}");
        Console.ResetColor();
        return 1;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Revoked");
    Console.ResetColor();
    return 0;
}

// ---------------------------------------------------------------------------
// Container commands
// ---------------------------------------------------------------------------

static async Task<int> HandleContainer(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (NeedsSubcommandUsage(args))
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  connapse container create <name> [--description \"...\"]");
        Console.WriteLine("  connapse container list");
        Console.WriteLine("  connapse container delete <name>");
        Console.WriteLine("  connapse container stats <name>");
        return IsSubcommandHelp(args) ? 0 : 1;
    }

    var subCommand = args[1].ToLower();

    return subCommand switch
    {
        "create" => await ContainerCreate(args, httpClient, jsonOptions),
        "list" => await ContainerList(httpClient, jsonOptions),
        "delete" => await ContainerDelete(args, httpClient, jsonOptions),
        "stats" => await ContainerStats(args, httpClient, jsonOptions),
        _ => Error($"Unknown container subcommand '{subCommand}'")
    };
}

static async Task<int> ContainerCreate(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: connapse container create <name> [--description \"...\"]");
        return 1;
    }

    EnsureAuthenticated();

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
    EnsureAuthenticated();

    var response = await httpClient.GetAsync("/api/containers?skip=0&take=100");
    response.EnsureSuccessStatusCode();

    var paged = await response.Content.ReadFromJsonAsync<PagedContainerResponse>(jsonOptions);
    var containers = paged?.Items;

    if (containers is null || containers.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No containers found.");
        Console.ResetColor();
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(paged!.HasMore
        ? $"Showing {containers.Count} of {paged.TotalCount} container(s)"
        : $"Found {containers.Count} container(s)");
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
        Console.WriteLine("Usage: connapse container delete <name>");
        return 1;
    }

    EnsureAuthenticated();

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

static async Task<int> ContainerStats(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: connapse container stats <name>");
        return 1;
    }

    EnsureAuthenticated();

    var name = args[2];
    var containerId = await ResolveContainerId(name, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{name}' not found.");

    Console.Write($"Fetching stats for '{name}'... ");

    var response = await httpClient.GetAsync($"/api/containers/{containerId}/stats");

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        var error = TryParseError(errorBody, jsonOptions);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed: {error}");
        Console.ResetColor();
        return 1;
    }

    var stats = await response.Content.ReadFromJsonAsync<JsonDocument>(jsonOptions);
    if (stats is null)
        return Error("Unexpected response from server.");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("OK");
    Console.ResetColor();
    Console.WriteLine();

    var root = stats.RootElement;

    // Documents section
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Documents:");
    Console.ResetColor();
    if (root.TryGetProperty("documents", out var docs))
    {
        if (docs.TryGetProperty("total", out var total))
            Console.WriteLine($"  Total:      {total.GetInt32()}");
        if (docs.TryGetProperty("ready", out var ready))
            Console.WriteLine($"  Ready:      {ready.GetInt32()}");
        if (docs.TryGetProperty("processing", out var processing))
            Console.WriteLine($"  Processing: {processing.GetInt32()}");
        if (docs.TryGetProperty("failed", out var failed))
            Console.WriteLine($"  Failed:     {failed.GetInt32()}");
    }
    Console.WriteLine();

    // Chunks
    if (root.TryGetProperty("totalChunks", out var chunks))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Total Chunks: ");
        Console.ResetColor();
        Console.WriteLine(chunks.GetInt32());
    }

    // Storage size
    if (root.TryGetProperty("totalSizeBytes", out var sizeBytes))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Storage Size: ");
        Console.ResetColor();
        var bytes = sizeBytes.GetInt64();
        Console.WriteLine(FormatBytes(bytes));
    }

    // Embedding info
    if (root.TryGetProperty("embedding", out var embedding))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Embedding:");
        Console.ResetColor();
        if (embedding.TryGetProperty("modelId", out var modelId) && modelId.ValueKind != JsonValueKind.Null)
            Console.WriteLine($"  Model: {modelId.GetString()}");
        if (embedding.TryGetProperty("dimensions", out var dims) && dims.ValueKind != JsonValueKind.Null)
            Console.WriteLine($"  Dimensions: {dims.GetInt32()}");
    }

    // Last indexed
    if (root.TryGetProperty("lastIndexedAt", out var lastIndexed) && lastIndexed.ValueKind != JsonValueKind.Null)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Last Indexed: ");
        Console.ResetColor();
        if (DateTime.TryParse(lastIndexed.GetString(), out var dt))
            Console.WriteLine(dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        else
            Console.WriteLine(lastIndexed.GetString());
    }

    return 0;
}

static string FormatBytes(long bytes)
{
    string[] sizes = ["B", "KB", "MB", "GB", "TB"];
    double len = bytes;
    int order = 0;
    while (len >= 1024 && order < sizes.Length - 1)
    {
        order++;
        len /= 1024;
    }
    return $"{len:0.##} {sizes[order]}";
}

// ---------------------------------------------------------------------------
// Files commands
// ---------------------------------------------------------------------------

static async Task<int> HandleFiles(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (NeedsSubcommandUsage(args))
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  connapse files list --container <name> [--path <folder>]");
        Console.WriteLine("  connapse files delete --container <name> --file <id>");
        Console.WriteLine("  connapse files get --container <name> --file <id-or-path>");
        return IsSubcommandHelp(args) ? 0 : 1;
    }

    var subCommand = args[1].ToLower();

    return subCommand switch
    {
        "list" => await FilesList(args, httpClient, jsonOptions),
        "delete" => await FilesDelete(args, httpClient, jsonOptions),
        "get" => await FilesGet(args, httpClient, jsonOptions),
        _ => Error($"Unknown files subcommand '{subCommand}'")
    };
}

static async Task<int> FilesList(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    EnsureAuthenticated();

    var containerName = GetOption(args, "--container");
    var path = GetOption(args, "--path") ?? "/";

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    var containerId = await ResolveContainerId(containerName, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{containerName}' not found.");

    var url = $"/api/containers/{containerId}/files?path={Uri.EscapeDataString(path)}";
    var response = await httpClient.GetAsync(url);

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        var error = TryParseError(errorBody, jsonOptions);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed: {error}");
        Console.ResetColor();
        return 1;
    }

    var result = await response.Content.ReadFromJsonAsync<JsonDocument>(jsonOptions);
    if (result is null)
        return Error("Unexpected response from server.");

    var root = result.RootElement;

    // Display folders
    if (root.TryGetProperty("folders", out var folders) && folders.GetArrayLength() > 0)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Folders:");
        Console.ResetColor();
        foreach (var folder in folders.EnumerateArray())
        {
            var name = folder.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var count = folder.TryGetProperty("documentCount", out var dc) ? dc.GetInt32().ToString() : "?";
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  {name}/");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  ({count} files)");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    // Display files
    if (root.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Files:");
        Console.ResetColor();
        foreach (var file in files.EnumerateArray())
        {
            var name = file.TryGetProperty("fileName", out var fn) ? fn.GetString() : "?";
            var id = file.TryGetProperty("id", out var fid) ? fid.GetString() : "?";
            var status = file.TryGetProperty("status", out var s) ? s.GetString() : "?";

            var statusColor = status switch
            {
                "Ready" => ConsoleColor.Green,
                "Processing" or "Pending" => ConsoleColor.Yellow,
                "Failed" => ConsoleColor.Red,
                _ => ConsoleColor.DarkGray
            };

            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  {name}");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  [{id}]");
            Console.ResetColor();
            Console.Write("  ");
            Console.ForegroundColor = statusColor;
            Console.WriteLine(status);
            Console.ResetColor();
        }
    }

    if ((!root.TryGetProperty("folders", out var f2) || f2.GetArrayLength() == 0) &&
        (!root.TryGetProperty("files", out var f3) || f3.GetArrayLength() == 0))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("No files or folders found at this path.");
        Console.ResetColor();
    }

    return 0;
}

static async Task<int> FilesDelete(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    EnsureAuthenticated();

    var containerName = GetOption(args, "--container");
    var fileId = GetOption(args, "--file");

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");
    if (string.IsNullOrWhiteSpace(fileId))
        return Error("--file is required. Specify the file ID.");
    if (!Guid.TryParse(fileId, out _))
        return Error($"'{fileId}' is not a valid file ID (expected GUID format).");

    var containerId = await ResolveContainerId(containerName, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{containerName}' not found.");

    Console.Write($"Deleting file {fileId}... ");

    var response = await httpClient.DeleteAsync($"/api/containers/{containerId}/files/{fileId}");

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Not found.");
        Console.ResetColor();
        return 1;
    }

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

static async Task<int> FilesGet(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    EnsureAuthenticated();

    var containerName = GetOption(args, "--container");
    var fileRef = GetOption(args, "--file");

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");
    if (string.IsNullOrWhiteSpace(fileRef))
        return Error("--file is required. Specify the file ID or path.");

    var containerId = await ResolveContainerId(containerName, httpClient, jsonOptions);
    if (containerId is null)
        return Error($"Container '{containerName}' not found.");

    // Try as GUID first (direct file ID), then as path
    string url;
    if (Guid.TryParse(fileRef, out _))
    {
        url = $"/api/containers/{containerId}/files/{fileRef}/content";
    }
    else
    {
        // Use the search/lookup by path - get file list at that path first
        url = $"/api/containers/{containerId}/files/{Uri.EscapeDataString(fileRef)}/content";
    }

    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
    var response = await httpClient.SendAsync(request);

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        return Error($"File '{fileRef}' not found in container '{containerName}'.");

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        var error = TryParseError(errorBody, jsonOptions);
        return Error($"Failed: {error}");
    }

    var content = await response.Content.ReadAsStringAsync();
    Console.WriteLine(content);
    return 0;
}

// ---------------------------------------------------------------------------
// Upload / search / reindex commands (unchanged logic)
// ---------------------------------------------------------------------------

static async Task<int> HandleUpload(string[] args, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    if (NeedsSubcommandUsage(args))
    {
        Console.WriteLine("Usage: connapse upload <path> --container <name> [--strategy <name>] [--destination <path>]");
        return IsSubcommandHelp(args) ? 0 : 1;
    }

    var path = args[1];
    var containerName = GetOption(args, "--container");
    var strategy = GetOption(args, "--strategy") ?? "Semantic";
    var destination = GetOption(args, "--destination") ?? "/";

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    EnsureAuthenticated();

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
    if (NeedsSubcommandUsage(args))
    {
        Console.WriteLine("Usage: connapse search \"<query>\" --container <name> [--mode <mode>] [--top <n>] [--path <folder>] [--min-score <0.0-1.0>]");
        return IsSubcommandHelp(args) ? 0 : 1;
    }

    var query = args[1];
    var containerName = GetOption(args, "--container");
    var mode = GetOption(args, "--mode") ?? "Hybrid";
    var topK = int.Parse(GetOption(args, "--top") ?? "10");
    var folderPath = GetOption(args, "--path");
    var minScoreStr = GetOption(args, "--min-score");

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    EnsureAuthenticated();

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
    if (IsSubcommandHelp(args))
    {
        Console.WriteLine("Usage: connapse reindex --container <name> [--force] [--no-detect-changes]");
        return 0;
    }

    var containerName = GetOption(args, "--container");
    var force = HasFlag(args, "--force");
    var detectChanges = !HasFlag(args, "--no-detect-changes");

    if (string.IsNullOrWhiteSpace(containerName))
        return Error("--container is required. Specify the container name.");

    EnsureAuthenticated();

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

// ---------------------------------------------------------------------------
// PKCE + loopback callback helpers
// ---------------------------------------------------------------------------

static string GenerateCodeVerifier()
{
    var bytes = RandomNumberGenerator.GetBytes(32);
    return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

static string ComputeS256Challenge(string codeVerifier)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier));
    return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

static string GenerateState()
{
    var bytes = RandomNumberGenerator.GetBytes(16);
    return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}

// Accepts one TCP connection from the browser callback and extracts code + state.
// Returns ("__denied__", state) when the server sends error=access_denied.
static async Task<(string Code, string State)> WaitForCallbackAsync(
    TcpListener listener, CancellationToken ct)
{
    using var client = await listener.AcceptTcpClientAsync(ct);
    using var stream = client.GetStream();

    // Read the HTTP request (we only need the first line)
    var buffer = new byte[4096];
    var bytesRead = await stream.ReadAsync(buffer, ct);
    var request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

    // Extract query string from "GET /callback?... HTTP/1.1"
    var firstLine = request.Split('\n')[0];
    var queryString = "";
    var pathPart = firstLine.Split(' ').ElementAtOrDefault(1) ?? "";
    var qIdx = pathPart.IndexOf('?');
    if (qIdx >= 0)
        queryString = pathPart[(qIdx + 1)..].Trim();

    var queryParams = queryString
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2))
        .Where(p => p.Length == 2)
        .ToDictionary(
            p => Uri.UnescapeDataString(p[0]),
            p => Uri.UnescapeDataString(p[1]));

    var code = queryParams.GetValueOrDefault("code", "");
    var state = queryParams.GetValueOrDefault("state", "");
    var error = queryParams.GetValueOrDefault("error", "");

    // Write HTTP response so the browser shows a friendly message
    string body;
    if (!string.IsNullOrEmpty(error))
    {
        body = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width,initial-scale=1" />
              <title>Authorization Denied - Connapse</title>
              <style>
                *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  background: #0c0c14;
                  color: #e4e2ee;
                  font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
                  min-height: 100vh;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                }
                .card {
                  background: #1a1a28;
                  border: 1px solid #2a2a3e;
                  border-radius: 0.75rem;
                  padding: 2.5rem 2rem;
                  max-width: 400px;
                  width: 100%;
                  text-align: center;
                }
                .icon {
                  width: 52px;
                  height: 52px;
                  border-radius: 50%;
                  background: rgba(239, 68, 68, 0.1);
                  border: 1px solid rgba(239, 68, 68, 0.3);
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  margin: 0 auto 1.25rem;
                  color: #f87171;
                  font-size: 1.4rem;
                }
                h1 { font-size: 1.1rem; font-weight: 600; margin-bottom: 0.5rem; }
                p { font-size: 0.875rem; color: #8b89a0; }
                .brand { font-size: 0.75rem; color: #8b89a0; margin-top: 1.5rem; }
                .brand strong { color: #8b5cf6; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="icon">&#x2715;</div>
                <h1>Authorization Denied</h1>
                <p>You may close this tab and return to the terminal.</p>
                <p class="brand">Powered by <strong>Connapse</strong></p>
              </div>
            </body>
            </html>
            """;
    }
    else
    {
        body = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width,initial-scale=1" />
              <title>Authorized - Connapse</title>
              <style>
                *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
                body {
                  background: #0c0c14;
                  color: #e4e2ee;
                  font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
                  min-height: 100vh;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                }
                .card {
                  background: #1a1a28;
                  border: 1px solid #2a2a3e;
                  border-radius: 0.75rem;
                  padding: 2.5rem 2rem;
                  max-width: 400px;
                  width: 100%;
                  text-align: center;
                }
                .icon {
                  width: 52px;
                  height: 52px;
                  border-radius: 50%;
                  background: rgba(139, 92, 246, 0.1);
                  border: 1px solid rgba(139, 92, 246, 0.25);
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  margin: 0 auto 1.25rem;
                  color: #a78bfa;
                  font-size: 1.4rem;
                }
                h1 { font-size: 1.1rem; font-weight: 600; margin-bottom: 0.5rem; }
                p { font-size: 0.875rem; color: #8b89a0; }
                .brand { font-size: 0.75rem; color: #8b89a0; margin-top: 1.5rem; }
                .brand strong { color: #8b5cf6; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="icon">&#10003;</div>
                <h1>Authentication Successful</h1>
                <p>You may close this tab and return to the terminal.</p>
                <p class="brand">Powered by <strong>Connapse</strong></p>
              </div>
            </body>
            </html>
            """;
    }

    var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    var responseBytes = Encoding.UTF8.GetBytes(response);
    await stream.WriteAsync(responseBytes, ct);
    await stream.FlushAsync(ct);

    if (!string.IsNullOrEmpty(error))
        return ("__denied__", state);

    return (code, state);
}

// ---------------------------------------------------------------------------
// version / update
// ---------------------------------------------------------------------------

static int HandleVersion()
{
    Console.WriteLine($"connapse v{GetCurrentVersion()}");
    return 0;
}

static async Task<int> HandleUpdate(string[] args)
{
    if (IsSubcommandHelp(args))
    {
        Console.WriteLine("Usage: connapse update [--check] [--pre]");
        return 0;
    }

    var checkOnly = args.Contains("--check");
    var includePre = args.Contains("--pre");
    var currentVersion = GetCurrentVersion();
    Console.WriteLine($"Current version: v{currentVersion}");
    if (includePre)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Including pre-release versions.");
        Console.ResetColor();
    }

    using var ghClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    ghClient.DefaultRequestHeaders.UserAgent.Add(
        new ProductInfoHeaderValue("connapse-cli", currentVersion));

    GitHubRelease? release;
    try
    {
        release = await GetLatestReleaseAsync(ghClient, includePre);
    }
    catch (Exception ex)
    {
        return Error($"Could not reach GitHub: {ex.Message}");
    }

    if (release is null)
        return Error("Could not parse release information from GitHub.");

    var latestVersion = release.TagName.TrimStart('v');
    Console.WriteLine($"Latest version:  v{latestVersion}");

    if (!IsNewer(latestVersion, currentVersion))
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("You are already up to date.");
        Console.ResetColor();
        return 0;
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Update available: v{currentVersion} → v{latestVersion}");
    Console.ResetColor();

    if (checkOnly) return 0;

    var assetName = GetPlatformAssetName();
    var asset = release.Assets.FirstOrDefault(a =>
        string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));

    if (asset is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"No binary for this platform ({assetName}) in the release.");
        Console.WriteLine("If you installed via NuGet, run:  dotnet tool update -g Connapse.CLI");
        Console.ResetColor();
        return 1;
    }

    // Global tool install — update via nupkg from GitHub release
    if (IsGlobalToolInstall())
    {
        var nupkgAsset = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));

        if (nupkgAsset is null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No NuGet package in this release. Download the binary manually:");
            Console.WriteLine($"  https://github.com/Destrayon/Connapse/releases/tag/{release.TagName}");
            Console.ResetColor();
            return 1;
        }

        Console.Write($"Download and install v{latestVersion}? [y/N] ");
        var confirm = Console.ReadLine()?.Trim().ToLower();
        if (confirm is not ("y" or "yes")) return 0;

        var tmpDir = Path.Combine(Path.GetTempPath(), "connapse-update");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        Directory.CreateDirectory(tmpDir);

        try
        {
            Console.Write($"Downloading {nupkgAsset.Name}... ");
            using var dlResponse = await ghClient.GetAsync(
                nupkgAsset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            dlResponse.EnsureSuccessStatusCode();
            var nupkgPath = Path.Combine(tmpDir, nupkgAsset.Name);
            await using (var fs = new FileStream(nupkgPath, FileMode.Create, FileAccess.Write))
                await dlResponse.Content.CopyToAsync(fs);
            Console.WriteLine("done.");

            Console.Write("Installing... ");
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"tool update -g Connapse.CLI --add-source \"{tmpDir}\" --version {latestVersion}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stderr = await stderrTask;

            if (proc.ExitCode == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"done. Updated to v{latestVersion}.");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine();
                return Error($"dotnet tool update failed:\n{stderr.Trim()}");
            }
        }
        catch (Exception ex)
        {
            return Error($"\nUpdate failed: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        return 0;
    }

    // Standalone binary install — download and replace executable
    Console.Write($"Download and install v{latestVersion}? [y/N] ");
    var standaloneConfirm = Console.ReadLine()?.Trim().ToLower();
    if (standaloneConfirm is not ("y" or "yes")) return 0;

    var exePath = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exePath))
        return Error("Could not determine current executable path.");

    var tmpPath = exePath + ".new";
    Console.Write($"Downloading {assetName}... ");
    try
    {
        using var response = await ghClient.GetAsync(
            asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write);
        await response.Content.CopyToAsync(fs);
    }
    catch (Exception ex)
    {
        if (File.Exists(tmpPath)) File.Delete(tmpPath);
        return Error($"\nDownload failed: {ex.Message}");
    }
    Console.WriteLine("done.");

    if (OperatingSystem.IsWindows())
    {
        // Running .exe is locked on Windows — hand off to a batch script
        var batPath = Path.Combine(Path.GetTempPath(), "connapse-update.bat");
        File.WriteAllText(batPath,
            "@echo off\r\n" +
            "timeout /t 2 /nobreak > nul\r\n" +
            $"move /y \"{tmpPath}\" \"{exePath}\" > nul\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd",
            Arguments = $"/c \"{batPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"connapse will be updated to v{latestVersion} in a moment.");
        Console.ResetColor();
    }
    else
    {
        File.Move(tmpPath, exePath, overwrite: true);
        // Restore execute permission after overwrite
        var mode = File.GetUnixFileMode(exePath);
        File.SetUnixFileMode(exePath,
            mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Updated to v{latestVersion}. Restart connapse to confirm.");
        Console.ResetColor();
    }

    return 0;
}

// ---------------------------------------------------------------------------
// Credential helpers
// ---------------------------------------------------------------------------

static string GetCredentialsPath()
{
    var home = GetUserHomePath();
    return Path.Combine(home, ".connapse", "credentials.json");
}

static CliCredentials? LoadCredentials()
{
    var path = GetCredentialsPath();
    if (!File.Exists(path)) return null;

    try
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, CliJsonContext.Default.CliCredentials);
    }
    catch
    {
        return null;
    }
}

static void SaveCredentials(CliCredentials credentials)
{
    var path = GetCredentialsPath();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var json = JsonSerializer.Serialize(credentials, CliJsonContext.Default.CliCredentials);
    File.WriteAllText(path, json);
}

static void EnsureAuthenticated()
{
    if (LoadCredentials() is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Not authenticated. Run 'connapse auth login' first.");
        Console.ResetColor();
        Environment.Exit(1);
    }
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

static string GetUserHomePath() =>
    Environment.GetEnvironmentVariable("USERPROFILE")
    ?? Environment.GetEnvironmentVariable("HOME")
    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

static string GetCurrentVersion() =>
    typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0]   // strip build metadata e.g. +abc123
        ?? "0.0.0";

static string GetPlatformAssetName()
{
    if (OperatingSystem.IsWindows()) return "connapse-cli-win-x64.exe";
    if (OperatingSystem.IsLinux())   return "connapse-cli-linux-x64";
    if (OperatingSystem.IsMacOS())
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "connapse-cli-osx-arm64"
            : "connapse-cli-osx-x64";
    return "connapse-cli-linux-x64";
}

static bool IsGlobalToolInstall()
{
    var exePath = Environment.ProcessPath;
    if (string.IsNullOrEmpty(exePath)) return false;
    var dotnetToolsDir = Path.Combine(
        GetUserHomePath(),
        ".dotnet", "tools");
    return exePath.StartsWith(dotnetToolsDir, StringComparison.OrdinalIgnoreCase);
}

static bool IsNewer(string latest, string current)
{
    if (NuGetVersion.TryParse(latest, out var latestV) && NuGetVersion.TryParse(current, out var currentV))
        return latestV > currentV;
    return string.Compare(latest, current, StringComparison.Ordinal) > 0;
}

static async Task<GitHubRelease?> GetLatestReleaseAsync(HttpClient ghClient, bool includePre)
{
    if (!includePre)
    {
        return await ghClient.GetFromJsonAsync(
            "https://api.github.com/repos/Destrayon/Connapse/releases/latest",
            CliJsonContext.Default.GitHubRelease);
    }

    var releases = await ghClient.GetFromJsonAsync(
        "https://api.github.com/repos/Destrayon/Connapse/releases?per_page=10",
        CliJsonContext.Default.ListGitHubRelease);

    return releases?.FirstOrDefault(r => !r.Draft);
}

static async Task CheckForUpdateNotification()
{
    try
    {
        var checkFile = Path.Combine(
            GetUserHomePath(),
            ".connapse", "last-update-check");

        if (File.Exists(checkFile) &&
            DateTime.TryParse(File.ReadAllText(checkFile).Trim(), out var lastCheck) &&
            DateTime.UtcNow - lastCheck < TimeSpan.FromHours(24))
            return;

        var currentVersion = GetCurrentVersion();
        using var ghClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        ghClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("connapse-cli", currentVersion));

        var release = await ghClient.GetFromJsonAsync(
            "https://api.github.com/repos/Destrayon/Connapse/releases/latest",
            CliJsonContext.Default.GitHubRelease);

        Directory.CreateDirectory(Path.GetDirectoryName(checkFile)!);
        File.WriteAllText(checkFile, DateTime.UtcNow.ToString("O"));

        if (release is null) return;
        var latestVersion = release.TagName.TrimStart('v');
        if (IsNewer(latestVersion, currentVersion))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  Note: connapse v{latestVersion} is available. Run 'connapse update' to upgrade.");
            Console.ResetColor();
        }
    }
    catch { }
}

// Resolve container name to ID via API
static async Task<string?> ResolveContainerId(string nameOrId, HttpClient httpClient, JsonSerializerOptions jsonOptions)
{
    // If it looks like a GUID, use it directly
    if (Guid.TryParse(nameOrId, out _))
        return nameOrId;

    // Otherwise resolve by name
    var response = await httpClient.GetAsync($"/api/containers/by-name/{Uri.EscapeDataString(nameOrId.ToLowerInvariant())}");
    if (!response.IsSuccessStatusCode) return null;

    var container = await response.Content.ReadFromJsonAsync<ContainerInfo>(jsonOptions);
    return container?.Id;
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

// Reads a password from stdin with masking
static string ReadPassword()
{
    var password = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Remove(password.Length - 1, 1);
                Console.Write("\b \b");
            }
        }
        else if (key.KeyChar != '\0')
        {
            password.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    return password.ToString();
}

// ---------------------------------------------------------------------------
// DTOs and records
// ---------------------------------------------------------------------------

record CliCredentials(string ApiKey, string ApiBaseUrl, string UserEmail, Guid? PatId = null);
record CliExchangeResponse(string Token, Guid PatId, DateTime? ExpiresAt, string Email);
record TokenResponse(string AccessToken, string RefreshToken, DateTime ExpiresAt, string TokenType);
record PatCreateResponse(Guid Id, string Name, string Token, string[] Scopes, DateTime CreatedAt, DateTime? ExpiresAt);
record PatListItem(Guid Id, string Name, string TokenPrefix, string[] Scopes, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt, bool IsRevoked);
record PagedContainerResponse(List<ContainerInfo> Items, int TotalCount, bool HasMore);
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
record GitHubRelease(
    [property: JsonPropertyName("tag_name")]   string TagName,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("draft")]      bool Draft,
    [property: JsonPropertyName("assets")]     List<GitHubAsset> Assets);
record GitHubAsset(
    [property: JsonPropertyName("name")]                  string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<PatListItem>))]
[JsonSerializable(typeof(PatListItem))]
[JsonSerializable(typeof(PatCreateResponse))]
[JsonSerializable(typeof(PagedContainerResponse))]
[JsonSerializable(typeof(ContainerInfo))]
[JsonSerializable(typeof(List<ContainerInfo>))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(ReindexResult))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(CliExchangeResponse))]
[JsonSerializable(typeof(CliCredentials))]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(JsonDocument))]
internal partial class CliJsonContext : JsonSerializerContext { }
