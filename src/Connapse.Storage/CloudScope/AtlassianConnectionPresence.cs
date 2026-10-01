namespace Connapse.Storage.CloudScope;

/// <summary>
/// Whether this deployment has any Atlassian connection, remembered for the life of the process.
/// The last known answer never expires: the TTL only says when to ask the database again, so a
/// search never falls back to "unknown" and flips how much it retrieves.
/// <para>
/// A singleton because the verifier that reads it is scoped per request. Adding a site marks it
/// present at once, so the in-process window between creating a site and indexing its first page
/// is closed rather than left to the next refresh.
/// </para>
/// </summary>
public sealed class AtlassianConnectionPresence(TimeProvider time)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private const int Unknown = 0, Absent = 1, Present = 2;

    private int _state = Unknown;
    private long _recordedAt;
    private int _refreshing;
    private long _marks;

    /// <summary>Null until the first answer; the last answer after that, however old.</summary>
    public bool? Known => Volatile.Read(ref _state) switch
    {
        Absent => false,
        Present => true,
        _ => null,
    };

    /// <summary>True when the last answer is older than <see cref="RefreshInterval"/>, or there is none.</summary>
    public bool RefreshDue =>
        Volatile.Read(ref _state) == Unknown
        || time.GetElapsedTime(Interlocked.Read(ref _recordedAt)) >= RefreshInterval;

    /// <summary>
    /// Claims the refresh so concurrent searches don't all list connections at once. A caller that
    /// loses keeps using <see cref="Known"/>; the winner must finish with <see cref="CompleteRefresh"/>
    /// or <see cref="AbandonRefresh"/>.
    /// </summary>
    public bool TryBeginRefresh(out long ticket)
    {
        ticket = Interlocked.Read(ref _marks);
        return Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0;
    }

    public void CompleteRefresh(long ticket, bool anyAtlassianConnection)
    {
        // A site added while the list was being read is newer than the list: keep "present".
        if (anyAtlassianConnection || Interlocked.Read(ref _marks) == ticket)
            Record(anyAtlassianConnection);
        AbandonRefresh();
    }

    public void AbandonRefresh() => Volatile.Write(ref _refreshing, 0);

    /// <summary>Called when a site is added, so its pages are verified from the first search on.</summary>
    public void MarkPresent()
    {
        Interlocked.Increment(ref _marks);
        Record(true);
    }

    private void Record(bool any)
    {
        Interlocked.Exchange(ref _recordedAt, time.GetTimestamp());
        Volatile.Write(ref _state, any ? Present : Absent);
    }
}
