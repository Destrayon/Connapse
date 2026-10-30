using System.Reflection;
using System.Text.Json;
using Connapse.Application;
using Connapse.Web.Endpoints;
using Connapse.Web.Tests.Components;
using FluentAssertions;

namespace Connapse.Web.Tests.OpenApi;

/// <summary>
/// CON-20: the generic connapse_api_* MCP tools are built from the committed spec, so every operation
/// marked for MCP must be named there, on its route, with the scope and risk its [Operation] declares.
/// </summary>
[Trait("Category", "Unit")]
public class OperationExtensionTests
{
    [Fact]
    public void CommittedSpec_NamesEveryGenericOperation_WithItsScopeAndRisk()
    {
        var inSpec = SpecOperations().ToLookup(o => o.Id, StringComparer.Ordinal);
        var declared = typeof(OperationAttribute).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<OperationAttribute>())
            .OfType<OperationAttribute>()
            .ToList();

        declared.Where(o => o.Mcp == McpExposure.Generic && !inSpec.Contains(o.Id)).Select(o => o.Id)
            .Should().BeEmpty("an operation marked for MCP is reachable only through a route the spec names it on");

        var byId = declared.ToDictionary(o => o.Id, StringComparer.Ordinal);
        SpecOperations()
            .Where(s => !byId.TryGetValue(s.Id, out var o) || o.Scope != s.Scope || o.Risk.ToString() != s.Risk || o.Mcp.ToString() != s.Mcp)
            .Select(s => $"{s.Id}: {s.Scope} {s.Risk} {s.Mcp}")
            .Should().BeEmpty("the spec says exactly what each route's [Operation] declares");
    }

    [Fact]
    public void CommittedSpec_EveryRouteTakingASecret_IsCredentialOrRiskier()
    {
        // These take a secret only to check it, or to refuse it, and save none.
        string[] savesNoSecret = ["connections.test", "providers.custom.check", "sources.create"];

        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json")));
        var schemas = spec.RootElement.GetProperty("components").GetProperty("schemas");
        var understated = new List<string>();
        foreach (var path in spec.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Value.ValueKind != JsonValueKind.Object
                    || !operation.Value.TryGetProperty(OperationExtensionTransformer.Name, out var x)
                    || !operation.Value.TryGetProperty("requestBody", out var body))
                    continue;
                string id = x.GetProperty("id").GetString()!;
                string risk = x.GetProperty("risk").GetString()!;
                var secrets = body.GetProperty("content").EnumerateObject()
                    .SelectMany(c => SecretFields(c.Value.GetProperty("schema"), schemas, [], 0)).Distinct().ToList();
                if (secrets.Count > 0 && risk is not (nameof(RiskTier.Credential) or nameof(RiskTier.Destructive)) && !savesNoSecret.Contains(id))
                    understated.Add($"{id} ({risk}) takes {string.Join(", ", secrets)}");
            }

        understated.Should().BeEmpty("a route that saves a secret is a credential change, held for approval like connections.replaceSecret");
    }

    [Fact]
    public void Find_HandlerTakingAReadBesideItsDelete_LinksTheDelete()
    {
        EndpointOperations.Find([Handler(nameof(DeleteAndList))])!.Id.Should().Be("planted.delete");
    }

    [Fact]
    public void Find_HandlerTakingTwoOfTheSameRisk_LinksNone()
    {
        EndpointOperations.Find([Handler(nameof(TwoDeletes))]).Should().BeNull();
    }

    [Fact]
    public void Find_HandlerTakingNoOperation_LinksNone()
    {
        EndpointOperations.Find([Handler(nameof(NoOperation))]).Should().BeNull();
    }

    private static MethodInfo Handler(string name) =>
        typeof(OperationExtensionTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void DeleteAndList(PlantedList list, PlantedDelete delete) { }

    private static void TwoDeletes(PlantedDelete delete, PlantedOtherDelete other) { }

    private static void NoOperation(string value) { }

    [Operation("planted.list", Scope = "planted:read", Risk = RiskTier.Read)]
    private sealed class PlantedList;

    [Operation("planted.delete", Scope = "planted:write", Risk = RiskTier.Destructive)]
    private sealed class PlantedDelete;

    [Operation("planted.otherDelete", Scope = "planted:write", Risk = RiskTier.Destructive)]
    private sealed class PlantedOtherDelete;

    private static readonly System.Text.RegularExpressions.Regex SecretName =
        new("(privateKey|passphrase|token|secret|password|apiKey)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static IEnumerable<string> SecretFields(JsonElement schema, JsonElement schemas, HashSet<string> seen, int depth)
    {
        if (depth > 8 || schema.ValueKind != JsonValueKind.Object)
            yield break;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            string name = reference.GetString()!.Split('/')[^1];
            if (seen.Add(name) && schemas.TryGetProperty(name, out var target))
                foreach (string field in SecretFields(target, schemas, seen, depth + 1))
                    yield return field;
            yield break;
        }
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
            {
                if (SecretName.IsMatch(property.Name))
                    yield return property.Name;
                foreach (string field in SecretFields(property.Value, schemas, seen, depth + 1))
                    yield return field;
            }
        foreach (string key in new[] { "items", "additionalProperties" })
            if (schema.TryGetProperty(key, out var inner))
                foreach (string field in SecretFields(inner, schemas, seen, depth + 1))
                    yield return field;
        foreach (string key in new[] { "oneOf", "anyOf", "allOf" })
            if (schema.TryGetProperty(key, out var options))
                foreach (var option in options.EnumerateArray())
                    foreach (string field in SecretFields(option, schemas, seen, depth + 1))
                        yield return field;
    }

    private sealed record SpecOperation(string Id, string Scope, string Risk, string Mcp);

    private static IEnumerable<SpecOperation> SpecOperations()
    {
        using var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(PageTestPaths.RepositoryRoot(), "docs", "api", "openapi.json")));
        var found = new List<SpecOperation>();
        foreach (var path in spec.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                if (operation.Value.ValueKind == JsonValueKind.Object && operation.Value.TryGetProperty(OperationExtensionTransformer.Name, out var x))
                    found.Add(new SpecOperation(x.GetProperty("id").GetString()!, x.GetProperty("scope").GetString()!,
                        x.GetProperty("risk").GetString()!, x.GetProperty("mcp").GetString()!));
        return found;
    }
}
