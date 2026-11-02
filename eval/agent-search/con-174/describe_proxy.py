"""CON-174 MCP proxy: Connapse's MCP server, with container_describe returning this run's container summary.

Claude Code starts this as the stdio server named `connapse`, so tool names stay `mcp__connapse__*`. Every tool list
and tool call is forwarded unchanged to Connapse's HTTP MCP endpoint, with its server instructions. Only
container_describe results for eval-erb-50k change: Connapse's `Summary: (not yet generated)` line becomes the summary
from container_summary.py, printed where and how Connapse prints a stored one.

    python describe_proxy.py --url http://localhost:5174/mcp --summary E:/tmp/con-174/container-summary.json
                                                                       # PAT from the CON142_PAT environment variable
"""
import argparse
import asyncio
import json
import os
from pathlib import Path

CONTAINER = "eval-erb-50k"
PLACEHOLDER = "Summary: (not yet generated)"  # McpTools.ContainerDescribe with no stored summary


def annotate_describe(text, summary, container=CONTAINER):
    """Put `summary` in place of the no-summary placeholder in a container_describe result for `container`."""
    lines = text.split("\n")
    if not lines or lines[0] != f"Container: {container}":
        return text
    found = [i for i, line in enumerate(lines) if line.startswith("Summary: ")]
    if len(found) != 1 or lines[found[0]] != PLACEHOLDER:
        raise ValueError("Connapse did not print its no-summary placeholder; run the stack with summaries off")
    lines[found[0]] = "Summary: " + summary.strip()  # printed as-is, like McpTools prints a stored summary
    return "\n".join(lines)


def load_summary(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))["summary"]


async def serve(args):
    from mcp import ClientSession, types
    from mcp.client.streamable_http import streamablehttp_client
    from mcp.server.lowlevel import Server
    from mcp.server.stdio import stdio_server

    summary = load_summary(args.summary)
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
                if name != "container_describe" or result.isError:
                    return result
                content, changed = [], {}
                for block in result.content:
                    if isinstance(block, types.TextContent):
                        changed[block.text] = annotate_describe(block.text, summary)
                        block = types.TextContent(type="text", text=changed[block.text])
                    content.append(block)
                structured = result.structuredContent and {
                    k: changed.get(v, v) if isinstance(v, str) else v for k, v in result.structuredContent.items()}
                return types.CallToolResult(content=content, structuredContent=structured, isError=False)

            async with stdio_server() as (sread, swrite):
                await server.run(sread, swrite, server.create_initialization_options())


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="http://localhost:5174/mcp")
    p.add_argument("--summary", required=True, help="container-summary.json from container_summary.py")
    asyncio.run(serve(p.parse_args()))


if __name__ == "__main__":
    main()
