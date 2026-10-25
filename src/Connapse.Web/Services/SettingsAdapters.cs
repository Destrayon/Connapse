using Connapse.Application.Ports;
using Connapse.Application.Settings;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
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

/// <summary>
/// Runs a re-index on its own scope after the caller has gone, recording in <see cref="ReindexStateService"/>
/// when it started and how it ended, which settings.reindexStatus reports.
/// </summary>
public sealed class BackgroundReindex(IServiceScopeFactory scopes, ReindexStateService runs, ILogger<BackgroundReindex> logger) : IBackgroundReindex
{
    public void Start(ReindexOptions options)
    {
        runs.MarkStarted();
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IReindexService>().ReindexAsync(options, CancellationToken.None);
                runs.MarkCompleted();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background reindex operation failed");
                runs.MarkFailed(ex.Message);
            }
        });
    }
}

/// <summary>Each provider's connection tester, as the Storage layer registers them.</summary>
public sealed class ConnectionTests(
    OllamaConnectionTester ollama, OpenAiConnectionTester openAi, AzureOpenAiConnectionTester azureOpenAi,
    OpenAiLlmConnectionTester openAiLlm, AzureOpenAiLlmConnectionTester azureOpenAiLlm, AnthropicConnectionTester anthropic,
    TeiConnectionTester tei, CohereConnectionTester cohere, JinaConnectionTester jina, AzureAIFoundryConnectionTester azureAIFoundry,
    VoyageConnectionTester voyage, MinioConnectionTester minio) : IConnectionTests
{
    public Task<ConnectionTestResult> TestAsync(ConnectionTesterKind kind, object settings, TimeSpan? timeout, CancellationToken ct)
    {
        IConnectionTester tester = kind switch
        {
            ConnectionTesterKind.Ollama => ollama,
            ConnectionTesterKind.OpenAiEmbedding => openAi,
            ConnectionTesterKind.AzureOpenAiEmbedding => azureOpenAi,
            ConnectionTesterKind.OpenAiLlm => openAiLlm,
            ConnectionTesterKind.AzureOpenAiLlm => azureOpenAiLlm,
            ConnectionTesterKind.Anthropic => anthropic,
            ConnectionTesterKind.Tei => tei,
            ConnectionTesterKind.Cohere => cohere,
            ConnectionTesterKind.Jina => jina,
            ConnectionTesterKind.AzureAIFoundry => azureAIFoundry,
            ConnectionTesterKind.Voyage => voyage,
            ConnectionTesterKind.Minio => minio,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return tester.TestConnectionAsync(settings, timeout, ct);
    }
}
