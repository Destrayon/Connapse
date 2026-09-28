using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Vectors;

/// <summary>
/// Process-wide concurrency limits on embedding requests, one lane for ingestion and one for
/// search, so that ingestion can never occupy the whole provider.
/// </summary>
/// <remarks>
/// A local Ollama serves requests roughly one batch at a time. Without a limit, a large sync
/// puts every ingestion worker's batch ahead of a user's query embedding, and the search waits
/// behind all of them. With ingestion held to a few requests, the provider always has room for
/// the query lane.
/// <para>
/// Limits come from <see cref="EmbeddingSettings"/> and are re-read on each request: a changed
/// limit replaces that lane's semaphore, and requests already holding the old one finish on it.
/// </para>
/// </remarks>
public sealed class EmbeddingThrottle(IOptionsMonitor<EmbeddingSettings> settings)
{
    private readonly Lane _ingestion = new();
    private readonly Lane _query = new();

    /// <summary>
    /// Waits for a slot in the lane <paramref name="inputType"/> belongs to — documents on the
    /// ingestion lane, everything else on the query lane — and returns a handle that frees it.
    /// </summary>
    public Task<IDisposable> AcquireAsync(EmbeddingInputType inputType, CancellationToken ct)
    {
        EmbeddingSettings current = settings.CurrentValue;
        return inputType == EmbeddingInputType.Document
            ? _ingestion.AcquireAsync(current.MaxConcurrentIngestionRequests, ct)
            : _query.AcquireAsync(current.MaxConcurrentQueryRequests, ct);
    }

    private sealed class Lane
    {
        private readonly object _swap = new();
        private (int Limit, SemaphoreSlim Semaphore)? _current;

        public async Task<IDisposable> AcquireAsync(int configuredLimit, CancellationToken ct)
        {
            SemaphoreSlim semaphore = SemaphoreFor(Math.Max(1, configuredLimit));
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(semaphore);
        }

        private SemaphoreSlim SemaphoreFor(int limit)
        {
            lock (_swap)
            {
                // Never disposed on a swap: requests holding the old semaphore release into it.
                if (_current is not { } current || current.Limit != limit)
                    _current = (limit, new SemaphoreSlim(limit, limit));
                return _current.Value.Semaphore;
            }
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}

/// <summary>
/// Wraps the configured embedding provider so every request passes through
/// <see cref="EmbeddingThrottle"/>.
/// </summary>
public sealed class ThrottledEmbeddingProvider(IEmbeddingProvider inner, EmbeddingThrottle throttle) : IEmbeddingProvider
{
    public int Dimensions => inner.Dimensions;
    public string ModelId => inner.ModelId;

    public async Task<float[]> EmbedAsync(string text, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        using var slot = await throttle.AcquireAsync(inputType, ct).ConfigureAwait(false);
        return await inner.EmbedAsync(text, inputType, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IEnumerable<string> texts, EmbeddingInputType inputType, CancellationToken ct = default)
    {
        using var slot = await throttle.AcquireAsync(inputType, ct).ConfigureAwait(false);
        return await inner.EmbedBatchAsync(texts, inputType, ct).ConfigureAwait(false);
    }
}
