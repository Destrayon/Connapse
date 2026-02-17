using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Data;
using Connapse.Storage.Containers;
using Connapse.Storage.Documents;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Folders;
using Connapse.Storage.Settings;
using Connapse.Storage.Vectors;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // Settings store
        services.AddScoped<ISettingsStore, PostgresSettingsStore>();

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

        // Embedding provider
        services.AddHttpClient<OllamaEmbeddingProvider>();
        services.AddScoped<IEmbeddingProvider, OllamaEmbeddingProvider>();

        // Container store
        services.AddScoped<IContainerStore, PostgresContainerStore>();

        // Folder store
        services.AddScoped<IFolderStore, PostgresFolderStore>();

        // Document store
        services.AddScoped<IDocumentStore, PostgresDocumentStore>();

        // Vector store
        services.AddScoped<IVectorStore, PgVectorStore>();

        // Connection testers
        services.AddScoped<OllamaConnectionTester>();
        services.AddScoped<MinioConnectionTester>();

        return services;
    }
}
