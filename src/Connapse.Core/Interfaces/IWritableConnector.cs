namespace Connapse.Core.Interfaces;

/// <summary>
/// A connector whose backing store Connapse owns and may mutate. Implemented only by
/// managed storage. External connectors deliberately do not implement this: a source
/// mirrors someone else's system, so "read-only" is enforced by the type rather than
/// by a runtime check that every call site has to remember to make.
/// </summary>
public interface IWritableConnector : IConnector
{
    Task WriteFileAsync(string path, Stream content, string? contentType = null, CancellationToken ct = default);
    Task DeleteFileAsync(string path, CancellationToken ct = default);
}
