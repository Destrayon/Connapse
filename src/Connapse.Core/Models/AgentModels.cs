using System.Text.Json;

namespace Connapse.Core;

public record ToolResult(
    bool Success,
    JsonElement? Data = null,
    string? Error = null,
    string? HumanReadable = null);

public record ToolContext(
    string? UserId,
    string? ConversationId,
    IServiceProvider Services);

public record Note(
    string Key,
    string Content,
    DateTime Created,
    DateTime Modified,
    Dictionary<string, string> Metadata);

public record NoteOptions(
    string? Category = null,
    Dictionary<string, string>? Metadata = null,
    TimeSpan? Expiry = null);
