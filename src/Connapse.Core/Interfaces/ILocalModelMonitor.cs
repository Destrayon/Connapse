namespace Connapse.Core.Interfaces;

/// <summary>Reports whether the embedding model of the Ollama that ships with Compose is ready to use.</summary>
public interface ILocalModelMonitor
{
    LocalModelStatus Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes.</summary>
    event Action? Changed;
}
