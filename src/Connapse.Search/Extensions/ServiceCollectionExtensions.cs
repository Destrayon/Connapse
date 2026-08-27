using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search.Hybrid;
using Connapse.Search.Keyword;
using Connapse.Search.Reranking;
using Connapse.Search.Vector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // The default is no filtering, so registering the enforcement path changes nothing.
        // A deployment opts in by replacing this with a resolver that resolves something.
        services.TryAddScoped<ISearchScopeResolver, UnrestrictedScopeResolver>();

        // Register rerankers
        services.AddScoped<ISearchReranker, CrossEncoderReranker>();

        // Named HttpClient for cross-encoder providers (TEI, Cohere, Jina)
        services.AddHttpClient("CrossEncoder");

        // Register hybrid search as the main IKnowledgeSearch implementation
        services.AddScoped<IKnowledgeSearch, HybridSearchService>();

        return services;
    }
}
