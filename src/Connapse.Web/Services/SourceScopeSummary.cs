using System.Text.Json.Nodes;

namespace Connapse.Web.Services;

/// <summary>
/// Renders a source's scope JSON as one short human-readable line for the Sources tab.
/// <para>
/// A scope names a bucket, blob container, or directory — infrastructure, not content. An
/// operator needs to see what a source points at to recognise it; what is *inside* that scope
/// is never shown, which is the distinction the whole connector/source split rests on.
/// </para>
/// <para>
/// Note this is a UI concern only. The REST surface deliberately omits <c>ScopeJson</c>
/// entirely, because a reader over the API has no equivalent need and the detail is useful for
/// reconnaissance.
/// </para>
/// </summary>
public static class SourceScopeSummary
{
    /// <summary>
    /// Summarizes the scope, or returns null when there is nothing meaningful to show. Never
    /// throws: a scope that cannot be parsed is a display problem, not a reason to fail the page.
    /// </summary>
    public static string? Describe(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson)) return null;

        JsonObject? node;
        try
        {
            node = JsonNode.Parse(scopeJson)?.AsObject();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }

        if (node is null) return null;

        // The container-ish key differs per provider, and only one is ever present.
        string? container = Str(node, "bucketName") ?? Str(node, "containerName");
        string? prefix = Str(node, "prefix");
        string? subPath = Str(node, "subPath");

        string? scope = container is not null
            ? Join(container, prefix)
            : subPath;

        if (scope is null) return null;

        // Patterns change what a source picks up as much as the path does, so an operator
        // comparing two sources on the same directory needs to see them.
        string? patterns = Patterns(node);
        return patterns is null ? scope : $"{scope} ({patterns})";
    }

    private static string Join(string container, string? prefix) =>
        string.IsNullOrWhiteSpace(prefix)
            ? container
            : $"{container.TrimEnd('/')}/{prefix.Trim('/')}";

    private static string? Patterns(JsonObject node)
    {
        var include = StringArray(node, "includePatterns");
        var exclude = StringArray(node, "excludePatterns");

        var parts = new List<string>();
        if (include.Count > 0) parts.Add(string.Join(", ", include));
        if (exclude.Count > 0) parts.Add("excluding " + string.Join(", ", exclude));

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static List<string> StringArray(JsonObject node, string name)
    {
        var values = new List<string>();
        if (node[name] is not JsonArray arr) return values;

        // TryGetValue rather than GetValue: a stored array holding a number would otherwise
        // throw out of a display helper and take the page down over cosmetic detail.
        foreach (var element in arr)
        {
            if (element is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static string? Str(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : null;
}
