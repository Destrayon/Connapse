"""CON-160 summary writer: Connapse's own summary prompts, answered by Claude Haiku through `claude -p`.

The two system prompts and the two user-message templates are copied verbatim from
src/Connapse.Storage/Llm/SummaryPrompts.cs, and the per-document truncation matches PerDocSummarizer
(MaxInputTokens 5,000 x 4 characters). `test_summarize.py` checks the copies still match the C# source.

    python summarize.py doc --corpus E:/tmp/con-146/corpus --cache E:/tmp/con-160/doc-summaries.jsonl \
        "/0001166/Shadow-to-solo oncall progression.txt"     # writes (or reads) one document summary
"""
import argparse
import json
import os
import subprocess
from pathlib import Path

BELOW_NORMAL = 0x00004000
MAX_INPUT_CHARS = 5_000 * 4  # PerDocSummarizer.DefaultMaxInputTokens * 4

PER_DOC_SYSTEM_PROMPT = """You are writing a DOCUMENT DESCRIPTION that an LLM agent will read to
decide whether to query this document's container via search_knowledge.
The reader is an AI agent, not a human.

Write 2-4 sentences with this structure:
1. What the document is — entities, scope, time range. Be specific.
2. What questions it can answer. Enumerate concrete query terms.
3. What it does NOT cover — but ONLY when the full document is shown
   below. If the content is marked truncated (you see only the HEAD),
   do not claim what the document omits; a later section you can't see
   may contain it. Hedge instead ("focuses on…; may also touch…").

Lead with concrete nouns and trigger terms. Avoid "this document
covers various topics about X." Prefer, when the full document is shown:
"Covers Apple Q3 2025 earnings: iPhone units, China revenue, services
growth. Answers: how much did iPhone sales decline? What's the
services margin? Does not cover product roadmap."
When truncated, drop the exclusion: "Covers the opening of Apple's Q3
2025 earnings call: iPhone units, China revenue. May also address later
topics not shown."

A false "does not cover X" is costly — it suppresses real retrievals —
while omitting an exclusion only risks a cheap, self-correcting extra
query; when unsure, omit it.

Output target: ~100 tokens."""


def render_per_doc_user_message(filename, mime_type, first_n_tokens, was_truncated):
    coverage_note = ("; you are shown only the HEAD of this document — it was truncated, so later sections are NOT included"
                     if was_truncated else "; the full document text is shown below")
    return (f"Document: {filename}\n"
            f"Content type: {mime_type or 'unknown'}{coverage_note}\n"
            f"---------------------\n"
            f"{first_n_tokens}\n"
            f"---------------------")


def complete(system_prompt, user_message, model="haiku", timeout=300):
    """One completion through `claude -p`, no tools, at below-normal priority. Returns (text, cost_usd)."""
    for attempt in range(3):
        proc = subprocess.run(
            ["claude", "-p", "--model", model, "--system-prompt", system_prompt, "--tools", "", "--strict-mcp-config",
             "--setting-sources", "", "--no-session-persistence", "--output-format", "json"],
            input=user_message, capture_output=True, text=True, encoding="utf-8", timeout=timeout,
            creationflags=BELOW_NORMAL if os.name == "nt" else 0)
        try:
            outer = json.loads(proc.stdout)
        except json.JSONDecodeError:
            outer = {}
        text = (outer.get("result") or "").strip()
        if text and not outer.get("is_error"):
            return text, outer.get("total_cost_usd")
    raise RuntimeError(f"claude -p failed three times: exit {proc.returncode} {proc.stderr[-500:]}")


class DocSummaries:
    """Document summaries keyed by Connapse path, written on first request and cached in a JSONL file."""

    def __init__(self, corpus, cache, model="haiku", mime_type="text/plain"):
        self.corpus, self.cache, self.model, self.mime_type = Path(corpus), Path(cache), model, mime_type
        self.by_path = {}
        if self.cache.exists():
            for line in self.cache.read_text(encoding="utf-8").splitlines():
                if line.strip():
                    rec = json.loads(line)
                    self.by_path[rec["path"]] = rec["summary"]

    def get(self, path, filename=None):
        if path in self.by_path:
            return self.by_path[path]
        text = (self.corpus / path.lstrip("/")).read_text(encoding="utf-8")
        truncated = len(text) > MAX_INPUT_CHARS
        user = render_per_doc_user_message(filename or Path(path).name, self.mime_type,
                                           text[:MAX_INPUT_CHARS] if truncated else text, truncated)
        summary, cost = complete(PER_DOC_SYSTEM_PROMPT, user, self.model)
        self.cache.parent.mkdir(parents=True, exist_ok=True)
        with self.cache.open("a", encoding="utf-8") as f:
            f.write(json.dumps({"path": path, "summary": summary, "cost_usd": cost, "model": self.model,
                                "truncated": truncated}) + "\n")
        self.by_path[path] = summary
        return summary


def main():
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["doc"])
    p.add_argument("path")
    p.add_argument("--corpus", required=True)
    p.add_argument("--cache", required=True)
    p.add_argument("--model", default="haiku")
    args = p.parse_args()
    print(DocSummaries(args.corpus, args.cache, args.model).get(args.path))


if __name__ == "__main__":
    main()
