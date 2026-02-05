namespace AIKnowledge.Core;

/// <summary>
/// Result of a connection test to an external service.
/// </summary>
public record ConnectionTestResult
{
    /// <summary>
    /// Whether the connection test succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Human-readable message describing the result.
    /// Success: "Connected to Ollama 0.1.30 (3 models available)"
    /// Failure: "Connection failed: Connection refused"
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional structured details about the connection.
    /// Examples: { "version": "0.1.30", "modelCount": 3 }
    ///          { "error": "System.Net.Http.HttpRequestException: Connection refused" }
    /// </summary>
    public Dictionary<string, object>? Details { get; init; }

    /// <summary>
    /// Duration of the connection test.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    public static ConnectionTestResult CreateSuccess(string message, Dictionary<string, object>? details = null, TimeSpan? duration = null) =>
        new()
        {
            Success = true,
            Message = message,
            Details = details,
            Duration = duration
        };

    public static ConnectionTestResult CreateFailure(string message, Dictionary<string, object>? details = null, TimeSpan? duration = null) =>
        new()
        {
            Success = false,
            Message = message,
            Details = details,
            Duration = duration
        };
}
