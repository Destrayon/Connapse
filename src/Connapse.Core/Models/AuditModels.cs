using System.Text.Json;

namespace Connapse.Core;

/// <summary>
/// Which audit entries to read: every filter left null matches everything. <paramref name="Since"/> is
/// inclusive and <paramref name="Until"/> exclusive, both UTC.
/// </summary>
public sealed record AuditQuery(
    string? Action = null,
    Guid? UserId = null,
    string? ResourceType = null,
    string? ResourceId = null,
    DateTime? Since = null,
    DateTime? Until = null,
    int Skip = 0,
    int Take = 50);

/// <summary>One audit entry: what was done, by whom, to what, and when. <see cref="UserEmail"/> is null once the user is deleted.</summary>
public sealed record AuditEntry(
    Guid Id,
    DateTime CreatedAt,
    string Action,
    Guid? UserId,
    string? UserEmail,
    string? ResourceType,
    string? ResourceId,
    JsonElement? Details,
    string? IpAddress);

/// <summary>A page of audit entries, newest first, and whether more follow.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, bool HasMore);
