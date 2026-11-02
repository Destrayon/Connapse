namespace Connapse.Application.Ports;

/// <summary>
/// What a settings category is when nothing is stored for it: the value configuration (appsettings,
/// environment variables) gives it. Web answers it from the options it registered.
/// </summary>
public interface IConfiguredSettings
{
    T Current<T>() where T : class, new();
}

/// <summary>
/// Rebuilds the vector indexes for the configured embedding model, in the background: changing the model
/// needs its index, and building one must not hold up the change.
/// </summary>
public interface IVectorIndexReconciler
{
    void Schedule();
}

/// <summary>
/// Re-embeds documents in the background, recording the run in <see cref="Settings.ReindexStateService"/>:
/// a re-index can take hours, and starting one must not hold up the caller.
/// </summary>
public interface IBackgroundReindex
{
    void Start(Connapse.Core.Interfaces.ReindexOptions options);
}

/// <summary>The tester that checks a provider's connection, one per provider and, where they differ, per kind of model.</summary>
public enum ConnectionTesterKind
{
    Ollama,
    OpenAiEmbedding,
    AzureOpenAiEmbedding,
    OpenAiLlm,
    AzureOpenAiLlm,
    Anthropic,
    Tei,
    Cohere,
    Jina,
    AzureAIFoundry,
    Voyage,
    Minio,
}

/// <summary>Runs a provider's connection tester on the settings given, which are never stored.</summary>
public interface IConnectionTests
{
    Task<Connapse.Core.ConnectionTestResult> TestAsync(ConnectionTesterKind kind, object settings, TimeSpan? timeout, CancellationToken ct);
}
