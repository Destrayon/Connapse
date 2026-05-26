using System.Text.Json;
using Connapse.Core.Interfaces;

namespace Connapse.Eval.Judges;

/// <summary>
/// Uses a separate LLM to score a generated container summary against the 8
/// agent-optimization writing rules. Manual harness; not run as a CI gate in v1.
/// </summary>
public sealed class SummaryJudge(ILlmProvider judge)
{
    private const string SystemPrompt = """
        You are a strict reviewer of agent-facing prose. You evaluate whether
        a CONTAINER DESCRIPTION is well-written for an LLM agent consumer.
        """;

    public async Task<JudgeResult> EvaluateAsync(
        string corpusName, string summary, CancellationToken ct = default)
    {
        string prompt = $"""
            You are evaluating a CONTAINER DESCRIPTION generated for an LLM agent.
            Score the summary on these 8 binary criteria (0 or 1 each):

            1. Names what + when (specific entities, scope, time range): __
            2. Pushy against undertriggering (enumerates trigger terms): __
            3. Concrete trigger terms (not abstract categories): __
            4. States what's NOT covered (negative scope): __
            5. Defines or namespaces specialized terms: __
            6. Uses imperative voice ("Use when…", "Does not cover…"): __
            7. Briefs like a new hire (decision-oriented, third person): __
            8. Lists answerable questions: __

            Output JSON format: scores array, total count, notes string.

            Summary to evaluate (for corpus '{corpusName}'):
            ---
            {summary}
            ---
            """;

        string responseText = await judge.CompleteAsync(SystemPrompt, prompt, options: null, ct);
        try
        {
            return JsonSerializer.Deserialize<JudgeResult>(responseText)
                   ?? new JudgeResult(new int[8], 0, "(failed to parse judge response)");
        }
        catch (JsonException)
        {
            return new JudgeResult(new int[8], 0, "(failed to parse judge response)");
        }
    }

    public sealed record JudgeResult(int[] Scores, int Total, string Notes);
}
