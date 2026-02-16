using AIKnowledge.Core.Interfaces;
using AIKnowledge.Search.Hybrid;
using AIKnowledge.Search.Keyword;
using AIKnowledge.Search.Reranking;
using AIKnowledge.Search.Vector;
using Microsoft.Extensions.DependencyInjection;

namespace AIKnowledge.Search.Extensions;

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
