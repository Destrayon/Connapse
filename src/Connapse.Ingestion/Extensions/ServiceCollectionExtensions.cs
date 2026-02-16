using AIKnowledge.Core.Interfaces;
using AIKnowledge.Ingestion.Chunking;
using AIKnowledge.Ingestion.Parsers;
using AIKnowledge.Ingestion.Pipeline;
using AIKnowledge.Ingestion.Reindex;
using Microsoft.Extensions.DependencyInjection;

namespace AIKnowledge.Ingestion.Extensions;

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
    /// </summary>
    public static IServiceCollection AddDocumentIngestion(this IServiceCollection services)
    {
        // Register document parsers
        services.AddSingleton<IDocumentParser, TextParser>();
        services.AddSingleton<IDocumentParser, PdfParser>();
        services.AddSingleton<IDocumentParser, OfficeParser>();

        // Register chunking strategies
        services.AddSingleton<IChunkingStrategy, FixedSizeChunker>();
        services.AddSingleton<IChunkingStrategy, RecursiveChunker>();
        services.AddTransient<IChunkingStrategy, SemanticChunker>(); // Transient because it depends on IEmbeddingProvider

        // Register ingestion queue (singleton for shared state)
        services.AddSingleton<IIngestionQueue, IngestionQueue>(sp => new IngestionQueue(capacity: 1000));

        // Register ingestion pipeline
        services.AddScoped<IKnowledgeIngester, IngestionPipeline>();

        // Register reindex service
        services.AddScoped<IReindexService, ReindexService>();

        // Register background worker
        services.AddHostedService<IngestionWorker>();

        return services;
    }
}
