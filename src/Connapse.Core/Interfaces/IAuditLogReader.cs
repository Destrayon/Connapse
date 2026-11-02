using Connapse.Core;

namespace Connapse.Core.Interfaces;

/// <summary>Reads the audit log that <see cref="IAuditLogger"/> writes.</summary>
public interface IAuditLogReader
{
    /// <summary>One page of entries matching <paramref name="query"/>, newest first.</summary>
    Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct = default);
}
