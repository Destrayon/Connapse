using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Connapse.Storage.Http;

/// <summary>
/// Retry and circuit-breaker policy for HTTP clients that call a model provider — a local
/// Ollama, a reranker service.
/// </summary>
/// <remarks>
/// One quick retry covers the brief faults: a 429, a 5xx, a dropped connection. It is kept short
/// because these clients serve searches, which a user is waiting on; ingestion has its own,
/// slower retries on top. The circuit breaker covers the long faults: once most recent calls have
/// failed, further calls fail at once for a while instead of each waiting out a timeout, so
/// ingestion jobs back off as transient failures and a search that depends on a dead reranker
/// falls back straight away. There is deliberately no per-attempt timeout here; each client's own
/// timeout, taken from settings, still bounds a call.
/// </remarks>
public static class ProviderResilience
{
    public static IHttpClientBuilder AddProviderResilience(this IHttpClientBuilder builder, string name)
    {
        builder.AddResilienceHandler(name, pipeline => pipeline
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(200),
            })
            .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 8,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30),
            }));
        return builder;
    }
}
