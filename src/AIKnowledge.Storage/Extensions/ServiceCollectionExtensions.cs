using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Storage.Data;
using AIKnowledge.Storage.Documents;
using AIKnowledge.Storage.FileSystem;
using AIKnowledge.Storage.Settings;
using AIKnowledge.Storage.Vectors;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIKnowledge.Storage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAIKnowledgeStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // PostgreSQL + pgvector
        services.AddDbContext<KnowledgeDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.UseVector()));

        // Settings store
        services.AddScoped<ISettingsStore, PostgresSettingsStore>();

        // Settings reload service
        services.AddSingleton<SettingsReloadService>();

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

        // Document store
        services.AddScoped<IDocumentStore, PostgresDocumentStore>();

        // Vector store
        services.AddScoped<IVectorStore, PgVectorStore>();

        return services;
    }
}
