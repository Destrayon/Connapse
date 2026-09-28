# How should Connapse apply embedding-model query/document prefixes across its embedding providers?
**Date:** 2026-09-28
**Status:** Reviewed
**Built on:** retrieval-eval-findings (memory), hybrid-fusion-improvements-2026-09-27.md; dev-suite baseline in docs/plans/fusion-dev-suite-552.md

## Executive summary
Connapse embeds queries and documents through one `IEmbeddingProvider` call with no notion of intent, so its default model, nomic-embed-text v1.5 via Ollama, runs without the `search_query: ` / `search_document: ` prefixes its authors say "must" be used. No Ollama package adds prefixes, and no OpenAI-shaped `/v1/embeddings` server (OpenAI, Azure, Ollama, vLLM, LM Studio, llama.cpp, TEI's `/v1`) accepts an intent field, so the client must prepend them. The proven design, used by sentence-transformers, LlamaIndex, Onyx and Dify, is: callers declare intent (query or document) through the interface; a per-model prompt table turns intent into prefix text; unknown models get no prefix; user overrides win. Because prefixes change the vector space, the prompt recipe must become part of the stored vector identity so the existing reindex and model-id filter keep old and new vectors apart. The size of the gain is poorly measured upstream (only Qwen3 quantifies it, at 1–5%; one field report found nomic's query prefix hurt a short-text corpus), so it must be measured on the Connapse dev suite before becoming the default.

