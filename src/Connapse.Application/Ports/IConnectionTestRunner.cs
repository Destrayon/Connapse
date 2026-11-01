using Connapse.Application.Connections;
using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Ports;

/// <summary>
/// What to test: a stored connection as stored (<see cref="Settings"/> null), a stored connection with settings
/// about to replace its own, or an unsaved one (<see cref="Stored"/> null). <see cref="ProbeTarget"/> is the bucket,
/// container or location an S3 or Azure test reads, when the connection's own allowed locations name none.
/// </summary>
public sealed record ConnectionTestSubject(
    string Name, ConnectionProvider Provider, ConnectionSettings? Settings, Connection? Stored, string? ProbeTarget = null);

/// <summary>
/// How a test went. <see cref="Result"/> is what the provider's service said; null when the test could not run, and
/// then <see cref="Problem"/> says why. <see cref="Health"/> is set when the test exercised exactly the stored
/// settings and credential, so it is what the connection's health now is.
/// </summary>
public sealed record ConnectionTestRun(ConnectionTestResult? Result, ConnectionHealth? Health = null)
{
    public ConnectionStatus Refusal { get; init; } = ConnectionStatus.Invalid;

    public string? Problem { get; init; }

    public string? Field { get; init; }

    public static ConnectionTestRun Refuse(string problem, string? field = null, ConnectionStatus refusal = ConnectionStatus.Invalid) =>
        new(Result: null) { Problem = problem, Field = field, Refusal = refusal };
}

/// <summary>
/// Tests a connection against its provider's live service, as the Connections page's Test button does. A stored
/// secret is only ever used with the stored settings it was saved for: a test of different settings needs its own.
/// </summary>
public interface IConnectionTestRunner
{
    Task<ConnectionTestRun> TestAsync(ConnectionTestSubject subject, CancellationToken ct = default);
}
