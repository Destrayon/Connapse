namespace Connapse.Core.Interfaces;

/// <summary>
/// Optional hook invoked before an ingestion job is processed.
/// Deployments can register an implementation to set up execution context
/// (e.g., scoped services, environment state) before any DB or storage calls.
/// </summary>
public interface IIngestionJobInitializer
{
    void Initialize(IngestionJob job);
}
