using Hangfire.Logging;

namespace Connapse.Background;

/// <summary>
/// No-op <see cref="ILogProvider"/> for Hangfire's internal <c>LibLog</c> abstraction.
/// Replaces the default <c>AspNetCoreLogProvider</c> that Hangfire installs in its
/// <c>AddHangfire</c> default callback.
/// </summary>
/// <remarks>
/// Why this exists: <c>AspNetCoreLogProvider</c> captures the host's <c>ILoggerFactory</c>
/// reference into the process-wide static <c>GlobalConfiguration.Configuration</c>. Any
/// scenario where multiple <c>IHost</c> instances start up in the same process (integration
/// tests that use <c>WebApplicationFactory.WithWebHostBuilder</c> for per-test config
/// overrides) causes the static to be overwritten with each host's factory; when one of
/// those hosts disposes, its factory is disposed but the static reference dangles, and
/// every subsequent Hangfire worker dequeue from any remaining host hits
/// <c>ObjectDisposedException("LoggerFactory")</c>.
///
/// Trade-off: Hangfire's internal breadcrumbs (server lifecycle, worker state transitions,
/// dispatcher startup) are silently dropped. Connapse application logging via
/// <c>ILogger&lt;T&gt;</c> inside our own job classes is unaffected.
/// </remarks>
internal sealed class NoOpHangfireLogProvider : ILogProvider
{
    public ILog GetLogger(string name) => NoOpLog.Instance;

    private sealed class NoOpLog : ILog
    {
        public static readonly NoOpLog Instance = new();

        public bool Log(LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null)
        {
            // Never log; never claim the level is enabled.
            return false;
        }
    }
}
