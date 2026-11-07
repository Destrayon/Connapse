namespace Connapse.Core;

/// <summary>Where the embedding model of the Ollama that ships with Compose has got to.</summary>
public enum LocalModelState
{
    /// <summary>Embeddings don't come from the bundled Ollama, so there is nothing to wait for.</summary>
    NotManaged,
    Checking,
    Downloading,
    Ready,

    /// <summary>The download keeps failing. It is still being retried.</summary>
    Failed,

    /// <summary>The bundled Ollama is switched off and nothing else is set up to embed.</summary>
    NoProvider,
}

/// <summary>What the app knows about the bundled embedding model right now.</summary>
public sealed record LocalModelStatus(LocalModelState State, string? Model = null, int? Percent = null)
{
    public static readonly LocalModelStatus NotManaged = new(LocalModelState.NotManaged);

    /// <summary>True while embedding can't work yet, so work that needs it should wait.</summary>
    public bool HoldsEmbedding => State is LocalModelState.Checking or LocalModelState.Downloading
        or LocalModelState.Failed or LocalModelState.NoProvider;

    /// <summary>What to tell a person, or null when there's nothing to say.</summary>
    public string? Explanation => State switch
    {
        LocalModelState.Checking => "Connapse is getting its search model ready.",
        LocalModelState.Downloading when Percent is { } percent =>
            $"Connapse is still downloading its search model ({percent}%). Search and new uploads start working when it finishes.",
        LocalModelState.Downloading => "Connapse is still downloading its search model. Search and new uploads start working when it finishes.",
        LocalModelState.Failed => "Connapse can't download its search model yet and keeps trying. Check that this server can reach the internet and has free disk space; the web container's logs say what went wrong.",
        LocalModelState.NoProvider => "The local model is switched off and no embedding provider is set up. Choose one in Settings, under Embedding.",
        _ => null,
    };
}
