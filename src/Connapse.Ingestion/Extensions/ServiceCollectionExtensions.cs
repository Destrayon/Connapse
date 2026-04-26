using Connapse.Core.Interfaces;
using Connapse.Ingestion.Chunking;
using Connapse.Ingestion.Parsers;
using Connapse.Ingestion.Pipeline;
using Connapse.Ingestion.Reindex;
using Connapse.Ingestion.Utilities;
using Connapse.Ingestion.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Connapse.Ingestion.Extensions;

/// <summary>
/// Extension methods for registering ingestion services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all document ingestion services:
    /// - Document parsers (Text, PDF, Office)
    /// - Chunking strategies (FixedSize, Recursive, Semantic)
    /// - Ingestion pipeline and queue
    /// - Reindex service
    /// - Background worker
    /// <summary>
    /// Registers all services required for document ingestion into the provided DI container.
    /// </summary>
    /// <summary>
    /// Registers services required for document ingestion into the provided service collection.
    /// </summary>
    /// <returns>The same <see cref="IServiceCollection"/> instance with ingestion-related services registered.</returns>
    public static IServiceCollection AddDocumentIngestion(this IServiceCollection services)
    {
        services.AddSingleton<ITokenCounter, TiktokenTokenCounter>();
        services.AddSingleton<ISentenceSegmenter, PragmaticSentenceSegmenter>();

        // Register document parsers
        services.AddSingleton<IDocumentParser, TextParser>();
        services.AddSingleton<IDocumentParser, PdfParser>();
        services.AddSingleton<IDocumentParser, OfficeParser>();
        services.AddSingleton<IFileTypeValidator, FileTypeValidator>();

        // Register chunking strategies
        services.AddSingleton<IChunkingStrategy, FixedSizeChunker>();
        // Register RecursiveChunker as both itself and IChunkingStrategy so SemanticChunker
        // can take a concrete dependency on it for oversize-fallback sub-splitting.
        services.AddSingleton<RecursiveChunker>();
        services.AddSingleton<IChunkingStrategy>(sp => sp.GetRequiredService<RecursiveChunker>());
        services.AddTransient<IChunkingStrategy, SemanticChunker>(); // Transient because it depends on IEmbeddingProvider

        // Register ingestion queue (singleton for shared state)
        services.AddSingleton<IIngestionQueue, IngestionQueue>(sp => new IngestionQueue(capacity: 1000));

        // Register embedding cache
        services.AddScoped<EmbeddingCache>();

        // Register ingestion pipeline
        services.AddScoped<IKnowledgeIngester, IngestionPipeline>();

        // Register reindex service
        services.AddScoped<IReindexService, ReindexService>();

        // Register background worker
        services.AddHostedService<IngestionWorker>();

        return services;
    }
}
