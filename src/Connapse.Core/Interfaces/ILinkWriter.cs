using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// The write side of the link layer. A document's links are replaced as a whole, so re-indexing a document never
/// leaves a link it no longer asserts, and deleting a document removes everything it asserted.
/// </summary>
public interface ILinkWriter
{
    Task ReplaceAsync(IReadOnlyList<DocumentLinks> documents, CancellationToken ct = default);
}

/// <summary>
/// Which nodes documents stand for, and the reverse. Unfiltered: callers pass only documents a scoped search
/// admitted, and check the documents they get back before showing them.
/// </summary>
public interface ILinkIndex
{
    /// <summary>
    /// Whether any document stands for a node. May answer from a short-lived cache: a write clears it at once, but a
    /// delete or another server's write can leave the old answer for a few seconds.
    /// </summary>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>For each document among <paramref name="documentIds"/> that stands for a node, the nodes.</summary>
    Task<ILookup<string, string>> NodesOfAsync(IReadOnlyCollection<string> documentIds, CancellationToken ct = default);

    /// <summary>For each node among <paramref name="nodeIds"/>, the documents that stand for it.</summary>
    Task<ILookup<string, string>> DocumentsOfAsync(IReadOnlyCollection<string> nodeIds, CancellationToken ct = default);
}
