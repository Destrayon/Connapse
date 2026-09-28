using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Connapse.Core;

/// <summary>
/// What a text is being embedded as. Asymmetric embedding models are trained with a different
/// instruction on each side (nomic's <c>search_query: </c> / <c>search_document: </c>), so every
/// caller says which side its text is on.
/// </summary>
public enum EmbeddingInputType
{
    /// <summary>Stored content: chunks, and the sentences whose vectors become chunks.</summary>
    Document,

    /// <summary>A search query compared against stored documents.</summary>
    Query,

    /// <summary>
    /// The text exactly as given, with no instruction. Used to query vectors that were stored
    /// before prompts applied, so a query always matches the recipe of the vectors it searches.
    /// </summary>
    Unspecified,
}

/// <summary>
/// The instruction text an embedding model expects before each side's input. Empty means none.
/// </summary>
public sealed record EmbeddingPrompts(string Query, string Document)
{
    public static readonly EmbeddingPrompts None = new("", "");

    public bool IsNone => Query.Length == 0 && Document.Length == 0;

    public string Apply(string text, EmbeddingInputType inputType) => inputType switch
    {
        EmbeddingInputType.Query => Query + text,
        EmbeddingInputType.Document => Document + text,
        _ => text,
    };

    public IReadOnlyList<string> Apply(IEnumerable<string> texts, EmbeddingInputType inputType) =>
        IsNone || inputType == EmbeddingInputType.Unspecified
            ? texts as IReadOnlyList<string> ?? texts.ToList()
            : texts.Select(t => Apply(t, inputType)).ToList();

    /// <summary>
    /// The prompts for the configured model: its published prompts, or the custom prefixes when
    /// <see cref="EmbeddingSettings.UseModelPrefixes"/> is off. Models the registry doesn't know get
    /// none, so symmetric models and hosted APIs are unchanged.
    /// </summary>
    public static EmbeddingPrompts Resolve(EmbeddingSettings settings) =>
        settings.UseModelPrefixes
            ? ForModel(settings.Model)
            : new EmbeddingPrompts(settings.QueryPrefix ?? "", settings.DocumentPrefix ?? "");

    /// <summary>The model's published prompts, matched on its normalised name; none when unknown.</summary>
    public static EmbeddingPrompts ForModel(string? model) =>
        Known.TryGetValue(NormalizeModelName(model), out EmbeddingPrompts? prompts) ? prompts : None;

    /// <summary>
    /// Reduces the ways a model gets named to its bare name: lowercase, without a registry host
    /// (<c>hf.co/</c>), an owner (<c>nomic-ai/</c>), an Ollama tag (<c>:latest</c>, <c>:v1.5</c>,
    /// <c>:0.6b</c>) or a <c>-gguf</c> suffix.
    /// </summary>
    public static string NormalizeModelName(string? model)
    {
        string name = (model ?? "").Trim().ToLowerInvariant();
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        int colon = name.IndexOf(':');
        if (colon >= 0)
            name = name[..colon];
        if (name.EndsWith("-gguf", StringComparison.Ordinal))
            name = name[..^"-gguf".Length];
        return name;
    }

    // Exact strings from each model's sentence-transformers config (config_sentence_transformers.json,
    // "prompts") or, where it has none, its model card. Whitespace matters: most end in a space,
    // Qwen3's does not, and the instruct models contain a real newline. Sources and the models
    // deliberately left out (bge-m3, gte-v1.5, jina-v2, granite, MiniLM: no prompts; jina-v3: needs
    // task adapters, not text) are in docs/research/embedding-instruction-prefixes-2026-09-28.md.
    private static readonly EmbeddingPrompts Nomic = new("search_query: ", "search_document: ");
    private static readonly EmbeddingPrompts E5 = new("query: ", "passage: ");
    private static readonly EmbeddingPrompts BgeQuery = new("Represent this sentence for searching relevant passages: ", "");
    private static readonly EmbeddingPrompts ArcticV2 = new("query: ", "");
    private static readonly EmbeddingPrompts EmbeddingGemma = new("task: search result | query: ", "title: none | text: ");
    private static readonly EmbeddingPrompts Qwen3 =
        new("Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:", "");
    private static readonly EmbeddingPrompts MistralStyleInstruct =
        new("Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery: ", "");

    private static readonly Dictionary<string, EmbeddingPrompts> Known = new(StringComparer.Ordinal)
    {
        ["nomic-embed-text"] = Nomic,
        ["nomic-embed-text-v1"] = Nomic,
        ["nomic-embed-text-v1.5"] = Nomic,
        ["nomic-embed-text-v2-moe"] = Nomic,
        ["e5-small-v2"] = E5,
        ["e5-base-v2"] = E5,
        ["e5-large-v2"] = E5,
        ["multilingual-e5-small"] = E5,
        ["multilingual-e5-base"] = E5,
        ["multilingual-e5-large"] = E5,
        ["bge-small-en-v1.5"] = BgeQuery,
        ["bge-base-en-v1.5"] = BgeQuery,
        ["bge-large-en-v1.5"] = BgeQuery,
        ["bge-large"] = BgeQuery, // Ollama's name for bge-large-en-v1.5
        ["mxbai-embed-large"] = BgeQuery,
        ["mxbai-embed-large-v1"] = BgeQuery,
        ["snowflake-arctic-embed"] = BgeQuery, // Ollama's name for the v1 family
        ["snowflake-arctic-embed-xs"] = BgeQuery,
        ["snowflake-arctic-embed-s"] = BgeQuery,
        ["snowflake-arctic-embed-m"] = BgeQuery,
        ["snowflake-arctic-embed-m-long"] = BgeQuery,
        ["snowflake-arctic-embed-l"] = BgeQuery,
        ["snowflake-arctic-embed-m-v1.5"] = BgeQuery,
        ["snowflake-arctic-embed2"] = ArcticV2, // Ollama's name for v2.0
        ["snowflake-arctic-embed-m-v2.0"] = ArcticV2,
        ["snowflake-arctic-embed-l-v2.0"] = ArcticV2,
        ["embeddinggemma"] = EmbeddingGemma,
        ["embeddinggemma-300m"] = EmbeddingGemma,
        ["qwen3-embedding"] = Qwen3,
        ["qwen3-embedding-0.6b"] = Qwen3,
        ["qwen3-embedding-4b"] = Qwen3,
        ["qwen3-embedding-8b"] = Qwen3,
        ["e5-mistral-7b-instruct"] = MistralStyleInstruct,
        ["multilingual-e5-large-instruct"] = MistralStyleInstruct,
        ["gte-qwen2-1.5b-instruct"] = MistralStyleInstruct,
        ["gte-qwen2-7b-instruct"] = MistralStyleInstruct,
    };
}

