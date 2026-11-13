# How should Connapse build server-side tool search (find_tools and call_*) on the C# MCP SDK?
**Date:** 2026-11-12
**Status:** Reviewed
**Built on:** mcp-tool-scaling-2026-11-12.md (CON-343, section 4.1 is the design this answers questions for)

## Executive summary

Server-side tool search for Connapse can be built on ModelContextProtocol 2.1.0 without SDK changes: a call tool
can find the target in `McpServer.ServerOptions.ToolCollection` and run it with `McpServerTool.InvokeAsync`, but
that call skips every filter, so the call tool must check the target's authorization and the approval gate itself.
Arguments should be checked by a small hand-written validator, because the SDK's schemas use a small, known set of
keywords and the .NET libraries carry a fee or heavy dependencies. Vendors that ship search-and-call designs agree
that agents skip the search unless they see what can be found, and that full schemas should come back only for the
few tools picked. No source measures accuracy at about 30 tools, so the eval in CON-346 is the only evidence that
will exist for Connapse's size.

## Research brief

**Question:** What do the SDK internals, other vendors' experience and the .NET schema-validation options imply for
building CON-343 section 4.1 (find_tools plus call_read, call_write and call_destructive)?

**Sub-questions:**
1. SDK internals: how a tool finds and runs another tool, how authorization and filter order work, how to set
   `_meta`, and how two MCP routes differ.
2. Design experience: how other search-and-dispatch MCP servers fail and what fixes them.
3. Argument checking: which .NET JSON Schema validator to use, or none.

**Out of scope:** client-side tool search, embeddings, the eval method (in CON-343 and `eval/pass-bars.md`).

**Success criteria:** a decision for each open implementation point in section 4.1.

## Findings: SDK internals (ModelContextProtocol 2.1.0)

