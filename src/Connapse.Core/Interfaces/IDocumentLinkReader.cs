namespace Connapse.Core.Interfaces;

/// <summary>
/// One document's neighbours in the link layer (CON-214): the documents of the same container it links to and the ones
/// that link to it, as one reader may see them. Every document shown, and every link between them, is vouched for by
/// a document the reader may read; one the reader may not read is left out and not counted.
/// </summary>
public interface IDocumentLinkReader
{
    Task<DocumentLinkSet> ReadAsync(Guid? userId, Guid containerId, string documentId, CancellationToken ct = default);
}

/// <summary>
/// Another document, and the kinds of link between the two (for example "calls", "imports"), each named once and in
/// the direction of the list it is in: under "links to", this document calls that one; under "linked from", that one
/// calls this.
/// </summary>
public sealed record LinkedDocument(string Id, string FileName, string Path, IReadOnlyList<string> Types);

/// <summary>
/// What a document links to and what links to it, each ordered by path. <paramref name="Denied"/> is true when access
/// could not be checked, and then both lists are empty.
/// </summary>
public sealed record DocumentLinkSet(IReadOnlyList<LinkedDocument> LinksTo, IReadOnlyList<LinkedDocument> LinkedFrom, bool Denied = false)
{
    public static DocumentLinkSet Empty { get; } = new([], []);

    public static DocumentLinkSet DeniedResult { get; } = new([], [], Denied: true);
}
