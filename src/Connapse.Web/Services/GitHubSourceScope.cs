using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using Connapse.Storage.Connectors;
using Connapse.Storage.Connectors.GitHub;
using Connapse.Web.Components.Settings;

namespace Connapse.Web.Services;

/// <summary>
/// Completes a GitHub source's scope the way the New source dialog does: the repository's id and
/// whether it is private come from GitHub, asked as the connection's installation, never from the
/// caller.
/// </summary>
public static class GitHubSourceScope
{
    public sealed record Result(string? ScopeJson, string? Error);

    public static async Task<Result> ResolveAsync(
        string? scopeJson, Connection connection, GitHubRepositoryLookup repositories, CancellationToken ct)
    {
        JsonObject? scope;
        try
        {
            scope = JsonNode.Parse(string.IsNullOrWhiteSpace(scopeJson) ? "{}" : scopeJson) as JsonObject;
        }
        catch (JsonException ex)
        {
            return new(null, $"Invalid scope JSON: {ex.Message}");
        }

        if (scope is null)
            return new(null, "The scope must be a JSON object naming owner and repo.");

        string owner = scope["owner"]?.GetValueKind() == JsonValueKind.String ? scope["owner"]!.GetValue<string>() : "";
        string repo = scope["repo"]?.GetValueKind() == JsonValueKind.String ? scope["repo"]!.GetValue<string>() : "";
        if (!GitHubRepositoryForm.TryParse($"{owner}/{repo}", out owner, out repo))
            return new(null, "A GitHub source's scope needs \"owner\" and \"repo\", as in {\"owner\":\"my-org\",\"repo\":\"handbook\"}.");

        if (ConnectionForm.FromConnection(connection).GitHubInstallationId is not { } installationId)
            return new(null, $"Connection '{connection.Name}' names no GitHub App installation.");

        GitHubRepositoryInfo? found;
        try
        {
            found = await repositories.FindAsync(owner, repo, installationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, $"Could not look the repository up on GitHub. {GitHubMessages.Describe(ex, connection.Name)}");
        }

        if (found is null)
            return new(null, $"The installation behind connection '{connection.Name}' can't see {owner}/{repo}. If it exists, add it to the installation on GitHub.");

        return new(Apply(scope, found).ToJsonString(), null);
    }

    /// <summary>Writes GitHub's answer into the scope, over anything the caller put there.</summary>
    public static JsonObject Apply(JsonObject scope, GitHubRepositoryInfo found)
    {
        scope["repoId"] = found.Id;
        if (found.IsPublic) scope.Remove("private");
        else scope["private"] = true;
        return scope;
    }
}
