using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Which documents a reader may open right now. The link layer asks it on every read and never keeps the
/// answer, so revoked access takes effect on the next query.
/// </summary>
public interface IDocumentAccess
{
    /// <summary>
    /// The documents among <paramref name="documentIds"/> that <paramref name="userId"/> may read. Throws
    /// when it cannot tell; the caller must then show nothing.
    /// </summary>
    Task<IReadOnlySet<string>> ReadableAsync(Guid? userId, IReadOnlyCollection<string> documentIds, CancellationToken ct = default);
}

/// <summary>
/// The read side of the link layer. Every implementation must pass the permission-leak contract
/// (<c>Connapse.Integration.Tests.Links.LinkReaderContract</c>):
/// <list type="bullet">
/// <item>a node is visible only when one of its names comes from a readable document; a link only when one
/// of its evidence documents is readable and both its ends are visible;</item>
/// <item>everything shown (kinds, names, summaries, evidence, sources) comes from readable documents, and the
/// sources listed are exactly the readable ones;</item>
/// <item>a walk moves only through visible nodes and links, so it never reaches a node by way of a
/// document the reader cannot see;</item>
/// <item>access is checked on every call, and when it cannot be checked the answer is
/// <see cref="LinkExpansion.DeniedResult"/>.</item>
/// </list>
/// </summary>
public interface ILinkReader
{
    Task<LinkExpansion> ExpandAsync(LinkExpandRequest request, CancellationToken ct = default);
}
