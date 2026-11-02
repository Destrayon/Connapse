using System.Text.Json;

namespace Connapse.Core;

/// <summary>
/// Which audit entries to read: every filter left null matches everything. <paramref name="Since"/> is
/// inclusive and <paramref name="Until"/> exclusive, both UTC. <paramref name="Before"/> continues from a
/// page's last entry, so entries written meanwhile neither repeat nor shift the next page.
/// </summary>
public sealed record AuditQuery(
    string? Action = null,
    Guid? UserId = null,
    string? ResourceType = null,
    string? ResourceId = null,
    DateTime? Since = null,
    DateTime? Until = null,
    AuditCursor? Before = null,
    int Take = 50);

/// <summary>Where a page of audit entries ended: its last entry's time and id, the log's order.</summary>
public sealed record AuditCursor(DateTime CreatedAt, Guid Id)
{
    /// <summary>The cursor as the API hands it out: opaque, URL-safe.</summary>
    public override string ToString() =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{CreatedAt.Ticks}:{Id:N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryParse(string? text, out AuditCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(text))
            return false;
        try
        {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')));
            string[] parts = decoded.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long ticks)
                && ticks is >= 0 and <= 3155378975999999999 && Guid.TryParseExact(parts[1], "N", out Guid id))
            {
                cursor = new AuditCursor(new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException)
        {
            // Not base64: not a cursor this API handed out.
        }
        return false;
    }
}

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

/// <summary>A page of audit entries, newest first, and the cursor for the next when more follow.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, AuditCursor? Next);
