using Connapse.Storage.Vectors;
using FluentAssertions;

namespace Connapse.Core.Tests.Vectors;

[Trait("Category", "Unit")]
public class VectorColumnManagerTests
{
    [Theory]
    [InlineData("nomic-embed-text", "idx_cv_emb_nomic_embed_text")]
    [InlineData("text-embedding-3-small", "idx_cv_emb_text_embedding_3_small")]
    [InlineData("text-embedding-ada-002", "idx_cv_emb_text_embedding_ada_002")]
    [InlineData("mxbai-embed-large", "idx_cv_emb_mxbai_embed_large")]
    public void GetIndexName_CommonModels_ReturnsExpectedName(string modelId, string expected)
    {
        VectorColumnManager.GetIndexName(modelId).Should().Be(expected);
    }

    [Fact]
    public void GetIndexName_LongModelId_TruncatesTo63Characters()
    {
        var longModelId = new string('a', 100);
        var result = VectorColumnManager.GetIndexName(longModelId);
        result.Length.Should().BeLessThanOrEqualTo(63);
        result.Should().StartWith("idx_cv_emb_");
    }

    [Fact]
    public void GetIndexName_SpecialCharacters_SanitizesToUnderscores()
    {
        var result = VectorColumnManager.GetIndexName("model/v2@beta.1");
        result.Should().Be("idx_cv_emb_model_v2_beta_1");
    }

    [Fact]
    public void GetIndexName_CollapsesConsecutiveUnderscores()
    {
        var result = VectorColumnManager.GetIndexName("model---name");
        result.Should().Be("idx_cv_emb_model_name");
    }

    [Fact]
    public void GetIndexName_TrimsLeadingTrailingUnderscores()
    {
        var result = VectorColumnManager.GetIndexName("-leading-trailing-");
        result.Should().Be("idx_cv_emb_leading_trailing");
    }

    [Fact]
    public void GetIndexName_UppercaseModelId_NormalizesToLowercase()
    {
        var result = VectorColumnManager.GetIndexName("Text-Embedding-3-Small");
        result.Should().Be("idx_cv_emb_text_embedding_3_small");
    }
}
