using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.CloudScope;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors;
using Connapse.Storage.Data;
using Connapse.Storage.Containers;
using Connapse.Storage.Documents;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Folders;
using Connapse.Storage.Settings;
using Connapse.Storage.Llm;
using Connapse.Storage.Vectors;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Connapse.Storage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConnapseStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // PostgreSQL + pgvector with dynamic JSON support
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson(); // Required for Dictionary<string, string> serialization
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<KnowledgeDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()));

        // Factory for short-lived per-operation contexts (required for Blazor Server and background services
        // to avoid concurrent DbContext access on the same scoped instance).
        services.AddDbContextFactory<KnowledgeDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()), ServiceLifetime.Scoped);

        // Settings store
        services.AddScoped<ISettingsStore, PostgresSettingsStore>();

        // Per-container settings resolver
        services.AddScoped<IContainerSettingsResolver, ContainerSettingsResolver>();

        // Settings reload service - requires IConfigurationRoot to trigger reload
        services.AddSingleton<ISettingsReloader, SettingsReloader>();

        // Local file system (kept for non-Docker dev)
        services.Configure<KnowledgeFileSystemOptions>(
            configuration.GetSection(KnowledgeFileSystemOptions.SectionName));

        // MinIO (S3-compatible object storage)
        services.Configure<MinioOptions>(
            configuration.GetSection(MinioOptions.SectionName));

        var minioConfig = configuration
            .GetSection(MinioOptions.SectionName)
            .Get<MinioOptions>() ?? new MinioOptions();

        var scheme = minioConfig.UseSSL ? "https" : "http";

        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            new BasicAWSCredentials(minioConfig.AccessKey, minioConfig.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = $"{scheme}://{minioConfig.Endpoint}",
                ForcePathStyle = true
            }));

        services.AddSingleton<MinioFileSystem>();
        services.AddSingleton<LocalKnowledgeFileSystem>();

        // Default IKnowledgeFileSystem: use MinIO when configured, otherwise local
        if (!string.IsNullOrEmpty(minioConfig.AccessKey))
            services.AddSingleton<IKnowledgeFileSystem>(sp => sp.GetRequiredService<MinioFileSystem>());
        else
            services.AddSingleton<IKnowledgeFileSystem>(sp => sp.GetRequiredService<LocalKnowledgeFileSystem>());

        // Embedding providers — resolved at runtime based on EmbeddingSettings.Provider
        services.AddHttpClient<OllamaEmbeddingProvider>();
        services.AddScoped<OpenAiEmbeddingProvider>();
        services.AddScoped<AzureOpenAiEmbeddingProvider>();
        services.AddScoped<IEmbeddingProvider>(sp =>
        {
            var settings = sp.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue;
            return settings.Provider switch
            {
                "OpenAI" => sp.GetRequiredService<OpenAiEmbeddingProvider>(),
                "AzureOpenAI" => sp.GetRequiredService<AzureOpenAiEmbeddingProvider>(),
                _ => sp.GetRequiredService<OllamaEmbeddingProvider>()
            };
        });

        // LLM providers — resolved at runtime based on LlmSettings.Provider
        services.AddHttpClient<OllamaLlmProvider>();
        services.AddScoped<OpenAiLlmProvider>();
        services.AddScoped<AzureOpenAiLlmProvider>();
        services.AddScoped<AnthropicLlmProvider>();
        services.AddScoped<ILlmProvider>(sp =>
        {
            var settings = sp.GetRequiredService<IOptionsMonitor<LlmSettings>>().CurrentValue;
            return settings.Provider switch
            {
                "OpenAI" => sp.GetRequiredService<OpenAiLlmProvider>(),
                "AzureOpenAI" => sp.GetRequiredService<AzureOpenAiLlmProvider>(),
                "Anthropic" => sp.GetRequiredService<AnthropicLlmProvider>(),
                _ => sp.GetRequiredService<OllamaLlmProvider>()
            };
        });

        // Container store
        services.AddScoped<IContainerStore, PostgresContainerStore>();

        // Folder store
        services.AddScoped<IFolderStore, PostgresFolderStore>();

        // Document store
        services.AddScoped<IDocumentStore, PostgresDocumentStore>();

        // Vector store
        services.AddScoped<IVectorStore, PgVectorStore>();

        // Vector index management (partial IVFFlat indexes per embedding model)
        services.AddScoped<VectorColumnManager>();

        // Vector model discovery (cross-model search support)
        services.AddScoped<VectorModelDiscovery>();

        // Connector factory (singleton — shared S3 client and config must outlive requests)
        services.AddSingleton<ConnectorFactory>();
        services.AddSingleton<IConnectorFactory>(sp => sp.GetRequiredService<ConnectorFactory>());

        // Managed storage provider (default: MinIO — Cloud overrides with Azure Blob)
        services.AddSingleton<IManagedStorageProvider, MinioManagedStorageProvider>();

        // Connection testers
        services.AddScoped<OllamaConnectionTester>();
        services.AddScoped<MinioConnectionTester>();
        services.AddScoped<S3ConnectionTester>();
        services.AddScoped<AzureBlobConnectionTester>();
        services.AddScoped<AwsSsoConnectionTester>();
        services.AddScoped<AzureAdConnectionTester>();
        services.AddScoped<OpenAiConnectionTester>();
        services.AddScoped<AzureOpenAiConnectionTester>();
        services.AddScoped<OpenAiLlmConnectionTester>();
        services.AddScoped<AzureOpenAiLlmConnectionTester>();
        services.AddScoped<AnthropicConnectionTester>();
        services.AddScoped<TeiConnectionTester>();
        services.AddScoped<CohereConnectionTester>();
        services.AddScoped<JinaConnectionTester>();
        services.AddScoped<AzureAIFoundryConnectionTester>();
        services.AddScoped<VoyageConnectionTester>();

        // Cloud scope discovery
        services.AddScoped<ICloudIdentityProvider, AwsIdentityProvider>();
        services.AddScoped<ICloudIdentityProvider, AzureIdentityProvider>();
        services.AddSingleton<IConnectorScopeCache, ConnectorScopeCache>();

        // AWS SSO client registration and token exchange
        services.AddScoped<IAwsSsoClientRegistrar, AwsSsoClientRegistrar>();

        return services;
    }
}
