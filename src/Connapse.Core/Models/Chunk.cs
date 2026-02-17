namespace Connapse.Core;

public record Chunk(
    string Id,
    string DocumentId,
    string Content,
    int Index,
    Dictionary<string, string> Metadata);