## Research brief
**Question:** How should Connapse add embedding instruction prefixes so that each model gets its correct query/document treatment, modularly, without changing behaviour for embedders that don't need prefixes?
**Sub-questions:** (1) the official conventions for open models served through Ollama/TEI/vLLM; (2) how hosted APIs and OpenAI-compatible servers express intent; (3) how mature frameworks structure it; (4) measured impact, migration of existing vectors, and knock-on effects.
**Out of scope:** changing the default embedding model; fusion tuning itself (#552 continues after this).
**Success criteria:** a concrete design for Connapse with an evaluation plan.

## Connapse today: where embeddings are made
All embedding goes through `IEmbeddingProvider.EmbedAsync/EmbedBatchAsync(texts)` (Core), implemented by `OllamaEmbeddingProvider`, `OpenAiEmbeddingProvider` (with a configurable base URL, so any OpenAI-compatible server) and `AzureOpenAiEmbeddingProvider`. Four call sites:
- `VectorSearchService` — the search query (**query** intent).
- `IngestionPipeline` — chunk text (**document**), behind an embedding cache keyed by (content hash, model id, dimensions).
- `SemanticChunker` — embeds context-windowed sentences to find boundaries, **and mean-pools those same vectors into the stored chunk vectors** (`PrecomputedEmbedding`). They are therefore document vectors and must use the document prefix; comparing them to each other stays symmetric because both sides carry the same prefix.
- `DocumentSummaryEmbeddingProvider` — per-document summaries searched by queries (**document**).

Stored vectors carry `model_id` = the configured model name; search filters on it, and the reindex endpoint re-embeds documents whose `model_id` differs from the configured model (with `EnableCrossModelSearch` as a keyword fallback meanwhile). Ollama runs nomic with `num_ctx` 8192 (model trained at 2048); chunks are at most 512, so a prefix of a few tokens never forces truncation.

## Open-model conventions (sub-question 1)
The authoritative, machine-readable source is the `prompts` field in each model's `config_sentence_transformers.json` on Hugging Face; where absent, the model card. Strings must be stored byte-exact: most end in a space, Qwen3's `Query:` does not, instruct models contain a real newline. No Ollama library model (nomic-embed-text, v2-moe, mxbai-embed-large, snowflake-arctic-embed/2, bge-m3, bge-large, embeddinggemma, qwen3-embedding, all-minilm, granite-embedding) adds a prefix: their manifests have no template layer and `/api/embed` tokenizes the input directly.

| Model | Query | Document | Evidence |
|---|---|---|---|
| nomic-embed-text v1 / v1.5 / v2-moe | `search_query: ` | `search_document: ` | card ("must"); v2-moe ST config |
| e5-*-v2, multilingual-e5-* | `query: ` | `passage: ` | card FAQ |
| e5-mistral-7b-instruct, multilingual-e5-large-instruct, gte-Qwen2-*-instruct | `Instruct: {task}\nQuery: ` | none | ST config + card |
| Qwen3-Embedding 0.6B/4B/8B | `Instruct: {task}\nQuery:` | none | ST config; card: 1–5% worse without |
| bge-*-en-v1.5 | `Represent this sentence for searching relevant passages: ` | none | card (v1.5 degrades only "slightly" without) |
| mxbai-embed-large-v1, snowflake-arctic-embed v1/v1.5 | `Represent this sentence for searching relevant passages: ` | none | ST config |
| snowflake-arctic-embed v2.0 | `query: ` | none | ST config |
| EmbeddingGemma-300m | `task: search result \| query: ` | `title: none \| text: ` | Google model card + ST config |
| bge-m3, gte-*-en-v1.5, jina-v2, granite-embedding, all-MiniLM, paraphrase-* | none | none | cards (granite evidence thin) |
| jina-embeddings-v3 | LoRA task adapter + prompt | LoRA task adapter + prompt | prefix alone is insufficient; out of scope |

## Hosted APIs and OpenAI-compatible servers (sub-question 2)
No OpenAI-shaped `/v1/embeddings` surface takes an intent parameter: OpenAI, Azure OpenAI, Ollama (`/api/embed` and `/v1`), vLLM, LM Studio, llama.cpp, and TEI's `/v1` endpoint. TEI's native `/embed` accepts a per-request `prompt_name`; its server-wide `--default-prompt` would prefix queries and documents alike and silently break asymmetric models. OpenAI `text-embedding-3-*`, Azure OpenAI, Mistral and Bedrock Titan are symmetric: nothing to apply. Hosted APIs with their own intent parameter, applied server-side: Cohere `input_type` (required on v3+), Voyage `input_type` (query/document/None), Gemini `gemini-embedding-001` `task_type`, Vertex `task_type` (reported default `RETRIEVAL_QUERY`, which would mis-embed documents if omitted), Jina `task`, Nomic Atlas `task_type` (default `search_document`). `gemini-embedding-2` dropped `task_type` and expects client-side text prompts. Microsoft.Extensions.AI's `EmbeddingGenerationOptions` has no intent field (only `AdditionalProperties`, whose keys adapters handle inconsistently), so Connapse needs its own intent type.

## How frameworks structure it (sub-question 3)
The recurring design across sentence-transformers v5 (`encode_query`/`encode_document`, `prompts` dict), LlamaIndex (`_get_query_embedding`/`_get_text_embedding`), LangChain (`embed_query`/`embed_documents`), FastEmbed (`query_embed`/`passage_embed`), RAGFlow (`encode`/`encode_queries`), Onyx and Dify (an intent enum on every request):
1. Intent is declared by the caller through the interface — two methods or an enum.
2. The provider turns intent into a text prefix (local and OpenAI-compatible models) or a native API parameter (Cohere, Voyage, Gemini, Jina, Nomic); Onyx rejects a prefix sent to a hosted model with its own parameter.
3. Prefix strings come from, in order: user override (always wins; empty string = disable), a small built-in registry (LlamaIndex BGE/INSTRUCTOR lists, AnythingLLM, Onyx's UI catalog), or the model's own metadata.
4. Unknown models get no prefix, leaving symmetric models untouched.
5. Onyx ties prefixes to the index settings, so changing them is handled like changing the model.

The Ollama integrations are the consistently broken ones: LangChain, AnythingLLM, RAGFlow and Dify's plugin send nomic queries unprefixed; Haystack can prefix Ollama documents but not queries. No framework normalizes Ollama tags (`:latest`, `:v1.5`, `hf.co/...`); all match exact names. A global prefix not tied to a model (Open WebUI) goes stale when the model changes.

## Impact, migration and knock-on effects (sub-question 4)
- **Measured gain is thin.** Only Qwen3's card quantifies omitting its instruction (1–5%). The nomic technical report has no omission ablation; its MTEB runs used `search_query`/`search_document` for retrieval and `classification:` on both sides for STS. BGE v1.5 was retrained to need its instruction less. Field reports conflict: EmbeddingGemma recall@1 rose 16/25 → 23/25 with prompts; one nomic project with query-length stored texts found `search_query:` *worse* than `search_document:` on both sides (recall@5 93 → 89). Connapse must measure on its dev suite.
- **Never mix recipes.** No study measures prefixed queries against unprefixed documents; it is a pairing the model never trained on. Projects handling this refuse to query across recipes. Rule: embed each query with the recipe of the vectors it searches.
- **Versioning.** Vector stores treat any recipe change as a new vector space: Qdrant named vectors or blue-green collections with an alias switch, Weaviate alias switch, Elastic's `inference_id` compatibility check, pgai a new destination table. For Connapse the recipe (model + prompt set) should be the stored `model_id`, and the embedding-cache key follows it.
- **Thresholds shift.** Prefixes move cosine-score distributions: `SemanticChunker` breakpoint thresholds and any `MinScore` need re-checking.
- **Fusion.** Gains from adding BM25 shrink as the dense model improves (one patent-retrieval study found the best dense weight 0.7–0.9). Fix the embeddings before retuning fusion α.

## Recommended design for Connapse
1. **Intent in Core.** Add `enum EmbeddingInputType { Document, Query }` and make it a required parameter on `IEmbeddingProvider.EmbedAsync/EmbedBatchAsync`, so all four call sites state intent at compile time (query: `VectorSearchService`; document: pipeline, `SemanticChunker`, summaries). A symmetric purpose is not needed yet because the chunker's vectors are stored as document vectors.
2. **Prompt resolution, one place.** `EmbeddingPrompts(string? Query, string? Document)` resolved by an `IEmbeddingPromptResolver`: user override from `EmbeddingSettings.QueryPrefix/DocumentPrefix` (null = use registry, `""` = none) → built-in registry → none. The registry lives in Core and matches a normalized name (lowercase; strip `hf.co/`, an org prefix, and an Ollama `:tag`) against explicit family entries from the table above, with exact strings. Unknown names resolve to none, so OpenAI `text-embedding-3-*`, Azure deployments and symmetric models are unaffected.
3. **Providers apply it.** Text-prefix application is shared by the Ollama and OpenAI-compatible providers. Azure OpenAI and official OpenAI models resolve to none. Future hosted providers (Cohere, Voyage, Gemini) map `EmbeddingInputType` to their native parameter instead and never prepend text.
4. **Recipe in the vector identity.** A single `EmbeddingIdentity` computes the stored `model_id`: the bare model name when no prompts apply (so every non-prefixed deployment keeps its existing ids, cache and indexes untouched) and `model#p<short-hash-of-prompts>` when they do. Every place that reads `embedSettings.Model` as the vector id (pipeline cache and storage, `VectorSearchService.ResolveModelId`, reindex detection, settings endpoints) uses it.
5. **Upgrade path.** Existing nomic vectors keep `model_id = nomic-embed-text` and now count as outdated. Until they are re-embedded, search must embed the query with the recipe of the stored vectors (no prefix for the bare id) rather than the new recipe, so search quality never drops mid-migration; the existing reindex endpoint re-embeds. Cutover per container is when its vectors all carry the new id.
6. **Evaluate before defaulting.** Run the dev suite with prefixes on vs off (dense-only and hybrid), check `SemanticChunker` thresholds, then retune fusion α on the prefixed embeddings (#552).

## Conflicts and uncertainties
- nomic query prefix: vendor says required; one field report found it worse on a short-text corpus. Unresolved; measure.
- nomic symmetric prefix: model card suggests `clustering:`; the technical report used `classification:` for STS. Moot for Connapse under this design (chunker uses the document prefix).
- Vertex `task_type` default (`RETRIEVAL_QUERY`) and Jina's default come from secondary snippets, not rendered reference pages.
- Azure OpenAI "no intent parameter" is inferred from the shared OpenAI request shape.
- IBM granite: no prefix guidance found; treated as none.

## Gaps — what we did not find
- No published omission ablation for nomic-embed-text.
- No measurement of mixed prefixed/unprefixed indexes.
- No framework handling Ollama tag normalization — Connapse's matcher is new ground and needs unit tests on real Ollama names.
- Whether jina-v3 GGUF builds apply task LoRAs.

## Source quality assessment
The prefix strings rest on primary sources (authors' model cards and sentence-transformers configs, Ollama registry manifests and server source). API behaviour rests on official API references, with three rows on secondary snippets (flagged above). Framework findings come from reading source code. Impact evidence is the weakest: one quantified vendor figure (Qwen3), papers measuring training-time rather than inference-time instructions, and anecdotal GitHub reports — hence the dev-suite measurement step.

## Sources
**Primary:** huggingface.co model cards and `config_sentence_transformers.json` for nomic-ai/nomic-embed-text-v1.5 and v2-moe, intfloat/e5-*-v2, multilingual-e5-*, e5-mistral-7b-instruct, BAAI/bge-*-en-v1.5, bge-m3, Alibaba-NLP/gte-*, mixedbread-ai/mxbai-embed-large-v1, Snowflake/snowflake-arctic-embed-*, jinaai/jina-embeddings-v2/v3, Qwen/Qwen3-Embedding-*, ibm-granite/granite-embedding-*; https://ai.google.dev/gemma/docs/embeddinggemma/model_card; registry.ollama.ai manifests; github.com/ollama/ollama `server/routes.go`, `docs/api.md`; arXiv 2402.01613 (Nomic Embed), 2212.03533 (E5), 2401.00368 (E5-mistral); docs.cohere.com/reference/embed; docs.voyageai.com/docs/embeddings; ai.google.dev/gemini-api/docs/embeddings; docs.mistral.ai; jina.ai/embeddings; AWS Bedrock Titan and Cohere parameter pages; docs.nomic.ai; huggingface.co/docs/text-embeddings-inference; docs.vllm.ai pooling models; lmstudio.ai docs; llama.cpp server README; learn.microsoft.com EmbeddingGenerationOptions; source of sentence-transformers, llama_index, langchain, haystack-core-integrations, fastembed, onyx, open-webui, anything-llm, ragflow, dify, khoj, private-gpt; Qdrant, Weaviate, Elastic migration docs.
**Secondary:** arXiv 2210.11934 (Bruch et al., fusion); arXiv 2605.24297 (patent retrieval fusion weights); arXiv 2605.22544 (prompt sensitivity); huggingface.co/BAAI/bge-large-en-v1.5 discussion 6; microsoft/semantic-kernel#13250.
**Tertiary / anecdotal:** NodeSpaceAI/nodespace-core PR 3128; roygurner-gif/cloxy PR 4; openclaw/openclaw#140932; Xveyn/vectorgrep#70.
