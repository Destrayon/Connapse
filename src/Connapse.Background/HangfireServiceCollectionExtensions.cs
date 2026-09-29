using Connapse.Core.Interfaces;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Connapse.Background;

public static class HangfireServiceCollectionExtensions
{
    /// <summary>
    /// Registers Hangfire infrastructure: storage (PostgreSQL), server (worker pools),
    /// dashboard authorization filter. Job-class registrations are added separately.
    /// </summary>
    public static IServiceCollection AddConnapseHangfire(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "DefaultConnection string is required for Hangfire's PostgreSQL storage.");

        // Cap Hangfire's own Npgsql pool. Hangfire draws connections from the global string-keyed
        // pool for this connection string, independent of the app's NpgsqlDataSource pool. Left
        // unspecified, Npgsql defaults Maximum Pool Size to 100 — and stacked on the app pool
        // against Postgres's own max_connections ceiling, that let concurrent rollups exhaust
        // connections ("too many clients already"). Overridable per deployment; default suits a
        // single-process box where the app pool also needs headroom under the same ceiling.
        int hangfireMaxPool = configuration.GetValue<int?>("Hangfire:MaxPoolSize") ?? 30;
        if (hangfireMaxPool <= 0)
            throw new InvalidOperationException("Hangfire:MaxPoolSize must be a positive integer.");
        string hangfireConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = hangfireMaxPool
        }.ConnectionString;

        services.AddHangfire(opt => opt
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            // Replace Hangfire's default AspNetCoreLogProvider with a no-op. The default
            // captures the host's ILoggerFactory into the process-wide static
            // GlobalConfiguration.Configuration; any second IHost in the same process
            // (e.g., WebApplicationFactory.WithWebHostBuilder in integration tests)
            // overwrites the static with its own factory reference. When that host
            // disposes, the static reference dangles and every Hangfire worker dequeue
            // hits ObjectDisposedException("LoggerFactory"). See NoOpHangfireLogProvider
            // for full rationale.
            .UseLogProvider(new NoOpHangfireLogProvider())
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(hangfireConnectionString), new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(2),
                InvisibilityTimeout = TimeSpan.FromMinutes(30)
            }));

        // BackgroundJobServer startup can be suppressed via Hangfire:DisableServer=true.
        // Used by integration-test derived factories (WebApplicationFactory.WithWebHostBuilder)
        // that share the parent fixture's Postgres queue — starting a second BackgroundJobServer
        // in those derived hosts corrupts Hangfire's STATIC LogProvider on dispose, causing
        // ObjectDisposedException("LoggerFactory") in the parent fixture's still-running workers.
        // Production callers leave this unset; only test code sets it.
        bool disableServer = configuration.GetValue<bool>("Hangfire:DisableServer", defaultValue: false);
        if (!disableServer)
        {
            // Two pools, so neither kind of work can take the other's workers: a burst of slow LLM
            // summaries cannot stall ingestion, and a large sync cannot starve summaries. Each
            // running job holds a database connection, so both stay small; more ingestion workers
            // than the embedding throttle's ingestion lane only overlaps download and parsing with
            // embedding, and does not load the provider further.
            //
            // The default queue — short recurring sweeps — rides with ingestion rather than with a
            // single summary worker that may be busy for minutes on one LLM call.
            int ingestionWorkers = PositiveSetting(configuration, "Hangfire:IngestionWorkerCount", 4);
            int summaryWorkers = PositiveSetting(configuration, "Hangfire:SummaryWorkerCount", 1);

            services.AddHangfireServer(opt =>
            {
                opt.WorkerCount = ingestionWorkers;
                opt.Queues = [Jobs.JobQueues.Ingestion, Jobs.JobQueues.Default];
                opt.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}:ingestion";
            });
            services.AddHangfireServer(opt =>
            {
                opt.WorkerCount = summaryWorkers;
                opt.Queues = [Jobs.JobQueues.Summarization];
                opt.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}:summarization";
            });
        }

        services.AddSingleton<IDashboardAuthorizationFilter, HangfireDashboardAuthFilter>();

        // Job-class registrations — Hangfire activator resolves these via this IServiceProvider.
        services.AddScoped<Jobs.IIngestionJobs, Jobs.IngestionJobs>();
        services.AddScoped<Jobs.ISummaryJobs, Jobs.SummaryJobs>();

        // Bridge the legacy IIngestionQueue API onto Hangfire. Replaces the in-memory
        // Channel-based queue + IngestionWorker that previously lived in Connapse.Ingestion.
        services.AddSingleton<IIngestionQueue, Storage.HangfireIngestionQueue>();

        return services;
    }

    private static int PositiveSetting(IConfiguration configuration, string key, int defaultValue)
    {
        int value = configuration.GetValue<int?>(key) ?? defaultValue;
        if (value <= 0)
            throw new InvalidOperationException($"{key} must be a positive integer.");
        return value;
    }
}
