using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class EmbeddingPromptsTests
{
    [Theory]
    [InlineData("nomic-embed-text")]
    [InlineData("nomic-embed-text:latest")]
    [InlineData("nomic-embed-text:v1.5")]
    [InlineData("nomic-embed-text:137m-v1.5-fp16")]
    [InlineData("NOMIC-EMBED-TEXT")]
    [InlineData("nomic-ai/nomic-embed-text-v1.5")]
    [InlineData("hf.co/nomic-ai/nomic-embed-text-v1.5-GGUF:Q8_0")]
    [InlineData("nomic-embed-text-v2-moe")]
    public void ForModel_NomicNames_ResolveNomicPrompts(string model)
    {
        EmbeddingPrompts.ForModel(model).Should().Be(new EmbeddingPrompts("search_query: ", "search_document: "));
    }

    [Theory]
    [InlineData("text-embedding-3-small")]
    [InlineData("text-embedding-ada-002")]
    [InlineData("my-azure-deployment")]
    [InlineData("bge-m3")]
    [InlineData("all-minilm:l6-v2")]
    [InlineData("granite-embedding:278m")]
    [InlineData("nomic-embed-text-experimental")]
    [InlineData("")]
    [InlineData(null)]
    public void ForModel_ModelsWithoutPublishedPrompts_ResolveNone(string? model)
    {
        EmbeddingPrompts.ForModel(model).Should().Be(EmbeddingPrompts.None);
    }

    [Theory]
    [InlineData("mxbai-embed-large:latest", "Represent this sentence for searching relevant passages: ", "")]
    [InlineData("snowflake-arctic-embed:335m", "Represent this sentence for searching relevant passages: ", "")]
    [InlineData("snowflake-arctic-embed2:568m", "query: ", "")]
    [InlineData("intfloat/multilingual-e5-large", "query: ", "passage: ")]
    [InlineData("embeddinggemma:300m", "task: search result | query: ", "title: none | text: ")]
    [InlineData("qwen3-embedding:0.6b", "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:", "")]
    public void ForModel_OtherAsymmetricFamilies_ResolveTheirPublishedStrings(string model, string query, string document)
    {
        EmbeddingPrompts.ForModel(model).Should().Be(new EmbeddingPrompts(query, document));
    }

    [Fact]
    public void Resolve_ModelPrefixes_IgnoreCustomOnes()
    {
        // Saved settings turn a null prefix into "", so custom prefixes must not count unless asked for.
        EmbeddingSettings settings = new() { Model = "nomic-embed-text", QueryPrefix = "", DocumentPrefix = "" };

        EmbeddingPrompts.Resolve(settings).Should().Be(new EmbeddingPrompts("search_query: ", "search_document: "));
    }

    [Fact]
    public void Resolve_CustomPrefixes_ApplyAsGiven()
    {
        EmbeddingSettings settings = new()
        {
            Model = "custom-model", UseModelPrefixes = false, QueryPrefix = "query: ", DocumentPrefix = null,
        };

        EmbeddingPrompts.Resolve(settings).Should().Be(new EmbeddingPrompts("query: ", ""));
    }

    [Fact]
    public void Resolve_CustomPrefixesEmpty_TurnPromptsOff()
    {
        EmbeddingSettings settings = new() { Model = "nomic-embed-text", UseModelPrefixes = false };

        EmbeddingPrompts.Resolve(settings).IsNone.Should().BeTrue();
    }

    [Theory]
    [InlineData(EmbeddingInputType.Query, "search_query: hello")]
    [InlineData(EmbeddingInputType.Document, "search_document: hello")]
    [InlineData(EmbeddingInputType.Unspecified, "hello")]
    public void Apply_PrependsTheSidesPrompt(EmbeddingInputType inputType, string expected)
    {
        EmbeddingPrompts.ForModel("nomic-embed-text").Apply(["hello"], inputType).Should().Equal(expected);
    }

    [Fact]
    public void Identity_WithoutPrompts_IsTheBareModelName()
    {
        EmbeddingIdentity.For(new EmbeddingSettings { Model = "text-embedding-3-small" }).Should().Be("text-embedding-3-small");
        EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text", UseModelPrefixes = false })
            .Should().Be("nomic-embed-text");
    }

    [Fact]
    public void Identity_WithPrompts_IsStableAndChangesWithThePrompts()
    {
        string first = EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text" });
        string again = EmbeddingIdentity.For(new EmbeddingSettings { Model = "nomic-embed-text" });
        string other = EmbeddingIdentity.For(new EmbeddingSettings
        {
            Model = "nomic-embed-text", UseModelPrefixes = false, QueryPrefix = "q: ", DocumentPrefix = "search_document: ",
        });

        first.Should().MatchRegex("^nomic-embed-text-prompts-[0-9a-f]{8}$");
        again.Should().Be(first);
        other.Should().NotBe(first);
    }
}
