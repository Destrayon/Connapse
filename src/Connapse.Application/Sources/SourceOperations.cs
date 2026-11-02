namespace Connapse.Application.Sources;

/// <summary>
/// Every Sources operation in one place, for the pages: a page that offers several actions injects
/// this rather than each operation, so it depends on what it can do, not on how many things it does.
/// </summary>
public sealed class SourceOperations(
    ListSources list, GetSource get, UpdateSource update, CreateSource create, DeleteSource delete, SyncSource sync, RetryFailedDocuments retryFailed)
{
    public ListSources List { get; } = list;

    public GetSource Get { get; } = get;

    public UpdateSource Update { get; } = update;

    public CreateSource Create { get; } = create;

    public DeleteSource Delete { get; } = delete;

    public SyncSource Sync { get; } = sync;

    public RetryFailedDocuments RetryFailed { get; } = retryFailed;
}
