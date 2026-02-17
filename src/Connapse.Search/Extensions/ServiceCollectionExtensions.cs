using Connapse.Core.Interfaces;
using Connapse.Search.Hybrid;
using Connapse.Search.Keyword;
using Connapse.Search.Reranking;
using Connapse.Search.Vector;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Search.Extensions;

/// <summary>
/// Extension methods for registering search services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all search-related services including vector search, keyword search,
    /// rerankers, and hybrid search.
    /// </summary>
    public static IServiceCollection AddKnowledgeSearch(this IServiceCollection services)
    {
        // Register individual search services
        services.AddScoped<VectorSearchService>();
        services.AddScoped<KeywordSearchService>();

        // Register rerankers
        services.AddScoped<ISearchReranker, RrfReranker>();
        services.AddHttpClient<ISearchReranker, CrossEncoderReranker>();

        // Register hybrid search as the main IKnowledgeSearch implementation
        services.AddScoped<IKnowledgeSearch, HybridSearchService>();

        return services;
    }
}
