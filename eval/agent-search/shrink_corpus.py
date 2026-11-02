"""CON-142: cuts the eval corpus down to every question's evidence files plus up to N distractor files.

Ingesting all 773 files under the 2-CPU Ollama cap would take about 3 hours, so the RAG run uses a smaller
corpus. Distractors are files the container has already ingested (no re-embedding), taken in path order.
Everything else is deleted from the container, and the kept files are copied to --out so the files setup
searches exactly the same set.

    python shrink_corpus.py --base ... --email ... --password ... --container-id <id> \
        --corpus E:/tmp/con-142/corpus --out E:/tmp/con-142/corpus-small --distractors 150
"""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

from ingest import CODE_EXTENSIONS, call

HERE = Path(__file__).parent


def stored_docs(cid):
    rows = subprocess.run(["docker", "exec", "-i", "con142-postgres-1", "psql", "-U", "connapse", "-d", "connapse",
                           "-At", "-F", "\t"],
                          input=f"select id, path, ingestion_status from documents where container_id = '{cid}';",
                          capture_output=True, text=True, encoding="utf-8", check=True).stdout
    return [dict(zip(("id", "path", "status"), r.split("\t"))) for r in rows.splitlines() if r]


def corpus_path(stored):
    p = stored.lstrip("/")
    return p[:-4] if p.endswith(".txt") and Path(p[:-4]).suffix in CODE_EXTENSIONS else p


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--base", required=True)
    p.add_argument("--email", required=True)
    p.add_argument("--password", required=True)
    p.add_argument("--container-id", required=True)
    p.add_argument("--corpus", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--distractors", type=int, default=150)
    args = p.parse_args()
    base = args.base.rstrip("/")

    questions = json.loads((HERE / "questions.json").read_text(encoding="utf-8"))
    evidence = {e.rsplit(":", 1)[0] for q in questions for e in q["evidence"]}
    stored = stored_docs(args.container_id)
    missing = evidence - {corpus_path(d["path"]) for d in stored}
    if missing:
        raise SystemExit(f"evidence files not in the container: {sorted(missing)}")
    ready = sorted(corpus_path(d["path"]) for d in stored if d["status"] == "Ready")
    distractors = [f for f in ready if f not in evidence][:args.distractors]
    keep = evidence | set(distractors)

    drop = [d["id"] for d in stored if corpus_path(d["path"]) not in keep]
    jwt = call("POST", f"{base}/api/v1/auth/token", body={"email": args.email, "password": args.password})["accessToken"]
    for i in range(0, len(drop), 100):
        call("POST", f"{base}/api/containers/{args.container_id}/files/bulk-delete", jwt, {"fileIds": drop[i:i + 100]})

    out = Path(args.out)
    if out.exists():
        shutil.rmtree(out)
    for f in sorted(keep):
        target = out / f
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(Path(args.corpus) / f, target)
    code = sum(1 for f in keep if Path(f).suffix in CODE_EXTENSIONS)
    print(json.dumps({"evidence_files": len(evidence), "distractors": len(distractors), "kept": len(keep),
                      "kept_code": code, "kept_docs": len(keep) - code, "deleted": len(drop),
                      "evidence_not_yet_ingested": len(evidence - set(ready))}))
    (out.parent / "corpus-small-files.txt").write_text("\n".join(sorted(keep)), encoding="utf-8")


if __name__ == "__main__":
    main()
