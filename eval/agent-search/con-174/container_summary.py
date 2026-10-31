"""CON-174 container summary: Connapse's own prompts over a random sample of erb-50k, answered by Claude Haiku.

Document summaries come from CON-160's `summarize.DocSummaries` (Connapse's per-document prompt and truncation). The
container prompt and user message are copied verbatim from src/Connapse.Storage/Llm/SummaryPrompts.cs, rendered the
way ContainerSummarizer renders a clustered container: every summary prefixed with the number of documents it stands
for, here 50,000 / 20 = 2,500 each. `test_describe.py` checks the copies still match the C# source.

The sample is 20 records (Connapse's MaxClusters) drawn with seed 174 from all 50,000, leaving out the gold records of
every question in CON-146's set, so the summary cannot carry an answer.

    python container_summary.py --corpus E:/tmp/con-146/corpus --dataset E:/tmp/con-32/cache/datasets/erb-50k/69916e31c68a \
        --cache E:/tmp/con-174/doc-summaries.jsonl --out E:/tmp/con-174/container-summary.json [--dry]
"""
import argparse
import ast
import json
import random
import sys
from pathlib import Path

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE.parent / "con-160"))
from summarize import DocSummaries, complete  # noqa: E402

CONTAINER = "eval-erb-50k"
SAMPLE_SIZE = 20  # ContainerSummarizer.MaxClusters
SEED = 174

CONTAINER_ROLLUP_SYSTEM_PROMPT = """You are writing a CONTAINER DESCRIPTION that an LLM agent will read
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
unsure, omit it."""


def render_container_rollup_user_message(container_name, total_docs, is_clustered, summaries):
    cluster_note = ("; you are shown a representative SAMPLE — cluster medoids, with cluster sizes in brackets — NOT every document"
                    if is_clustered else "; all documents are shown below")
    body = "\n".join(f"{i + 1}. {s}" for i, s in enumerate(summaries))
    return (f'Container: "{container_name}"\n'
            f"{total_docs} documents total{cluster_note}\n"
            f"---------------------\n"
            f"{body}\n"
            f"---------------------")


def _ids(value):
    return ast.literal_eval(value) if isinstance(value, str) else value


def gold_rows(dataset, questions):
    """Row indices (= corpus folder numbers) of every gold record of every question."""
    import pyarrow.parquet as pq
    gold = {d for q in questions for d in _ids(q["gold_doc_ids"])}
    ids = pq.read_table(Path(dataset) / "documents.parquet", columns=["doc_id"]).column("doc_id").to_pylist()
    rows = {i for i, d in enumerate(ids) if d in gold}
    if len(rows) != len(gold):
        sys.exit(f"found {len(rows)} of {len(gold)} gold records in the dataset")
    return rows, len(ids)


def sample_rows(total, excluded, k=SAMPLE_SIZE, seed=SEED):
    return sorted(random.Random(seed).sample([i for i in range(total) if i not in excluded], k))


def record_path(corpus, row):
    """Connapse path of a record: `/<row>/<title>.txt`, or `/<row>.txt` for an untitled one (see export_corpus.py)."""
    folder = Path(corpus) / f"{row:07d}"
    if folder.is_dir():
        files = list(folder.iterdir())
        if len(files) != 1:
            sys.exit(f"{folder} holds {len(files)} files, expected one")
        return f"/{row:07d}/{files[0].name}"
    return f"/{row:07d}.txt"


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--corpus", required=True)
    p.add_argument("--dataset", required=True)
    p.add_argument("--questions", default=str(HERE.parent / "con-146" / "questions.json"))
    p.add_argument("--cache", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--model", default="haiku")
    p.add_argument("--dry", action="store_true", help="print the sample and the container prompt; call no model")
    args = p.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    questions = json.loads(Path(args.questions).read_text(encoding="utf-8"))
    excluded, total = gold_rows(args.dataset, questions)
    paths = [record_path(args.corpus, r) for r in sample_rows(total, excluded)]
    docs = DocSummaries(args.corpus, args.cache, args.model)
    if args.dry:
        summaries = [docs.by_path.get(path, f"<document summary of {path}>") for path in paths]
    else:
        summaries = [" ".join(docs.get(path).split()) for path in paths]
    each = total // SAMPLE_SIZE
    user = render_container_rollup_user_message(
        CONTAINER, total, True, [f"(represents {each} similar docs): {s}" for s in summaries])
    if args.dry:
        print(f"{len(excluded)} gold records left out; sample:\n" + "\n".join(paths) + "\n\n" + user)
        return
    summary, cost = complete(CONTAINER_ROLLUP_SYSTEM_PROMPT, user, args.model)
    doc_cost = sum(r["cost_usd"] or 0 for r in map(json.loads, Path(args.cache).read_text(encoding="utf-8").splitlines())
                   if r["path"] in paths)
    Path(args.out).write_text(json.dumps({"summary": summary, "sample": paths, "model": args.model,
                                          "rollup_cost_usd": cost, "doc_summaries_cost_usd": doc_cost},
                                         indent=1), encoding="utf-8")
    print(summary)


if __name__ == "__main__":
    main()
