using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Reads a single ADLS Gen2 directory's <b>access</b> ACL, with Connapse's own (Data Reader)
/// identity, to evaluate a searcher's traverse rights. Two calls so the resolver can short-circuit:
/// the cheap mode-bits read decides on its own whenever the requester owns the directory or the ACL
/// is not extended; only otherwise is the full ACL fetched. Both return <c>null</c> when the
/// directory cannot be read — an uncertain answer the caller treats as fail-closed.
/// </summary>
public interface IGen2DirectoryReader
{
    Task<Gen2ModeBits?> ReadModeBitsAsync(Gen2Path directory, CancellationToken ct = default);
    Task<Gen2Acl?> ReadAccessAclAsync(Gen2Path directory, CancellationToken ct = default);
}
