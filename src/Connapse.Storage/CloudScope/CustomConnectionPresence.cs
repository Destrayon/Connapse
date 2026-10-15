namespace Connapse.Storage.CloudScope;

/// <summary>
/// Whether any custom connection exists, as the custom scope resolver last saw it. It only sizes the
/// verifier's over-fetch: the resolver lists connections on every search, and the verifier checks every
/// <c>custom://</c> hit whatever this says, so a stale answer can cost backfill but never admit a hit.
/// </summary>
public sealed class CustomConnectionPresence
{
    private const int Unknown = 0, Absent = 1, Present = 2;
    private int _state = Unknown;

    /// <summary>Null until the resolver first answers.</summary>
    public bool? Known => Volatile.Read(ref _state) switch
    {
        Absent => false,
        Present => true,
        _ => null,
    };

    public void Record(bool present) => Volatile.Write(ref _state, present ? Present : Absent);
}
