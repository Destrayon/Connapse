using Connapse.AgentSearch.Prototype;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage: Connapse.AgentSearch.Prototype --root <folder of original files> [--rg <path to rg>]
string? root = null;
string rg = Environment.GetEnvironmentVariable("CONNAPSE_RG") ?? "rg";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--root") root = args[i + 1];
    if (args[i] == "--rg") rg = args[i + 1];
}
if (root is null || !Directory.Exists(root))
{
    Console.Error.WriteLine("--root must name an existing folder");
    return 2;
}

var builder = Host.CreateApplicationBuilder();
// stdout carries the MCP protocol, so every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(new FileRoot(root, rg));
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<FileSearchTools>();
await builder.Build().RunAsync();
return 0;
