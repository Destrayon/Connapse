namespace Connapse.Background.Jobs;

/// <summary>
/// Hangfire queue name constants. Job classes reference these via [Queue] attribute.
/// </summary>
public static class JobQueues
{
    public const string Ingestion = "ingestion";
    public const string Default = "default";
}
