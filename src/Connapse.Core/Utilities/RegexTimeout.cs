using System.Text.RegularExpressions;

namespace Connapse.Core.Utilities;

/// <summary>
/// Gives every regular expression in the process a match timeout unless it sets its own.
/// <para>
/// PragmaticSegmenterNet builds 46 regexes with no timeout, and one of them backtracks without
/// end on long digit runs: a Prague timetable PDF in olmOCR-bench held its document in Processing
/// forever, burning a core, and no cancellation token reaches inside a regex match (#595). With a
/// default timeout the match throws <see cref="RegexMatchTimeoutException"/> instead, which the
/// segmenter catches.
/// </para>
/// </summary>
public static class RegexTimeout
{
    public const string AppContextKey = "REGEX_DEFAULT_MATCH_TIMEOUT";

    /// <summary>Generous for any honest pattern; a match that needs longer is itself the bug.</summary>
    public static readonly TimeSpan ProcessDefault = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sets the process-wide default. Must run before anything touches <see cref="Regex"/>: .NET
    /// reads the value once, when the type initialises, and only accepts a <see cref="TimeSpan"/>
    /// object, so it cannot come from runtimeconfig.json.
    /// </summary>
    public static void ApplyProcessDefault()
    {
        if (AppContext.GetData(AppContextKey) is null)
            AppDomain.CurrentDomain.SetData(AppContextKey, ProcessDefault);
    }
}
