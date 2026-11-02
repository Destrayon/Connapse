"""CON-160 MCP proxy: Connapse's MCP server with a `Document summary:` line added to every search_knowledge hit.

Claude Code starts this as the stdio server named `connapse`, so tool names stay `mcp__connapse__*`. Every tool list
and tool call is forwarded unchanged to Connapse's HTTP MCP endpoint, along with its server instructions. Only
search_knowledge results change: after each hit's `DocumentId:` line comes the summary of that hit's whole record,
read from the cache or written on first sight by `summarize.DocSummaries`.

    python summary_proxy.py --url http://localhost:5146/mcp --corpus E:/tmp/con-146/corpus \
        --cache E:/tmp/con-160/doc-summaries.jsonl           # PAT from the CON142_PAT environment variable
    python summary_proxy.py --annotate recorded.txt ...      # offline: print one recorded result with summaries added
"""
import argparse
import asyncio
import os
import re
import sys
from pathlib import Path

from summarize import DocSummaries

PATH_LINE = re.compile(r"^Path: (.+)$")
FILE_LINE = re.compile(r"^File: (.+)$")


def annotate(text, summaries):
    """Insert `Document summary: ...` after each hit's DocumentId line, keyed by the hit's Path line."""
    out, path, filename = [], None, None
    for line in text.split("\n"):
        out.append(line)
        if m := FILE_LINE.match(line):
            filename = m.group(1)
        elif m := PATH_LINE.match(line):
            path = m.group(1)
        elif line.startswith("DocumentId: ") and path:
            summary = " ".join(summaries.get(path, filename).split())
            out.append(f"Document summary: {summary}")
            path, filename = None, None
    return "\n".join(out)


async def serve(args):
    from mcp import ClientSession, types
    from mcp.client.streamable_http import streamablehttp_client
    from mcp.server.lowlevel import Server
    from mcp.server.stdio import stdio_server

    summaries = DocSummaries(args.corpus, args.cache, args.model)
    headers = {"X-Api-Key": os.environ["CON142_PAT"]}
    async with streamablehttp_client(args.url, headers=headers, timeout=60, sse_read_timeout=900) as (read, write, _):
        async with ClientSession(read, write) as upstream:
            init = await upstream.initialize()
            server = Server("connapse", instructions=init.instructions)

            @server.list_tools()
            async def list_tools():
                return (await upstream.list_tools()).tools

            @server.call_tool(validate_input=False)
            async def call_tool(name, arguments):
                result = await upstream.call_tool(name, arguments)
                if name != "search_knowledge" or result.isError:
                    return result
                content, changed = [], {}
                for block in result.content:
                    if isinstance(block, types.TextContent):
                        changed[block.text] = await asyncio.to_thread(annotate, block.text, summaries)
                        block = types.TextContent(type="text", text=changed[block.text])
                    content.append(block)
                structured = result.structuredContent and {
                    k: changed.get(v, v) if isinstance(v, str) else v for k, v in result.structuredContent.items()}
                return types.CallToolResult(content=content, structuredContent=structured, isError=False)

            async with stdio_server() as (sread, swrite):
                await server.run(sread, swrite, server.create_initialization_options())


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="http://localhost:5146/mcp")
    p.add_argument("--corpus", required=True)
    p.add_argument("--cache", required=True)
    p.add_argument("--model", default="haiku")
    p.add_argument("--annotate", help="offline: annotate this recorded search_knowledge result and print it")
    args = p.parse_args()
    if args.annotate:
        sys.stdout.reconfigure(encoding="utf-8")
        print(annotate(Path(args.annotate).read_text(encoding="utf-8"),
                       DocSummaries(args.corpus, args.cache, args.model)))
        return
    asyncio.run(serve(args))


if __name__ == "__main__":
    main()
