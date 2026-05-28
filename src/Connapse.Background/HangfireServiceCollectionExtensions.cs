using Connapse.Core.Interfaces;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
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
            services.AddHangfireServer(opt =>
            {
                opt.WorkerCount = Environment.ProcessorCount * 2;
                opt.Queues = new[]
                {
                    Jobs.JobQueues.Ingestion,
                    Jobs.JobQueues.Summarization,
                    Jobs.JobQueues.Default
                };
                opt.ServerName = $"{Environment.MachineName}:{Environment.ProcessId}";
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
}
