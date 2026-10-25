using Connapse.Application.Ports;
using Connapse.Storage.Vectors;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>A category's configured value: its registered options, or its defaults when none are registered.</summary>
public sealed class ConfiguredSettings(IServiceProvider services) : IConfiguredSettings
{
    public T Current<T>() where T : class, new() =>
        services.GetService<IOptionsMonitor<T>>()?.CurrentValue ?? new T();
}

/// <summary>
/// Rebuilds the vector indexes as a Hangfire job, which gets its own scope and is retried if it fails, so a
/// settings change neither waits for the build nor loses it.
/// </summary>
public sealed class HangfireVectorIndexReconciler(IBackgroundJobClient jobs) : IVectorIndexReconciler
{
    public void Schedule() => jobs.Enqueue<VectorColumnManager>(m => m.EnsureIndexesAsync(CancellationToken.None, false));
}
