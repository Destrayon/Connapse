using Connapse.Core;
using OpenAI.Embeddings;

namespace Connapse.Storage.Vectors;

/// <summary>The size the OpenAI and Azure OpenAI providers ask for, and the check that they got it.</summary>
internal static class EmbeddingDimensions
{
    /// <summary>
    /// Asks for <see cref="EmbeddingSettings.Dimensions"/> when a size is set and the model can shorten its
    /// vectors: text-embedding-3 always can, any other model when <see cref="EmbeddingSettings.SendDimensions"/> is on.
    /// </summary>
    public static EmbeddingGenerationOptions? Options(EmbeddingSettings settings)
    {
        bool supported = settings.SendDimensions
            || settings.Model.StartsWith("text-embedding-3", StringComparison.OrdinalIgnoreCase);
        return supported && settings.Dimensions > 0
            ? new EmbeddingGenerationOptions { Dimensions = settings.Dimensions }
            : null;
    }

    /// <summary>Throws when a vector is not the configured size, so it is never stored.</summary>
    public static void Check(EmbeddingSettings settings, string modelId, float[] vector)
    {
        if (settings.Dimensions > 0 && vector.Length != settings.Dimensions)
            throw new EmbeddingDimensionMismatchException(modelId, settings.Dimensions, vector.Length, Options(settings) is not null);
    }
}
