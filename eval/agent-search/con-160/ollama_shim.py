"""CON-160 shim: answers Ollama's POST /api/chat with Claude Haiku, so Connapse's own summary jobs can use Haiku.

Connapse's Ollama LLM provider sends {model, messages: [system, user], stream: false} and reads message.content.
This serves one request at a time and logs every call (prompt sizes, cost, reply) to --log.

    python ollama_shim.py --port 11460 --log E:/tmp/con-160/shim-calls.jsonl
Then start Connapse with Knowledge__Llm__Provider=Ollama and Knowledge__Llm__BaseUrl=http://localhost:11460.
"""
import argparse
import json
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

from summarize import complete


def handler_for(log_path, model):
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            if self.path != "/api/chat":
                self.send_error(404)
                return
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            messages = body.get("messages", [])
            system = "\n".join(m["content"] for m in messages if m["role"] == "system")
            user = "\n".join(m["content"] for m in messages if m["role"] == "user")
            text, cost = complete(system, user, model)
            with Path(log_path).open("a", encoding="utf-8") as f:
                f.write(json.dumps({"requested_model": body.get("model"), "model": model, "system_chars": len(system),
                                    "user": user, "reply": text, "cost_usd": cost}) + "\n")
            reply = json.dumps({"model": body.get("model"), "message": {"role": "assistant", "content": text},
                                "done": True}).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(reply)))
            self.end_headers()
            self.wfile.write(reply)

        def log_message(self, fmt, *args):
            pass

    return Handler


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--port", type=int, default=11460)
    p.add_argument("--log", required=True)
    p.add_argument("--model", default="haiku")
    args = p.parse_args()
    HTTPServer(("127.0.0.1", args.port), handler_for(args.log, args.model)).serve_forever()


if __name__ == "__main__":
    main()
