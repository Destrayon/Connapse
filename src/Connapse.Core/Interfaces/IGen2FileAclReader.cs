using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>
/// Reads a single ADLS Gen2 <b>file's own</b> access ACL, with Connapse's Data-Reader identity, to
/// evaluate a searcher's Read on the leaf (the ancestor-traverse resolver checks only directories).
/// Returns <c>null</c> when the file cannot be read or the ACL is structurally incomplete — an
/// uncertain answer the caller treats as fail-closed. On a non-HNS (flat) account the underlying
/// call fails and this returns <c>null</c>, which is the correct drop for a flat blob reached only
/// by the broad retrieval over-approximation.
/// </summary>
public interface IGen2FileAclReader
{
    Task<Gen2Acl?> ReadFileAclAsync(Gen2Path file, CancellationToken ct = default);
}
