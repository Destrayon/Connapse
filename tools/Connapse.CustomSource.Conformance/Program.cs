using System.Text.Json;
using System.Text.Json.Serialization;
using Connapse.CustomSource.Conformance;

const string Usage = """
    connapse-source-check — checks a middle man against the Connapse custom source protocol, version 1.

    Usage: connapse-source-check --url <base-url> --token <token> [options]

      --url <url>               The middle man's address, as entered in Connapse.
      --token <token>           The bearer token it expects. Or set CONNAPSE_SOURCE_TOKEN.
      --scope <id>              The scope to test. Default: the first one offered.
      --user <connapse-user-id> A Connapse user ID the middle man maps to someone who can read
                                documents in the scope. Needed by the check and key checks.
      --link-secret <secret>    The provider's link secret. Or set CONNAPSE_LINK_SECRET.
      --provider-id <id>        The provider's ID in Connapse (the link token audience).
      --issuer <url>            Connapse's address (the link token issuer).
      --create-command <cmd>    Creates a document in $CONNAPSE_SCOPE, for the change feed checks.
      --delete-command <cmd>    Deletes $CONNAPSE_DOCUMENT_ID from $CONNAPSE_SCOPE.
      --sign-in-command <cmd>   Finishes the middle man's sign-in for $CONNAPSE_LINK_TOKEN posted to
                                $CONNAPSE_LINK_URL and prints the code (or the URL it redirected to).
      --json                    Print the results as JSON.

    Checks whose capability, option or command is missing are skipped. Exit code: 0 when nothing failed,
    1 when a check failed, 2 for a usage error.
    """;

Dictionary<string, string> values = new(StringComparer.Ordinal);
bool json = false;
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];
    if (arg is "-h" or "--help")
    {
        Console.WriteLine(Usage);
        return 0;
    }
    if (arg == "--json")
    {
        json = true;
        continue;
    }
    if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"Unexpected argument '{arg}'.\n\n{Usage}");
        return 2;
    }
    values[arg[2..]] = args[++i];
}

string? Option(string name, string? environment = null) =>
    values.GetValueOrDefault(name) ?? (environment is null ? null : Environment.GetEnvironmentVariable(environment));

if (Option("url") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out Uri? baseUrl) || Option("token", "CONNAPSE_SOURCE_TOKEN") is not { } token)
{
    Console.Error.WriteLine($"--url and --token are required.\n\n{Usage}");
    return 2;
}

ConformanceOptions options = new()
{
    BaseUrl = baseUrl,
    Token = token,
    Scope = Option("scope"),
    UserId = Option("user"),
    LinkSecret = Option("link-secret", "CONNAPSE_LINK_SECRET"),
    ProviderId = Option("provider-id"),
    Issuer = Option("issuer"),
};
CommandHooks hooks = new(Option("create-command"), Option("delete-command"), Option("sign-in-command"));

// Redirects are answers to check, never something to follow.
using HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
IReadOnlyList<CheckResult> results = await new ConformanceSuite(http, options, hooks).RunAsync();

if (json)
{
    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    }));
}
else
{
    foreach (CheckResult result in results)
    {
        string mark = result.Outcome switch { CheckOutcome.Pass => "PASS", CheckOutcome.Fail => "FAIL", _ => "SKIP" };
        Console.WriteLine($"{mark}  {result.Id,-28} {result.Title}");
        if (result.Detail is not null) Console.WriteLine($"      {result.Detail}");
    }

    Console.WriteLine();
    Console.WriteLine($"{results.Count(r => r.Outcome == CheckOutcome.Pass)} passed, " +
        $"{results.Count(r => r.Outcome == CheckOutcome.Fail)} failed, {results.Count(r => r.Outcome == CheckOutcome.Skip)} skipped.");
}

return results.Any(r => r.Outcome == CheckOutcome.Fail) ? 1 : 0;
