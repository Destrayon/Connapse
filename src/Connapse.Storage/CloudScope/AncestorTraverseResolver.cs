using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Decides whether an identity set holds traverse-<c>X</c> on <b>every</b> ancestor directory of a
/// candidate Gen2 file — the necessary condition (with the file's own read, checked by Phase 4e's
/// verifier) for the file to be readable. Resolves lazily per directory with the mode-bits
/// short-circuit and caches confident per-directory decisions, so candidates that share ancestors
/// share the work. Fails closed: any directory that cannot be read denies the whole traverse and is
/// never cached.
/// </summary>
public sealed class AncestorTraverseResolver(IGen2DirectoryReader reader, IMemoryCache cache)
{
    /// <summary>Cache window for a confident per-directory traverse decision; also its revocation
    /// delay. Kept short (the spec's ~30–120 s) so a changed ACL takes effect quickly.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private const string KeyPrefix = "gen2-traverse:";

    public async Task<bool> HoldsTraverseOnAllAncestorsAsync(
        Gen2Path file, IReadOnlySet<string> principals, CancellationToken ct = default)
    {
        string principalKey = string.Join(",", principals.OrderBy(p => p, StringComparer.Ordinal));

        foreach (Gen2Path dir in Ancestors(file))
        {
            string key = $"{KeyPrefix}{dir.Account}:{dir.FileSystem}:{dir.Path}:{principalKey}";
            if (cache.TryGetValue(key, out bool cached))
            {
                if (!cached) return false; // a cached definitive deny short-circuits the whole walk
                continue;
            }

            bool? decision = await ResolveDirectoryExecuteAsync(dir, principals, ct);
            if (decision is null)
                return false; // uncertain → fail closed, uncached (retried next time)

            cache.Set(key, decision.Value, CacheLifetime);
            if (!decision.Value)
                return false;
        }
        return true;
    }

    /// <summary>Whether <paramref name="principals"/> hold traverse-<c>X</c> on one directory.
    /// <c>null</c> means the directory could not be read (uncertain → the caller denies).</summary>
    private async Task<bool?> ResolveDirectoryExecuteAsync(
        Gen2Path dir, IReadOnlySet<string> principals, CancellationToken ct)
    {
        Gen2ModeBits? bits = await reader.ReadModeBitsAsync(dir, ct);
        if (bits is null)
            return null;

        // Mode bits alone are authoritative when the requester owns the directory (owner is terminal)
        // or the ACL is not extended (no named entries, no mask). Otherwise a named entry could be
        // decisive, so the full access ACL is read.
        bool ownsDir = bits.OwnerOid is not null && principals.Contains(bits.OwnerOid);
        if (ownsDir || !bits.HasExtendedAcl)
            return PosixAclEvaluator.Grants(bits.ToAcl(), principals, Gen2Permission.Execute);

        Gen2Acl? full = await reader.ReadAccessAclAsync(dir, ct);
        if (full is null)
            return null;
        return PosixAclEvaluator.Grants(full, principals, Gen2Permission.Execute);
    }

    /// <summary>The filesystem root and every directory prefix of the file, file itself excluded.</summary>
    private static IEnumerable<Gen2Path> Ancestors(Gen2Path file)
    {
        yield return file with { Path = "" }; // filesystem root

        string[] segments = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < segments.Length; i++) // exclude the last segment (the file)
            yield return file with { Path = string.Join('/', segments[..i]) };
    }
}
