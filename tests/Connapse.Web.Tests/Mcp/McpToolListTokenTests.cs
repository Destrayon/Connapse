using System.Reflection;
using System.Text.Json;
using Connapse.Web.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.Tokenizers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit.Abstractions;

namespace Connapse.Web.Tests.Mcp;

/// <summary>
/// Pass bar 1 for CON-346 (eval/pass-bars.md): the compact tools/list plus the server instructions is at
/// most 30% of the full tools/list's tokens, counted with o200k_base.
/// </summary>
[Trait("Category", "Unit")]
public class McpToolListTokenTests(ITestOutputHelper output)
{
    /// <summary>
    /// Tells the SDK which tool parameters are injected rather than sent by the agent: interfaces and
    /// classes that aren't records, which is how Connapse's operations and services are declared.
    /// </summary>
    private sealed class InjectedTypes : IServiceProviderIsService, IServiceProvider
    {
        public bool IsService(Type serviceType) =>
            serviceType.IsInterface
            || (serviceType.IsClass && serviceType != typeof(string) && !serviceType.IsArray
                && serviceType.GetMethod("<Clone>$") is null && !typeof(System.Collections.IEnumerable).IsAssignableFrom(serviceType));

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService) ? this : null;
    }

    private static List<Tool> Tools()
    {
        var services = new InjectedTypes();
        return typeof(McpTools).Assembly.GetTypes()
            .Where(t => t.IsDefined(typeof(McpServerToolTypeAttribute)))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.IsDefined(typeof(McpServerToolAttribute)))
            .Select(m => McpServerTool.Create(m, target: null, new McpServerToolCreateOptions { Services = services }).ProtocolTool)
            .ToList();
    }

    private static int Count(Tokenizer tokenizer, IEnumerable<Tool> tools) =>
        tokenizer.CountTokens(JsonSerializer.Serialize(new ListToolsResult { Tools = [.. tools] }, McpJsonUtilities.DefaultOptions));

    [Fact]
    public void CompactListAndInstructions_AtMost30PercentOfFullList()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var tools = Tools();
        var full = tools.Where(t => McpToolList.Shows(t.Name, full: true)).ToList();
        var compact = tools.Where(t => McpToolList.Shows(t.Name, full: false)).ToList();
        compact.Select(t => t.Name).Should().BeEquivalentTo(McpToolList.Core.Concat(McpToolSearch.Names));
        full.Should().HaveCountGreaterThan(20, "the full list is every tool");
        full.Should().OnlyContain(t => !t.InputSchema.GetRawText().Contains("\"getContainer\""), "injected services aren't arguments");

        foreach (var tool in tools)
            output.WriteLine($"{tool.Name}: {Count(tokenizer, [tool])}");
        output.WriteLine($"instructions: {tokenizer.CountTokens(McpToolList.CompactInstructions)}; default: {tokenizer.CountTokens(McpServerConfig.McpServerInstructions)}");
        int fullTokens = Count(tokenizer, full);
        int compactTokens = Count(tokenizer, compact) + tokenizer.CountTokens(McpToolList.CompactInstructions);

        ((double)compactTokens / fullTokens).Should().BeLessThanOrEqualTo(0.30,
            $"the compact list and its instructions take {compactTokens} tokens against the full list's {fullTokens}");
    }
}