/// <summary>
/// Turns text into what the model is sent: its prompt for the side the text is on, then, for models
/// whose vocabulary is uncased, the lowercasing and accent stripping their tokenizer is meant to do.
/// Ollama's BERT tokenizer skips that step, so capitalised words become the wrong tokens (#561).
/// </summary>
public static class EmbeddingText
{
    /// <summary>
    /// The text each side of an embedding call is sent as. <see cref="EmbeddingInputType.Unspecified"/>
    /// sends it exactly as given: that is how vectors stored before this preparation were made.
    /// </summary>
    public static IReadOnlyList<string> Prepare(
        EmbeddingSettings settings, IEnumerable<string> texts, EmbeddingInputType inputType)
    {
        if (inputType == EmbeddingInputType.Unspecified)
            return texts as IReadOnlyList<string> ?? texts.ToList();
        IReadOnlyList<string> prompted = EmbeddingPrompts.Resolve(settings).Apply(texts, inputType);
        return IsUncased(settings.Model) ? prompted.Select(Uncase).ToList() : prompted;
    }

    /// <summary>True when the model's tokenizer is uncased (<c>do_lower_case</c> in its tokenizer_config.json).</summary>
    public static bool IsUncased(string? model) => Uncased.Contains(EmbeddingPrompts.NormalizeModelName(model));

    /// <summary>What BERT's uncased BasicTokenizer does before WordPiece: lowercase, then drop accents.</summary>
    public static string Uncase(string text)
    {
        string decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        StringBuilder result = new(decomposed.Length);
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(c);
        return result.ToString();
    }

    // Checked against each model's tokenizer_config.json (do_lower_case: true, BertTokenizer).
    // Cased models, left alone: nomic v2-moe, multilingual-e5, arctic v2, Qwen3, bge-m3, granite.
    private static readonly HashSet<string> Uncased = new(StringComparer.Ordinal)
    {
        "nomic-embed-text", "nomic-embed-text-v1", "nomic-embed-text-v1.5",
        "e5-small-v2", "e5-base-v2", "e5-large-v2",
        "bge-small-en-v1.5", "bge-base-en-v1.5", "bge-large-en-v1.5", "bge-large",
        "mxbai-embed-large", "mxbai-embed-large-v1",
        "snowflake-arctic-embed", "snowflake-arctic-embed-xs", "snowflake-arctic-embed-s", "snowflake-arctic-embed-m",
        "snowflake-arctic-embed-m-long", "snowflake-arctic-embed-l", "snowflake-arctic-embed-m-v1.5",
        "all-minilm", "all-minilm-l6-v2", "all-minilm-l12-v2",
        "gte-small-en-v1.5", "gte-base-en-v1.5", "gte-large-en-v1.5",
    };
}

/// <summary>
/// The id stored with every vector (<c>chunk_vectors.model_id</c>): which vector space it lives in.
/// How text is prepared (prompts, lowercasing) changes the space, so it is part of it. When text is
/// sent as it is, the id is the bare model name, exactly as before, so those deployments keep their
/// vectors, cache and indexes. The suffix uses only letters, digits and hyphens: <c>VectorColumnManager</c>
/// builds each model's partial index predicate from a sanitised id, and any other character would be
/// dropped from it.
/// </summary>
public static class EmbeddingIdentity
{
    public static string For(EmbeddingSettings settings) =>
        For(settings.Model, EmbeddingPrompts.Resolve(settings), EmbeddingText.IsUncased(settings.Model));

    /// <summary>
    /// Every vector space the configured model can still embed a query for, current first: the
    /// current recipe, and the model's published recipe when custom prefixes replaced it. (The bare
    /// model id, for vectors stored before any text preparation, is the third: its queries are sent
    /// exactly as given.) Vectors made with an older custom recipe are not reproducible: its prefixes
    /// are not stored anywhere, so they wait for a reindex.
    /// </summary>
    public static IReadOnlyList<EmbeddingSettings> ReproducibleRecipes(EmbeddingSettings settings)
    {
        List<EmbeddingSettings> recipes = [settings];
        HashSet<string> seen = [For(settings)];
        EmbeddingSettings published = settings with { UseModelPrefixes = true };
        if (seen.Add(For(published)))
            recipes.Add(published);
        return recipes;
    }

    public static string For(string model, EmbeddingPrompts prompts, bool uncased = false)
    {
        if (prompts.IsNone && !uncased)
            return model;
        string recipe = prompts.Query + "\0" + prompts.Document + (uncased ? "\0uncased" : "");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(recipe));
        return $"{model}-recipe-{Convert.ToHexStringLower(hash)[..8]}";
    }
}
