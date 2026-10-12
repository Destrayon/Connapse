namespace Connapse.Core;

/// <summary>
/// An error whose message is written for an administrator (what happened and what to do) and whose
/// provider's own answer, such as an HTTP status and error code, is kept apart as <see cref="Detail"/>.
/// </summary>
public interface IErrorDetail
{
    /// <summary>The provider's raw answer, or null when there is none. Never carries a secret.</summary>
    string? Detail { get; }
}

/// <summary>
/// How a source's sync error is stored: the plain message, then the provider's answer after
/// <see cref="DetailMarker"/>, so the Sources page can show the message and keep the raw answer
/// behind a disclosure while the API still returns both in one string.
/// </summary>
public static class SyncErrorText
{
    public const string DetailMarker = "\nDetails: ";

    /// <summary>The text recorded for a failed sync.</summary>
    public static string Of(Exception ex) =>
        ex is IErrorDetail { Detail: { Length: > 0 } detail } ? ex.Message + DetailMarker + detail : ex.Message;

    /// <summary>Splits stored text into its message and, when present, the provider's answer.</summary>
    public static (string Message, string? Detail) Split(string error)
    {
        int at = error.IndexOf(DetailMarker, StringComparison.Ordinal);
        return at < 0 ? (error, null) : (error[..at], error[(at + DetailMarker.Length)..]);
    }
}
