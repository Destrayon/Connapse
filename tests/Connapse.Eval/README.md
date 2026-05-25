# Connapse summary eval harness

Manual LLM-judge harness for scoring container auto-summary quality against the 8 agent-optimization writing rules. **Not a CI gate.**

## Purpose

Detect prompt-template regressions: after changing `SummaryPrompts.cs`, run this harness against the seed corpora and compare scores to the committed baseline.

## Setup

1. Configure an LLM provider (Anthropic / OpenAI / Ollama) via the standard Connapse settings hierarchy.
2. Start Docker dev services: `docker compose up -d` from the repo root.
3. Populate seed corpora under `Corpora/`. Recommended starter set:
   - `Corpora/code-repo/` — ~20 .cs files (boilerplate-heavy)
   - `Corpora/research-reports/` — ~5-10 markdown docs (prose-heavy)
   - `Corpora/multilingual/` — EN + JA + DE markdown (multi-language)
   - `Corpora/binary-pdfs/` — ~3 open-licensed PDFs (binary/scan content)

## Running

```bash
dotnet run --project tests/Connapse.Eval
```

(The current `Program.cs` is a scaffold; wire the ingest/judge loop before first run — see scaffold instructions in `Program.cs`.)

## Output

Append run results to `docs/eval/baseline-scores-<YYYY-MM-DD>.md` for regression tracking.

## What the judge scores

Per the 8 agent-optimization writing rules:

1. Names what + when (entities, scope, time range)
2. Pushy against undertriggering (trigger terms enumerated)
3. Concrete trigger terms (not abstract categories)
4. States what's NOT covered
5. Defines specialized terms
6. Imperative voice
7. Briefs like a new hire
8. Lists answerable questions

Total score out of 8 per summary; per-rule breakdown for diagnosis.
