using Microsoft.Extensions.DependencyInjection;

namespace AIKnowledge.Core.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAIKnowledgeCore(this IServiceCollection services)
    {
        // Core registrations (shared services, options validation)
        return services;
    }
}
