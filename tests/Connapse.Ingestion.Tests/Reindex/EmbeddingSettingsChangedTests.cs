using Connapse.Core;
using Connapse.Ingestion.Pipeline;
using Connapse.Ingestion.Reindex;
using FluentAssertions;

namespace Connapse.Ingestion.Tests.Reindex;

[Trait("Category", "Unit")]
public class EmbeddingSettingsChangedTests
{
    private static EmbeddingSettings Settings(string provider, string model, int dimensions, bool send = false) =>
        new() { Provider = provider, Model = model, Dimensions = dimensions, SendDimensions = send };

    /// <summary>Metadata as ingestion wrote it; <paramref name="requested"/> null is a document indexed before #666.</summary>
    private static Dictionary<string, string> IndexedWith(EmbeddingSettings settings, bool? requested)
    {
        Dictionary<string, string> metadata = new()
        {
            [IngestionPipeline.MetadataKeyEmbeddingProvider] = settings.Provider,
            [IngestionPipeline.MetadataKeyEmbeddingModel] = EmbeddingIdentity.For(settings),
            [IngestionPipeline.MetadataKeyEmbeddingDimensions] = settings.Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (requested is { } value)
            metadata[IngestionPipeline.MetadataKeyEmbeddingDimensionsRequested] = value ? "true" : "false";
        return metadata;
    }

    [Fact]
    public void EmbeddingSettingsChanged_LegacyDocAndSendDimensionsNowOn_IsChanged()
    {
        var before = Settings("OpenAI", "Qwen/Qwen3-Embedding-8B", 1536);

        var (changed, stored, current) = ReindexService.EmbeddingSettingsChanged(IndexedWith(before, requested: null), before with { SendDimensions = true });

        changed.Should().BeTrue();
        stored.Should().Contain("native size");
        current.Should().Contain("asked for 1536 dimensions");
    }

    [Fact]
    public void EmbeddingSettingsChanged_LegacyTextEmbedding3Doc_IsUnchanged()
    {
        var settings = Settings("OpenAI", "text-embedding-3-small", 512);

        var (changed, _, _) = ReindexService.EmbeddingSettingsChanged(IndexedWith(settings, requested: null), settings);

        changed.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingSettingsChanged_RequestedSizeChanged_IsChanged()
    {
        var before = Settings("AzureOpenAI", "text-embedding-3-large", 1024);

        var (changed, _, _) = ReindexService.EmbeddingSettingsChanged(IndexedWith(before, requested: true), before with { Dimensions = 256 });

        changed.Should().BeTrue();
    }

    [Fact]
    public void EmbeddingSettingsChanged_OllamaDimensionsEdited_IsUnchanged()
    {
        // Ollama is never asked for a size, so the setting only describes the model's output.
        var before = Settings("Ollama", "nomic-embed-text", 768);

        var (changed, _, _) = ReindexService.EmbeddingSettingsChanged(IndexedWith(before, requested: false), before with { Dimensions = 512, SendDimensions = true });

        changed.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingSettingsChanged_SameSettings_IsUnchanged()
    {
        var settings = Settings("OpenAI", "Qwen/Qwen3-Embedding-8B", 1024, send: true);

        var (changed, _, _) = ReindexService.EmbeddingSettingsChanged(IndexedWith(settings, requested: true), settings);

        changed.Should().BeFalse();
    }
}
