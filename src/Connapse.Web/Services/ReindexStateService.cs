namespace Connapse.Web.Services;

/// <summary>
/// Singleton that tracks the state of the most recent background reindex operation
/// triggered via POST /api/settings/reindex.
/// Exposed by GET /api/settings/reindex/status so admins can distinguish between
/// "in progress", "completed", and "failed" states.
/// </summary>
public class ReindexStateService
{
    private volatile ReindexState _state = new();

    /// <summary>Gets the current reindex state snapshot.</summary>
    public ReindexState Current => _state;

    /// <summary>Called when a background reindex is launched.</summary>
    public void MarkStarted()
    {
        _state = new ReindexState
        {
            Status = ReindexStatus.Running,
            StartedAt = DateTime.UtcNow,
            CompletedAt = null,
            LastError = null
        };
    }

    /// <summary>Called when the background reindex completes successfully.</summary>
    public void MarkCompleted()
    {
        _state = _state with
        {
            Status = ReindexStatus.Completed,
            CompletedAt = DateTime.UtcNow
        };
    }

    /// <summary>Called when the background reindex fails with an exception.</summary>
    public void MarkFailed(string error)
    {
        _state = _state with
        {
            Status = ReindexStatus.Failed,
            CompletedAt = DateTime.UtcNow,
            LastError = error
        };
    }
}

/// <summary>Immutable snapshot of the reindex operation state.</summary>
public record ReindexState
{
    public ReindexStatus Status { get; init; } = ReindexStatus.Idle;
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? LastError { get; init; }
}

/// <summary>Lifecycle states for a background reindex run.</summary>
public enum ReindexStatus
{
    /// <summary>No reindex has been triggered since startup.</summary>
    Idle,
    /// <summary>A reindex is currently running in the background.</summary>
    Running,
    /// <summary>The most recent reindex completed successfully.</summary>
    Completed,
    /// <summary>The most recent reindex failed; see LastError for details.</summary>
    Failed
}
