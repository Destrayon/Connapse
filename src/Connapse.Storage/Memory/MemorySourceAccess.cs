using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Storage.Memory;

/// <summary>
/// Which sources a reader can read now: a document when they may read it, a link node when it stands for at least one
/// document and they may read every one of them, since one node can span files with different permissions.
/// </summary>
internal static class MemorySourceAccess
{
    /// <summary>The sources among <paramref name="sources"/> that <paramref name="userId"/> can read now. Throws when it cannot tell.</summary>
    public static async Task<IReadOnlySet<MemorySource>> ReadableAsync(
        IDocumentAccess access, ILinkIndex links, IEnumerable<MemorySource> sources, Guid? userId, CancellationToken ct)
    {
        MemorySource[] all = [.. sources.Distinct()];
        string[] nodes = [.. all.Where(s => s.Kind == MemorySourceKind.Node).Select(s => s.Ref).Distinct(StringComparer.Ordinal)];
        ILookup<string, string> nodeDocuments = await links.DocumentsOfAsync(nodes, ct);

        HashSet<string> documents = new(StringComparer.Ordinal);
        documents.UnionWith(all.Where(s => s.Kind == MemorySourceKind.Document).Select(s => s.Ref));
        foreach (string node in nodes)
            documents.UnionWith(nodeDocuments[node]);

        // Asked even when empty: a failing check must deny, not let an empty answer through.
        IReadOnlySet<string> open = await access.ReadableAsync(userId, documents, ct);
        return all.Where(s => s.Kind == MemorySourceKind.Document
                ? open.Contains(s.Ref)
                : nodeDocuments[s.Ref].Any() && nodeDocuments[s.Ref].All(open.Contains))
            .ToHashSet();
    }
}
