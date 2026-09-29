using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Integration.Tests;

/// <summary>
/// Records what a sync enqueued, and stops there.
/// </summary>
/// <remarks>
/// The sync tests assert on what the service decided — upserted and deleted counts, the rows it
/// claimed, the owner a job carries. None of them need the job to run. Handing them the real
/// queue meant every sync left a Hangfire job behind that resolved a live S3 connector, and CI
/// has no AWS credentials: each one spent roughly three quarters of a second exhausting the
/// credential chain, failed, and was retried three more times.
/// <para>
/// That is background work, so it did not fail the test that created it. It failed the ones
/// that came after: with a hundred-odd of them churning through the shared ingestion queue, an
/// ordinary container upload waited nearly six minutes to be picked up and its test timed out
/// at sixty seconds. Tests that never asked for ingestion should not be able to do that.
/// </para>
/// </remarks>
internal class RecordingIngestionQueue : IIngestionQueue
{
    public List<IngestionJob> Jobs { get; } = [];

    public virtual Task<string?> EnqueueAsync(IngestionJob job, CancellationToken ct = default)
    {
        Jobs.Add(job);
        return Task.FromResult<string?>(Guid.NewGuid().ToString());
    }

    public Task<bool> CancelJobForDocumentAsync(string documentId) => Task.FromResult(false);

    public Task<int> GetQueueDepthAsync(CancellationToken ct = default) => Task.FromResult(Jobs.Count);
}
