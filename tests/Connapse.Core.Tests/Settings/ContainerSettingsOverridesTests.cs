using System.Text.Json;
using Connapse.Core;
using FluentAssertions;

namespace Connapse.Core.Tests.Settings;

[Trait("Category", "Unit")]
public class ContainerSettingsOverridesTests
{
    [Fact]
    public void Default_AllPropertiesNull()
    {
        var overrides = new ContainerSettingsOverrides();

        overrides.Chunking.Should().BeNull();
        overrides.Embedding.Should().BeNull();
        overrides.Search.Should().BeNull();
        overrides.Upload.Should().BeNull();
    }

    [Fact]
    public void JsonRoundTrip_PreservesValues()
    {
        var original = new ContainerSettingsOverrides
        {
            Chunking = new ChunkingSettings { Strategy = "FixedSize", MaxChunkSize = 1024 },
            Embedding = new EmbeddingSettings { Provider = "OpenAI", Model = "text-embedding-3-small" },
            Search = new SearchSettings { Mode = "Keyword", TopK = 20 },
            Upload = new UploadSettings { ParallelWorkers = 8 }
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ContainerSettingsOverrides>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Chunking!.Strategy.Should().Be("FixedSize");
        deserialized.Chunking.MaxChunkSize.Should().Be(1024);
        deserialized.Embedding!.Provider.Should().Be("OpenAI");
        deserialized.Embedding.Model.Should().Be("text-embedding-3-small");
        deserialized.Search!.Mode.Should().Be("Keyword");
        deserialized.Search.TopK.Should().Be(20);
        deserialized.Upload!.ParallelWorkers.Should().Be(8);
    }

    [Fact]
    public void PartialOverride_OnlyPopulatesSpecified()
    {
        var overrides = new ContainerSettingsOverrides
        {
            Embedding = new EmbeddingSettings { Provider = "OpenAI", Model = "text-embedding-3-large" }
        };

        overrides.Embedding.Should().NotBeNull();
        overrides.Chunking.Should().BeNull();
        overrides.Search.Should().BeNull();
        overrides.Upload.Should().BeNull();
    }

    [Fact]
    public void WithExpression_ReturnsNewRecordWithOverride()
    {
        var original = new ContainerSettingsOverrides
        {
            Chunking = new ChunkingSettings { Strategy = "Semantic" }
        };

        var updated = original with
        {
            Embedding = new EmbeddingSettings { Provider = "AzureOpenAI" }
        };

        updated.Chunking!.Strategy.Should().Be("Semantic", "original chunking preserved");
        updated.Embedding!.Provider.Should().Be("AzureOpenAI", "new embedding set");
        original.Embedding.Should().BeNull("original unchanged");
    }

    [Fact]
    public void NullOverride_MeansUseGlobal()
    {
        var containerOverrides = new ContainerSettingsOverrides
        {
            Embedding = new EmbeddingSettings { Provider = "OpenAI", Model = "text-embedding-3-small" }
            // Chunking is null — should fall back to global
        };

        var globalChunking = new ChunkingSettings { Strategy = "Recursive", MaxChunkSize = 512 };

        // Simulate the resolver logic: use override if non-null, else global
        var effectiveChunking = containerOverrides.Chunking ?? globalChunking;
        var effectiveEmbedding = containerOverrides.Embedding ?? new EmbeddingSettings();

        effectiveChunking.Strategy.Should().Be("Recursive", "fell back to global");
        effectiveChunking.MaxChunkSize.Should().Be(512, "fell back to global");
        effectiveEmbedding.Provider.Should().Be("OpenAI", "used container override");
    }
}
