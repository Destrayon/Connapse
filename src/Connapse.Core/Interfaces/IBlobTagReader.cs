using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>Reads a blob's index tags (with Connapse's Data-Reader identity) for live ABAC tag
/// verification. <c>null</c> on any read failure (fail closed); an empty map is a valid "no tags".</summary>
public interface IBlobTagReader
{
    Task<IReadOnlyDictionary<string, string>?> ReadTagsAsync(Gen2Path blob, CancellationToken ct = default);
}
