using Hangfire;
using Microsoft.Extensions.Configuration;

namespace Connapse.Background;

/// <summary>
/// How ingestion work is split between Hangfire servers, read once from configuration at startup.
/// </summary>
/// <remarks>
/// A document at or above <see cref="LargeDocumentBytes"/> goes to its own queue, served by a server with
/// <see cref="LargeWorkers"/> workers. Before CON-354 every document shared one queue, and four 492-page PDFs held
/// all four workers for over an hour while small uploads waited at 0 chunks. Now large documents can only ever take
/// the large lane's workers, and the <see cref="Workers"/> of the general lane are always left for everything else.
/// A small upload still shares the embedding provider with the large documents, but the embedding throttle hands
/// out its slots one request at a time, so it gets a turn between their batches rather than after them.
/// <para>
/// Size in bytes is the only measure known before a file is parsed. It is a proxy for the text a file holds: a
/// scanned PDF is large in bytes for little text, and is merely sent to the lane with fewer workers.
/// </para>
/// <para>
/// The parser pool stays sized by <c>Hangfire:IngestionWorkerCount</c> alone: with the defaults, large documents
/// can hold at most two of its four hosts, and adding hosts for the large lane would cost memory for parses that
/// mostly wait on embedding anyway.
/// </para>
/// </remarks>
/// <param name="Workers">Documents below the threshold ingested at once (<c>Hangfire:IngestionWorkerCount</c>).</param>
/// <param name="LargeWorkers">Large documents ingested at once (<c>Hangfire:LargeIngestionWorkerCount</c>).</param>
/// <param name="LargeDocumentBytes">Size from which a document is large (<c>Hangfire:LargeDocumentBytes</c>).</param>
public sealed record IngestionLanes(
    int Workers,
    int LargeWorkers = IngestionLanes.DefaultLargeWorkers,
    long LargeDocumentBytes = IngestionLanes.DefaultLargeDocumentBytes)
{
    public const int DefaultWorkers = 4;

    /// <summary>
    /// Two, the default number of ingestion requests the embedding throttle lets through at once: large documents
    /// spend nearly all their time embedding, so two of them already keep the provider's ingestion lane busy.
    /// </summary>
    public const int DefaultLargeWorkers = 2;

    /// <summary>
    /// 2 MiB. A text PDF of a few dozen pages is well under it; the 492-page reports that caused CON-354 are above.
    /// </summary>
    public const long DefaultLargeDocumentBytes = 2L * 1024 * 1024;

    public static IngestionLanes FromConfiguration(IConfiguration configuration) => new(
        PositiveSetting(configuration, "Hangfire:IngestionWorkerCount", DefaultWorkers),
        PositiveSetting(configuration, "Hangfire:LargeIngestionWorkerCount", DefaultLargeWorkers),
        PositiveSetting(configuration, "Hangfire:LargeDocumentBytes", DefaultLargeDocumentBytes));

    /// <summary>Whether a document of <paramref name="sizeBytes"/> goes to the large lane.</summary>
    public bool IsLarge(long sizeBytes) => sizeBytes >= LargeDocumentBytes;

    /// <summary>The options of each Hangfire server that processes ingestion work.</summary>
    public IReadOnlyList<BackgroundJobServerOptions> ServerOptions(string serverNamePrefix) =>
    [
        new BackgroundJobServerOptions
        {
            WorkerCount = Workers,
            Queues = [Jobs.JobQueues.Ingestion, Jobs.JobQueues.Default],
            ServerName = $"{serverNamePrefix}:ingestion",
        },
        // Only the large queue: a server that also listened on the general queue would let large documents
        // and small ones compete for the same workers again.
        new BackgroundJobServerOptions
        {
            WorkerCount = LargeWorkers,
            Queues = [Jobs.JobQueues.IngestionLarge],
            ServerName = $"{serverNamePrefix}:ingestion-large",
        },
    ];

    private static T PositiveSetting<T>(IConfiguration configuration, string key, T defaultValue)
        where T : struct, System.Numerics.INumber<T>
    {
        T value = configuration.GetValue<T?>(key) ?? defaultValue;
        if (value <= T.Zero)
            throw new InvalidOperationException($"{key} must be a positive integer.");
        return value;
    }
}
