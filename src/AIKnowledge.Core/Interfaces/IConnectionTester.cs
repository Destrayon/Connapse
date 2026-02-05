namespace AIKnowledge.Core.Interfaces;

/// <summary>
/// Tests connectivity to external services (Ollama, MinIO, cloud APIs).
/// Used by Settings page to validate configuration before saving.
/// </summary>
public interface IConnectionTester
{
    /// <summary>
    /// Test connection to the service using provided settings.
    /// Does NOT modify any state - read-only validation.
    /// </summary>
    /// <param name="settings">Settings to test (from form, not database)</param>
    /// <param name="timeout">Timeout for the test (default: 10 seconds)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Test result with success status, message, and optional details</returns>
    Task<ConnectionTestResult> TestConnectionAsync(
        object settings,
        TimeSpan? timeout = null,
        CancellationToken ct = default);
}