The answers below come from the SDK source at tag v2.1.0 (primary source; paths relative to the repo's `src/`).

- **Finding the tools.** `McpServer` can be injected into a tool method. `McpServer.ServerOptions.ToolCollection`
  is the collection the server dispatches from, with `TryGetPrimitive(name, out tool)`. In stateless mode or with
  `ConfigureSessionOptions`, each session gets its own options object, so read it from the injected server, not
  from `IOptions<McpServerOptions>`.
- **Running another tool.** `McpServerTool.InvokeAsync(new RequestContext<CallToolRequestParams>(server,
  outer.JsonRpcRequest, params), ct)` runs it. Passing the outer JSON-RPC request carries the user and `Items`. The
  constructor sets `Services` to the server's root container, so set `Services = outer.Services`. Use a new
  context object per call. `McpServerImpl` applies call filters only around the top-level call, so the inner call
  reaches neither `[Authorize]` nor the approval gate.
- **Authorization.** `AddAuthorizationFilters()` (`AspNetCore/AuthorizationFilterSetup.cs`, internal) reads
  `IAuthorizeData`, `AuthorizationPolicy` and `IAuthorizationRequirementData` from `tool.Metadata`, skips tools
  marked `IAllowAnonymous`, and calls `IAuthorizationService.AuthorizeAsync(user, context, policy)`. It has no public
  helper, so the call tools copy its `CombineAsync`.
- **Filter order.** The first filter added is the outermost. The authorization list filter trims `tools/list`
  after `next` returns, so a filter that hides tools must be added before `AddAuthorizationFilters()` to see the
  trimmed list, or it can simply hide by name either way.
- **Errors.** An `McpException` thrown by a tool reaches the client as `"An error occurred invoking '{name}':
  {message}"`; any other exception loses its message. The call tools should return their own error text.
- **`_meta`.** `[McpMeta("anthropic/alwaysLoad", true)]` on the method sets it.
- **Two routes.** `MapMcp` can be called twice; both routes share one handler and session store. A filter cannot
  see the route, but `IHttpContextAccessor` in a filter sees the request (unconfirmed for messages that arrive over
  the GET stream, which `tools/list` does not use).

## Findings: other vendors' search-and-dispatch designs

Vendors that ship search-and-call tools report the same failures and fixes (mostly vendor sources, one paper).

- **Agents skip search when they can't see what exists.** Speakeasy's v2 fix put a category overview into the
  search tool's description; Anthropic advises naming the tool categories in the system prompt and keeping the most
  used tools loaded. Section 4.1 already names every tool in the server instructions.
- **Full schemas only for the picked tools.** Speakeasy, Klavis Strata and Stainless separate listing from
  describing because schemas are 60 to 80% of the tokens. At Connapse's size a separate describe step mostly adds a
  round trip, and an agent that skips it guesses arguments; returning the top three in full (section 4.1) avoids
  both.
- **Failures are wrong picks and wrong arguments, not loops.** Anthropic names similar tool names as the main cause.
  ScaleMCP found agents picked the right tool 23 to 54% of the time yet finished 69 to 94% of tasks, so task
  success alone hides wrong picks. No source reports agents looping on search.
- **Call-tool wording.** Composio's execute tool says the name "must be a valid tool slug; never invent" and the
  arguments must use "exact field names and types". An unknown name should return the closest names.
- **Keyword search is enough at ~30 tools.** Anthropic ships BM25 and regex search; embeddings only win at
  thousands of tools (ScaleMCP: BM25 recall 0.674 against 0.912 at 5,000 tools).
- **Numbers.** Anthropic: MCP evals 49% to 74% (Opus 4) and 79.5% to 88.1% (Opus 4.5) with tool search.
  Speakeasy: 100% task success but 2 to 3 times more calls (vendor test).

## Findings: argument validation in .NET

A hand-written validator of about 150 lines covers every keyword the SDK emits (probe of `McpServerTool.Create`).

- **What the SDK emits.** Root `{"type":"object","properties","required"}` with no `additionalProperties`. A
  parameter without a C# default is required, even if nullable. `T?` becomes `[T,"null"]`. `int`/`long` become
  `integer`, `double` becomes `number`. Enums are `{"type":"string","enum":[C# names]}`. `JsonElement` emits `true`
  and `JsonElement?` emits `{"default":null}`. Defaults can contradict the type, so they must not be checked.
  Unknown arguments are silently dropped by the SDK (csharp-sdk issue #1508).
- **Libraries.** JsonSchema.Net 9.x binaries carry a maintenance fee for organisations earning US$10,000 or more a
  year. NJsonSchema needs Newtonsoft and has no net10 build. Corvus.Json.Validator pulls in Roslyn. .NET 10 has no
  validator.
- **Decision.** Validate `required`, unknown properties, `type` (string or array; `integer` means no fraction),
  `enum`, `items` and nested `properties`. Ignore `format`, `default` and `description`. A test fails if any tool's
  schema uses a keyword outside that set.

## Conflicts and uncertainties

- Result count: Anthropic returns 5 full definitions by default; section 4.1 returns 3 in full and the rest as one
  line each. Kept at 3, since the next matches still show names and the eval measures it.
- Route detection through `IHttpContextAccessor` is unconfirmed for the GET stream; `tools/list` is a POST, and an
  integration test covers `/mcp/full`.
- The exception type Microsoft.Extensions.AI throws on bad arguments was recalled, not read; the validator makes it
  moot by rejecting bad arguments first.

## Gaps

- No source measures tool-selection accuracy or search behaviour at about 30 tools.
- No public data on Claude Desktop's handling of `anthropic/alwaysLoad` from a remote connector.

## Source quality assessment

SDK and validator findings rest on primary sources (SDK source at v2.1.0, a local probe, NuGet licence pages).
Design-experience findings are mostly vendor engineering posts (secondary, self-reported numbers); the papers
(RAG-MCP, ScaleMCP, MCP-Zero, ToolRet) are primary but measure far larger tool pools.

## Sources

**Primary:** modelcontextprotocol/csharp-sdk v2.1.0 source; https://github.com/modelcontextprotocol/csharp-sdk/issues/1508;
https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool; https://www.nuget.org/packages/JsonSchema.Net/9.6.0/License;
https://www.nuget.org/packages/NJsonSchema; https://www.nuget.org/packages/Corvus.Json.Validator;
https://learn.microsoft.com/dotnet/api/system.text.json.schema; https://arxiv.org/abs/2505.03275; https://arxiv.org/html/2505.06416v1;
https://arxiv.org/abs/2506.01056; https://arxiv.org/abs/2503.01763; https://github.com/github/github-mcp-server/pull/2512.

**Secondary:** https://www.anthropic.com/engineering/advanced-tool-use; https://www.speakeasy.com/blog/100x-token-reduction-dynamic-toolsets;
https://www.speakeasy.com/blog/how-we-reduced-token-usage-by-100x-dynamic-toolsets-v2; https://www.speakeasy.com/docs/mcp/build/toolsets/dynamic-toolsets.md;
https://www.klavis.ai/blog/introducing-strata-one-mcp-server-for-thousands-of-tools; https://docs.composio.dev/toolkits/meta-tools/multi_execute_tool.md;
https://www.stainless.com/changelog/mcp-dynamic-tools/; https://blog.cloudflare.com/code-mode-mcp/.
