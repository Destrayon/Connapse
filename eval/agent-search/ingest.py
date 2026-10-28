"""CON-142: loads the corpus into a Connapse container and creates a read-only PAT for the agent.

    python ingest.py --base http://localhost:5142 --email admin@con142.local --password ... \
        --corpus E:/tmp/con-142/corpus --container con142-corpus --pat-out E:/tmp/con-142/pat.txt

Code files (.cs, .razor) are uploaded with an extra `.txt` suffix, because Connapse's text parser does
not accept code extensions today; the text is unchanged. Files go up one folder at a time so each keeps
its original path. Waits until every document has finished ingesting.
"""
import argparse
import json
import subprocess
import time
import urllib.request
import uuid
from collections import defaultdict
from pathlib import Path

CODE_EXTENSIONS = {".cs", ".razor"}


def call(method, url, token=None, body=None, content_type="application/json"):
    data = body if isinstance(body, (bytes, type(None))) else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", content_type)
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req, timeout=600) as resp:
        raw = resp.read()
        return json.loads(raw) if raw else None


def multipart(files, path, strategy=None):
    boundary = uuid.uuid4().hex
    parts = [f"--{boundary}\r\nContent-Disposition: form-data; name=\"path\"\r\n\r\n{path}\r\n".encode()]
    if strategy:
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"strategy\"\r\n\r\n{strategy}\r\n".encode())
    for name, content in files:
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"{name}\"\r\n"
                     f"Content-Type: text/plain\r\n\r\n".encode() + content + b"\r\n")
    parts.append(f"--{boundary}--\r\n".encode())
    return b"".join(parts), f"multipart/form-data; boundary={boundary}"


def upload(base, jwt, args, cid=None, skip=frozenset()):
    if cid is None:
        container = call("POST", f"{base}/api/containers", jwt,
                         {"name": args.container, "description": "CON-142 eval corpus: Connapse src and docs"})
        cid = container["id"]
    root = Path(args.corpus)
    by_folder = defaultdict(list)
    for f in sorted(root.rglob("*")):
        if f.is_file():
            rel = f.relative_to(root)
            name = f.name + (".txt" if f.suffix in CODE_EXTENSIONS else "")
            folder = "/" + rel.parent.as_posix().strip(".") + "/" if str(rel.parent) != "." else "/"
            if (folder + name).replace("//", "/") not in skip:
                by_folder[folder].append((name, f))
    total = 0
    for folder, items in by_folder.items():
        for i in range(0, len(items), 50):
            batch = [(n, f.read_bytes()) for n, f in items[i:i + 50]]
            body, ctype = multipart(batch, folder.replace("//", "/"), args.strategy)
            resp = call("POST", f"{base}/api/containers/{cid}/files", jwt, body, ctype)
            errors = [d for d in resp["documents"] if d.get("error")]
            if errors:
                print("upload errors:", errors)
            total += resp["successCount"]
    print(f"uploaded {total} files into {args.container} ({cid})", flush=True)
    return cid


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--base", required=True)
    p.add_argument("--email", required=True)
    p.add_argument("--password", required=True)
    p.add_argument("--corpus", required=True)
    p.add_argument("--container", default="con142-corpus")
    p.add_argument("--pat-out", required=True)
    p.add_argument("--strategy", help="chunking strategy for every upload (Connapse default when omitted)")
    p.add_argument("--fill-id", help="existing container: upload only the files it doesn't hold, then wait")
    p.add_argument("--resume-id", help="container already uploaded: only wait for ingestion and create the PAT")
    args = p.parse_args()
    base = args.base.rstrip("/")

    jwt = call("POST", f"{base}/api/v1/auth/token", body={"email": args.email, "password": args.password})["accessToken"]
    if args.fill_id:
        # Upload only the files the container doesn't hold yet.
        rows = subprocess.run(["docker", "exec", "-i", "con142-postgres-1", "psql", "-U", "connapse", "-d", "connapse",
                               "-At"], input=f"select path from documents where container_id = '{args.fill_id}';",
                              capture_output=True, text=True, encoding="utf-8", check=True).stdout
        present = {r for r in rows.splitlines() if r}
        print(f"{len(present)} files already present", flush=True)
        cid = upload(base, jwt, args, args.fill_id, present)
    elif args.resume_id:
        cid = args.resume_id
    else:
        cid = upload(base, jwt, args)

    started = time.time()
    while True:
        stats = call("GET", f"{base}/api/containers/{cid}/stats", jwt)
        print(f"{int(time.time() - started)}s {json.dumps(stats)[:300]}", flush=True)
        docs = stats["documents"]
        if docs["ready"] + docs["failed"] >= docs["total"]:
            break
        time.sleep(30)

    pat = call("POST", f"{base}/api/v1/auth/pats", jwt,
               {"name": "con142-agent", "scopes": ["knowledge:read"],
                "expiresAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 3 * 86400))})
    Path(args.pat_out).write_text(pat["token"], encoding="utf-8")
    print("PAT written to", args.pat_out)


if __name__ == "__main__":
    main()
