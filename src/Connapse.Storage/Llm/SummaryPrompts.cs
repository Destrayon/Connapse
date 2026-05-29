namespace Connapse.Storage.Llm;

public static class SummaryPrompts
{
    public const string PerDocSystemPrompt = """
        You are writing a DOCUMENT DESCRIPTION that an LLM agent will read to
        decide whether to query this document's container via search_knowledge.
        The reader is an AI agent, not a human.

        Write 2-4 sentences with this structure:
        1. What the document is — entities, scope, time range. Be specific.
        2. What questions it can answer. Enumerate concrete query terms.
        3. (If clear) What it does NOT cover.

        Lead with concrete nouns and trigger terms. Avoid "this document
        covers various topics about X." Prefer:
        "Covers Apple Q3 2025 earnings: iPhone units, China revenue, services
        growth. Answers: how much did iPhone sales decline? What's the
        services margin? Does not cover product roadmap."

        Output target: ~100 tokens.
        """;

    public const string ContainerRollupSystemPrompt = """
        You are writing a CONTAINER DESCRIPTION that an LLM agent will read
        to decide whether to issue search_knowledge queries against this
        container. The reader is an AI agent, NOT a human. Your output goes
        directly into the agent's context window.

        Write 200-500 tokens of agent-steering prose with these sections:
        1. One-sentence scope: "Use this container for queries about X, Y, Z."
        2. Concrete trigger terms and synonyms the agent should pattern-match.
        3. Representative questions this container can answer (5-8 bullets).
        4. Scope boundaries: only call a topic out-of-scope when the
           summaries below actively establish it (a bounded, single-subject
           corpus). If the input says you are shown a representative SAMPLE
           rather than every document, do NOT assert what the container
           excludes — a topic missing from the sample may still be present.
           Hedge instead: "appears centered on…; may also hold related
           material."
        5. Query hints: phrasings or keywords that retrieve well.

        Be specific over generic. Name entities, not categories. Prefer
        imperative voice ("Use when…"). Brief the reader like a new hire
        who'll be making routing decisions all day. A false "does not cover
        X" is costly — it suppresses real retrievals — while omitting an
        exclusion only risks a cheap, self-correcting extra query; when
        unsure, omit it.
        """;

    public static string RenderPerDocUserMessage(
        string filename, string? mimeType, string firstNTokens) =>
        $"""
        Document: {filename}
        Content type: {mimeType ?? "unknown"}
        ---------------------
        {firstNTokens}
        ---------------------
        """;

    public static string RenderContainerRollupUserMessage(
        string containerName,
        int totalDocs,
        bool isClustered,
        IEnumerable<string> summaries)
    {
        string clusterNote = isClustered
            ? "; you are shown a representative SAMPLE — cluster medoids, with cluster sizes in brackets — NOT every document"
            : "; all documents are shown below";

        string body = string.Join("\n", summaries.Select((s, i) => $"{i + 1}. {s}"));

        return $"""
            Container: "{containerName}"
            {totalDocs} documents total{clusterNote}
            ---------------------
            {body}
            ---------------------
            """;
    }
}
