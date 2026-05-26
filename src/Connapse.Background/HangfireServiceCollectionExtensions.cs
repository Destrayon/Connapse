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
            .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromSeconds(2),
                InvisibilityTimeout = TimeSpan.FromMinutes(30)
            }));

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

        services.AddSingleton<IDashboardAuthorizationFilter, HangfireDashboardAuthFilter>();

        return services;
    }
}
