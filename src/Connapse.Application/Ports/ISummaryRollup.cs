namespace Connapse.Application.Ports;

/// <summary>
/// Queues a container's summary rollup in the background. Web answers it with Hangfire, which
/// Connapse.Application doesn't reference.
/// </summary>
public interface ISummaryRollup
{
    /// <returns>The background job's id.</returns>
    string Enqueue(Guid containerId);
}
