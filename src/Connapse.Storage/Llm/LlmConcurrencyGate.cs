using Connapse.Core;
using Microsoft.Extensions.Options;

namespace Connapse.Storage.Llm;

/// <summary>
/// Process-wide concurrency limiter for local LLM inference (Ollama).
///
/// A single local Ollama instance serializes generation internally, so firing many
/// concurrent /api/chat requests at once just builds a deep internal queue: each request's
/// wall-clock becomes (queue position x per-call generation time), and the slowest tail
/// requests exceed HttpClient.Timeout and surface as TaskCanceledException. This gate makes
/// callers wait in-process — before the HTTP request starts — so each request that actually
/// reaches Ollama runs alone and finishes well within the timeout.
///
/// Registered as a singleton so the <see cref="SemaphoreSlim"/> is shared across all
/// (transient) provider instances. The limit comes from
/// <see cref="LlmSettings.MaxConcurrentRequests"/> (default 1) and is read once at
/// construction; changing it requires a restart. Only the Ollama provider acquires this
/// gate — cloud providers handle concurrency server-side and are not gated.
/// </summary>
public sealed class LlmConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public LlmConcurrencyGate(IOptions<LlmSettings> options)
    {
        int limit = Math.Max(1, options.Value.MaxConcurrentRequests);
        _semaphore = new SemaphoreSlim(limit, limit);
    }

    /// <summary>
    /// Waits for a slot, then returns a handle that releases the slot when disposed.
    /// Honors cancellation while waiting.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Guard against double-release (e.g. an iterator disposed more than once).
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
