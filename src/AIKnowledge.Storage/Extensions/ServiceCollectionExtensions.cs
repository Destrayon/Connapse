using AIKnowledge.Core;
using AIKnowledge.Core.Interfaces;
using AIKnowledge.Storage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AIKnowledge.Storage.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAIKnowledgeStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KnowledgeFileSystemOptions>(
            configuration.GetSection(KnowledgeFileSystemOptions.SectionName));

        services.AddSingleton<IKnowledgeFileSystem, LocalKnowledgeFileSystem>();

        return services;
    }
}
