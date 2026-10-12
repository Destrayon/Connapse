using Connapse.Storage.Connectors.Atlassian;
using Connapse.Storage.Connectors.GitHub;

namespace Connapse.Web.Services;

public enum SyncNoticeKind
{
    /// <summary>The provider asked Connapse to slow down; the next cycle carries on. Nothing to fix.</summary>
    Waiting,

    /// <summary>The sync finished but left something out that an administrator can fix.</summary>
    Warning,
}

/// <summary>
/// What a note on a successful sync means. A sync that succeeded can still carry a note: a rate
/// limit it is waiting out, or something it skipped (attachments over the size cap, attachments the
/// credential can't read). Only the first is a wait; the rest are warnings.
/// </summary>
public static class SourceSyncNotice
{
    public static SyncNoticeKind KindOf(string notice) =>
        notice.StartsWith(AtlassianRateLimitedException.MessagePrefix, StringComparison.Ordinal)
        || notice.StartsWith(GitHubRateLimit.NoticePrefix, StringComparison.Ordinal)
            ? SyncNoticeKind.Waiting
            : SyncNoticeKind.Warning;
}
