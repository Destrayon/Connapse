# How can Connapse's MCP tools keep growing without bloating every agent?
**Date:** 2026-11-12
**Status:** Reviewed
**Built on:** api-mcp-cli-consistency-2026-10-17.md (GitHub, Stripe and Cloudflare MCP designs; the 2026-07-28 spec change), eval/pass-bars.md (how bars are set), the memory-search agent harness in `tests/Connapse.Eval`.

## Executive summary

Connapse's tool list is smaller than its reputation: the 31 tools an admin or write key sees cost about 6,700
tokens, and a read-only agent key already sees only 17 tools (about 3,700 tokens), because `tools/list` is trimmed
per token. Four of the seven main clients (Claude Code, Codex, Cursor and VS Code Copilot) now defer tool
definitions themselves and load them through a client-side tool search, so in those clients the token cost of a
long list is mostly gone. The cost still lands in full in Claude Desktop and claude.ai chat, ChatGPT and Gemini CLI,
and every client still pays in tool-picking accuracy and in per-tool permission prompts. Adding tools mid-session
with `notifications/tools/list_changed` works reliably in none of the seven, and a generic "call any tool" dispatcher
defeats per-tool approval in all of them. **Recommendation for 0.5.0: (1) toolsets chosen per token or connection,
built on the per-token trimming Connapse already has, and (2) shorter, search-ready descriptions with the core
search tools pinned upfront.** The main uncertainty is accuracy: no vendor publishes numbers for a list of 25–30
tools, so the gain has to be shown on our own agent runs, with bars set below before any run.

## Research brief

**Question:** which ways of growing Connapse's MCP surface keep the tool cost and tool-picking errors of a
connected agent flat, across the apps people connect from?

**Sub-questions:**
1. What does Connapse's tool list cost today, per area (search, code, memory, admin)?
2. For each of Claude Code, Claude Desktop, Cursor, VS Code Copilot, ChatGPT, Codex and Gemini CLI: which options
   work there?
3. What is each option's catch, including what it does to the user's per-tool permission prompts?
4. Which two options should 0.5.0 build, and how do we prove them?

**Out of scope:** the near-twin merge itself. The user decided on 2026-11-12 that merging `get_document`/
`get_documents`, `delete_file`/`bulk_delete` and `upload_file`/`bulk_upload` ships in 0.4.2 as its own ticket
(CON-344), so this report takes the merged list as its starting point. Also out of scope: tool *output* size, and
the REST API and CLI.

**Success criteria:** a measured baseline, a client-by-option matrix with sources, each option's catch, the top two
for 0.5.0, and a test plan with bars fixed before any run.

## 1. What the tool list costs today

**Claim: the full list is about 6,700 tokens for 31 tools; memory is the most expensive area per tool, and a
read-only agent key already gets a list half that size.**

Method: a throwaway integration test (not committed) connected to the in-process server at `main` 24e6a7b7, called
`tools/list` as an admin, a read-only agent key (`knowledge:read`) and a read-write agent key, and saved the raw
responses. Each tool object (name, title, description, input schema, annotations) was serialized as compact JSON
and counted with OpenAI's tiktoken `o200k_base` encoding (tiktoken 0.14.0); `cl100k_base` gives counts within 2%.
Claude's own tokenizer is not public and usually counts more tokens for the same text, so treat these as a floor
for Claude; the test plan below re-counts with Anthropic's token-counting endpoint. Clients also wrap each tool in
their own framing, which this does not include. The server instructions sent on connect add 273 tokens.

| Area | Tools | Tokens (o200k) | Of which description text | Tools |
|---|---|---|---|---|
| Search and documents | 14 | 2,394 | 886 | `search_knowledge`, `list_files`, `get_document(s)`, `upload_file`, `bulk_upload`, `append_to_file`, `delete_file`, `bulk_delete`, `container_list/create/delete/describe/stats` |
| Memory | 7 | 2,313 | 841 | `memory_spaces`, `memory_space_create`, `memory_remember`, `memory_correct`, `memory_forget`, `memory_recall`, `memory_timeline` |
| Admin and approvals | 6 | 1,122 | 578 | `connapse_api_search/read/write`, `request_deletions`, `approval_status`, `deletion_status` |
| Code | 4 | 911 | 361 | `code_find_symbol`, `code_references`, `code_impact`, `code_repo_map` |
| **All (admin or write key)** | **31** | **6,740** | **2,666** | |
| Read-only agent key | 17 | 3,693 | 1,460 | write tools, `connapse_api_write` and `request_deletions` are trimmed |

- The most expensive single tools are `memory_remember` (499), `memory_correct` (425), `memory_recall` (423),
  `memory_timeline` (393) and `list_files` (369). Memory costs 330 tokens per tool against 171 for search.
- Description text is 40% of the total; the rest is input schema (parameter names, types and per-parameter
  descriptions). No tool sends an output schema.
- After CON-344 removes the three near-twins (`get_documents` 188, `bulk_delete` 157, `bulk_upload` 125 tokens), the
  starting point is 28 tools and about 6,300–6,500 tokens, depending on how much the merged tools' schemas grow.
- For scale: Anthropic measured GitHub's 35-tool server at about 26,000 tokens and five common servers (58 tools)
  at about 55,000 ([advanced tool use](https://www.anthropic.com/engineering/advanced-tool-use)). Connapse's tools
  average 217 tokens each, a third of GitHub's. The problem is the slope as features land, not today's size.

## 2. Which options work in which client

**Claim: client-side deferred loading already exists in four of the seven clients; mid-session tool-list changes
work reliably in none; per-tool allow and deny exists in all seven, which is why server-side groupings that keep
tools distinct are safe everywhere.**

"Server-side" options (toolsets per token or connection, broader tools, separate servers, shorter descriptions)
work in every client because the client just sees a different list; the columns that differ are the ones below.

| Client | Defers tool definitions itself (option 6) | Honours `tools/list_changed` mid-session (option 1a) | Per-tool allow/deny and approval | Uses `readOnlyHint` for approval |
|---|---|---|---|---|
| Claude Code | **Yes, on by default** (`ENABLE_TOOL_SEARCH`; `auto:N` defers past N% of context). Names and server instructions load upfront; `_meta["anthropic/alwaysLoad"]` pins a tool. | Documented for the interactive CLI if the server advertises `listChanged: true`; open bug that the tool-search index is not updated (#66084) | Yes: `mcp__server__tool` rules, deny removes the tool | No (used for parallel calls and retries) |
| Claude Desktop / claude.ai | **No** documented tool search in chat | **No**: stale list until relaunch (open #88172, #92858) | Yes: per-tool Blocked, per-chat connector toggle, Always allow | **Yes**: read-only tools run without confirmation, destructive ones always prompt (directory policy) |
| Cursor | **Yes**: dynamic context discovery (Jan 2026) gives names only; schemas in files the agent reads | **Regressed** since 3.12.17 (Jul 2026); CLI ignores it | Allowlist per tool or per server (forum-sourced, buggy); UI toggles don't persist | Not found |
| VS Code Copilot | **Yes**: tool search with deferred loading (2026) and virtual tool groups past 128 tools | Partly: reflected in the UI, but the deferred registry is a per-turn snapshot (#303012) | Yes: per-tool or per-server trust; per-tool on/off only in the deprecated Local harness | Local harness yes; Copilot harness no |
| ChatGPT | **No** | **No**: manual Refresh; workspace apps use a frozen snapshot | Yes: per-tool toggles; per-tool approval remembered per conversation | **Yes**: tools without `readOnlyHint` are write actions and need confirmation |
| Codex | **Yes, always** for MCP tools (open bugs where search misses exact names: #21503, #56973) | **No**: behind flag `mcp_tool_list_changed`, off by default, handler only logs (#10105) | Yes: `enabled_tools`/`disabled_tools`, per-tool `approval_mode` | **Yes**: `writes` mode prompts for tools not marked read-only |
| Gemini CLI | **No** | **Yes** since Mar 2026 (PR 21050), refresh races the next request (#25650) | Yes: `includeTools`/`excludeTools`; "always allow this tool / this server" | Not found; `trust: true` skips all prompts |

Sources: Claude Code [MCP](https://code.claude.com/docs/en/mcp) and [permissions](https://code.claude.com/docs/en/permissions) docs, [#66084](https://github.com/anthropics/claude-code/issues/66084), [#88483](https://github.com/anthropics/claude-code/issues/88483);
Claude Desktop [remote MCP](https://claude.com/docs/connectors/custom/remote-mcp) and [connector review criteria](https://claude.com/docs/connectors/building/review-criteria), [#88172](https://github.com/anthropics/claude-code/issues/88172), [#92858](https://github.com/anthropics/claude-code/issues/92858);
Cursor [MCP docs](https://cursor.com/docs/context/mcp), [run modes](https://cursor.com/docs/agent/security/run-modes), [dynamic context discovery](https://cursor.com/blog/dynamic-context-discovery), forum threads [166216](https://forum.cursor.com/t/mcp-server-regression-does-not-reload-tools-disconnect-does-nothing-ignores-mcp-json-changes-etc/166216), [161459](https://forum.cursor.com/t/mcp-notifications-tools-list-changed-not-acted-on-mid-session/161459), [169566](https://forum.cursor.com/t/mcp-allowlist-entries-are-written-but-never-matched-every-mcp-call-re-prompts/169566), [148139](https://forum.cursor.com/t/add-the-ability-to-pre-configure-which-mcp-tools-are-enabled-disabled-via-the-mcp-json-configuration-file-eliminating-the-need-to-manually-enable-tools-in-the-ui-after-every-cursor-restart/148139);
VS Code [tools](https://code.visualstudio.com/docs/agents/run/tools), [approvals](https://code.visualstudio.com/docs/agents/run/approvals), [AI settings](https://code.visualstudio.com/docs/agents/reference/ai-settings), [MCP extension guide](https://code.visualstudio.com/api/extension-guides/ai/mcp), [token-efficiency blog](https://code.visualstudio.com/blogs/2026/06/17/improving-token-efficiency-in-github-copilot), [#303012](https://github.com/microsoft/vscode/issues/303012), [#341709](https://github.com/microsoft/vscode/issues/341709);
ChatGPT and OpenAI [custom MCP server guide](https://developers.openai.com/api/docs/guides/custom-mcp-server), [MCP connectors](https://developers.openai.com/api/docs/guides/tools-connectors-mcp), [tool search](https://developers.openai.com/api/docs/guides/tools-tool-search.md), [function calling](https://developers.openai.com/api/docs/guides/function-calling);
Codex [MCP config](https://learn.chatgpt.com/docs/extend/mcp?surface=cli), [features/lib.rs](https://github.com/openai/codex/blob/main/codex-rs/features/src/lib.rs), [#10105](https://github.com/openai/codex/issues/10105), [#21503](https://github.com/openai/codex/issues/21503), [#56973](https://github.com/openai/codex/issues/56973);
Gemini CLI [MCP server docs](https://geminicli.com/docs/tools/mcp-server/), [PR 14375](https://github.com/google-gemini/gemini-cli/pull/14375), [PR 21050](https://github.com/google-gemini/gemini-cli/pull/21050), [#25650](https://github.com/google-gemini/gemini-cli/issues/25650).

Notes that change decisions:
- **Connapse speaks the stateless 2026-07-28 MCP spec**, which removed sessions and moved change notices to
  `subscriptions/listen` ([changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog)). A
  per-connection "enable this toolset" call (GitHub's `--dynamic-toolsets`) has no session to remember the choice
  in, so option 1a would need its own state keyed by token, on top of the client gaps above.
- **Anthropic's connector directory forbids a catch-all tool**: "Don't ship a catch-all `api_request` tool with a
  `method` parameter"; reads and writes must be separate tools, and every tool needs `readOnlyHint` or
  `destructiveHint` ([review criteria](https://claude.com/docs/connectors/building/review-criteria)). Connapse's
  `connapse_api_read`/`connapse_api_write` split already satisfies the read/write rule.
- **Gemini CLI** was replaced by Antigravity CLI for unpaid and Google One users on 2026-06-18 (same Gemini CLI
  docs page), which shrinks that audience. Antigravity was not researched.
- **Vendor accuracy guidance:** OpenAI says "aim for fewer than 20 functions available at the start of a turn" and
  under 10 per tool-search namespace; Anthropic's API docs say selection accuracy "degrades once you exceed 30–50
  available tools" and recommend tool search from 10 tools or 10k tokens up
  ([tool search tool](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)).

## 3. Each option and its catch

**Claim: every option that keeps one tool per action keeps per-tool permission prompts intact; the two that fold
many actions into one tool (a dispatcher, or broad tools that mix reads and writes) break them in every client.**

**1a. Tool search by adding tools mid-session (`notifications/tools/list_changed`).** The server starts with a few
tools plus an "enable toolset" tool and announces the new list when the agent asks for more.
- *Works in:* Gemini CLI (with a race on the next request); partly in Claude Code CLI and VS Code. Not in Claude
  Desktop or claude.ai, ChatGPT, Codex (flag off by default) or current Cursor (regressed).
- *Catch:* GitHub built exactly this (`--dynamic-toolsets`: `enable_toolset`, `list_available_toolsets`) and
  removed it in PR #2512 (merged 2026-05-20); the maintainer called server-side progressive discovery
  "increasingly outdated" next to client tool search, and users reported "agents constantly cannot discover tools"
  ([#275](https://github.com/github/github-mcp-server/issues/275),
  [#2512](https://github.com/github/github-mcp-server/pull/2512)). Under the stateless 2026-07-28 spec there is no
  session to hold the enabled set. Every proposal to put groups or tool manifests into the MCP spec (SEP-993,
  SEP-1300, SEP-2084, SEP-1881, SEP-2053, SEP-2636) was closed; the work moved to a Progressive Disclosure working
  group in September 2026 ([PR #3376](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/3376)).
- *Permission prompts:* unaffected, since tools stay distinct.

**1b. Tool search through a generic dispatcher** (`search_tools` plus `call_tool(name, args)`, as Speakeasy and
Cloudflare Code Mode do).
- *Works in:* every client, and it is the biggest cut: Cloudflare covers 2,500+ endpoints in about 1,000 tokens
  ([Code Mode](https://blog.cloudflare.com/code-mode-mcp/)); Speakeasy reports 91–97% fewer input tokens at the
  cost of more calls ([dynamic toolsets v2](https://www.speakeasy.com/blog/how-we-reduced-token-usage-by-100x-dynamic-toolsets-v2), vendor figures).
- *Catch:* **it defeats per-tool permission prompts in all seven clients**. Allow and deny rules in Claude Code,
  Codex, Gemini CLI, Cursor and VS Code match the tool name, so one dispatcher is one rule; ChatGPT, Claude
  Desktop and Codex read `readOnlyHint` from the tool, so a dispatcher is either read-only (writes skip
  confirmation) or not (every read prompts). Anthropic's directory rejects such a tool outright. The schema the
  client validates is a bag of arguments, so bad calls fail later and less clearly. Connapse already has the safe
  form of this for the long tail (`connapse_api_search/read/write`: read and write split, destructive calls held by
  `McpApprovalGate`); moving curated tools behind it would lose the prompts.

**2. Toolsets chosen per token or connection** (GitHub's `--toolsets`, `X-MCP-Toolsets` header and
`/mcp/x/{toolset}` URLs; Supabase `features=`; Linear `/mcp/readonly`).
- *Works in:* all seven, since the client simply receives a shorter list. GitHub's README: "Enabling only the
  toolsets that you need can help the LLM with tool choice and reduce the context size"
  ([README](https://github.com/github/github-mcp-server/blob/main/README.md),
  [remote server](https://github.com/github/github-mcp-server/blob/main/docs/remote-server.md)).
- *Catch:* someone has to choose, and an agent cannot reach a tool outside its toolset without a new token or
  connection. A wrong default is worse than a long list. Hiding a tool is not enforcement, so the server must still
  check every call (Connapse already does).
- *Permission prompts:* unaffected; fewer tools also means fewer prompts to set up.
- *In Connapse today:* `tools/list` is already trimmed per caller and marked `cacheScope: private` (#846), which is
  why a read-only key sees 17 tools. GitHub does the same for classic-token scopes
  ([changelog 2026-01-28](https://github.blog/changelog/2026-01-28-github-mcp-server-new-projects-tools-oauth-scope-filtering-and-new-features/)).

**3. Fewer, broader tools with an action parameter.**
- *Works in:* all seven. GitHub merged six pull-request tools into `pull_request_read` and cut its Projects tools'
  cost by "around 23,000 tokens (50%)"
  ([changelog 2025-10-14](https://github.blog/changelog/2025-10-14-github-mcp-server-now-supports-github-projects-and-more/));
  Amplitude went from 96 tools, which "used up 57%" of a 200K window, to one tool per object
  ([Amplitude](https://amplitude.com/blog/lessons-from-consolidating-our-mcp-server)); Block cut a 30-tool Linear
  server to two ([Block playbook](https://engineering.block.xyz/blog/blocks-playbook-for-designing-mcp-servers)).
- *Catch:* when one tool mixes reads and writes, permissions become "all-or-nothing" because clients "attach
  permissions at the *tool name* level, not on the `method` argument"; a GitHub maintainer conceded they "had to
  prioritise reducing server token footprint" and now ships `issues_granular` flags to bring the separate tools back
  ([#2525](https://github.com/github/github-mcp-server/issues/2525)). One `destructiveHint` covers every action
  ([PR #3791](https://github.com/github/github-mcp-server/pull/3791)), and a shared `required` list breaks some
  actions ([#3501](https://github.com/github/github-mcp-server/issues/3501)). Amplitude found that gating a merged
  tool at its riskiest action locked out read-only users. Safe only when every action shares one risk class, which
  is Block's rule and why CON-344's merges (one vs many of the same action) are fine.

**4. Separate MCP servers per area** (e.g. `/mcp/search`, `/mcp/memory`, `/mcp/code`).
- *Works in:* all seven; each is just another server. Claude Code, Codex and Gemini CLI can then enable or disable
  a whole area by server name.
- *Catch:* users connect and sign in once per server (OAuth per connector in Claude Desktop and ChatGPT), and an
  agent that needs two areas needs two connections. Toolset URL paths (option 2) give the same split behind one
  token, so a separate deployment adds cost without adding control.
- *Permission prompts:* unaffected.

**5. Shorter descriptions.**
- *Works in:* all seven; it is the only lever on the per-tool cost (40% of Connapse's tokens are description
  text, and most of the rest is per-parameter descriptions).
- *Catch:* accuracy can fall. A study of MCP descriptions found that richer descriptions raised task success by a
  median 5.85 points but added 67% more steps, and still regressed in 16.67% of cases
  ([arXiv 2602.14878](https://arxiv.org/abs/2602.14878)); Anthropic's guidance favours clear, specific descriptions
  ([writing tools for agents](https://www.anthropic.com/engineering/writing-tools-for-agents)). In clients with
  tool search the description is what the search matches, so a cut can make a tool unfindable (Codex has open
  search-miss bugs). Must be eval-guarded. Claude Code also truncates descriptions at 4,096 characters for
  upfront tools.
- *Permission prompts:* unaffected.

**6. Client-side deferred loading** (Claude Code tool search, Codex, Cursor, VS Code; the Anthropic and OpenAI
APIs' `defer_loading`).
- *Works in:* Claude Code (default on), Codex (always), Cursor and VS Code. Not Claude Desktop or claude.ai,
  ChatGPT or Gemini CLI. Anthropic reports 85% fewer tokens (about 77K to 8.7K) and MCP-eval accuracy from 49% to
  74% on Opus 4 ([advanced tool use](https://www.anthropic.com/engineering/advanced-tool-use)); Cursor 46.9% fewer
  agent tokens in runs that used MCP ([Cursor](https://cursor.com/blog/dynamic-context-discovery)); VS Code 8.6–11.3%
  fewer tokens per turn.
- *Catch:* Connapse cannot turn it on for the clients that lack it, and it is only as good as the descriptions it
  searches. It makes server-side token work unnecessary *in those four clients*, not in the other three, and
  does nothing for accuracy once a tool is loaded.
- *Server help:* good names and prefixes, server instructions that name the areas (Claude Code loads these upfront
  "to help Claude understand when to search for your tools"), and `_meta["anthropic/alwaysLoad"]` on the tools
  nearly every task needs.
- *Permission prompts:* unaffected.

| Option | Cuts tokens in | Hurts per-tool prompts | Verdict |
|---|---|---|---|
| 1a. `list_changed` loading | Gemini CLI only, reliably | No | Not now |
| 1b. Generic dispatcher | All | **Yes, everywhere** | Keep only `connapse_api_*` for the long tail |
| 2. Toolsets per token or connection | All | No | **Recommend** |
| 3. Broad tools with action param | All | Yes, when risk classes mix | Same-risk merges only (CON-344) |
| 4. Separate servers | All | No | Use toolset URLs instead |
| 5. Shorter descriptions | All | No | **Recommend**, eval-guarded |
| 6. Client-side deferral | 4 of 7, free | No | Support it with names, instructions, `alwaysLoad` |

## 4. Recommendation for 0.5.0

**Claim: build toolsets chosen per token or connection first, then put every tool on a description diet with the
core search tools pinned; keep the existing `connapse_api_*` tools as the long-tail escape hatch and do not add
mid-session tool loading.**

1. **Toolsets per token or connection** (4.1). Every tool belongs to one group, and each connection gets only the
   groups it asks for. Strongest reason: it is the only option that cuts tokens and choices in all seven clients
   without touching per-tool permission prompts, and the per-token trimming it builds on already exists.
2. **Shorter, search-ready descriptions with core tools pinned** (4.2). Strongest reason: in the four clients that
   defer tools themselves, the description *is* the search index, and Codex has open bugs where search misses
   tools; everywhere else it is a straight token cut.

### 4.1 Toolsets: how it works

**In short: each tool belongs to one group (a toolset); a token and a connection URL each say which groups they
want, and `tools/list` returns only the tools in those groups. Toolsets decide what an agent sees; scopes
(`knowledge:read`, `knowledge:write`) still decide what it may do.**

The groups, for the 28 tools left after CON-344 (o200k tokens from today's list):

| Toolset | Tools | Tokens |
|---|---|---|
| `search` (always included) | `container_list`, `search_knowledge`, `list_files`, `get_document` | about 900 |
| `documents` | `container_describe`, `container_stats`, `container_create`, `container_delete`, `upload_file`, `delete_file`, `append_to_file`, `approval_status`, `deletion_status` | about 1,450 |
| `memory` | `memory_spaces`, `memory_space_create`, `memory_remember`, `memory_correct`, `memory_forget`, `memory_recall`, `memory_timeline` | 2,313 |
| `code` | `code_find_symbol`, `code_references`, `code_impact`, `code_repo_map` | 911 |
| `admin` | `connapse_api_search/read/write`, `request_deletions` | 689 |

`approval_status` and `deletion_status` sit in `documents`, not `admin`, because a delete that needs a person's
approval comes back as a held batch that the agent can only follow with those two tools (`McpApprovalGate.cs`).
Without them, an agent allowed to delete files could start a delete it can never check on.

`search` is part of every selection, even one that names only `code` or `memory`, so `search_knowledge` and
`get_document` are always present for 4.2 to pin. The CON-344 merges may add a few dozen tokens to `documents`.

**How a connection picks its groups.** The choice can be made in two places, and the server uses the groups that
both allow:
1. **On the token.** Personal access tokens and agent keys get a `Toolsets` setting next to `Scopes`: checkboxes on
   the token pages and a `toolsets` field on the REST create calls. A new token starts with only `search` ticked.
   An empty setting means all groups, so every token that exists today keeps its full list.
2. **In the URL.** A connection can narrow its list with a path, as GitHub's server does with `/x/{toolset}`:
   `https://connapse.example/mcp/x/code,memory`. Plain `/mcp` means all groups. This is the only way to choose
   for the apps that sign in with OAuth (Claude Desktop and claude.ai, ChatGPT, Cursor, VS Code), because their
   users never see Connapse's token page; they paste the URL when they add the connector. The OAuth flow already
   accepts a path under `/mcp`: discovery metadata is served for any sub-path
   (`/.well-known/oauth-protected-resource/{**resourcePath}`), and the access-token audience check accepts any URL
   on the same host.

**Where it is stored and checked.**
- **Stored:** a new `Toolsets` text column on `PersonalAccessTokenEntity` and `AgentApiKeyEntity` (one Identity
  migration), a comma list like `Scopes`. OAuth grants store nothing new, because the URL carries the choice on
  every request.
- **Carried:** `ApiKeyAuthenticationHandler` already turns a token's `Scopes` into `scope` claims, and it would turn
  `Toolsets` into `toolset` claims the same way.
- **Checked:** every tool already has an `[Authorize(Policy = "Scope:…")]` attribute. `AddAuthorizationFilters()` in
  `Program.cs` uses those attributes to leave tools out of `tools/list` and to refuse `tools/call` for them, which
  is how a read-only key gets 17 tools today. Each tool gets a second attribute, such as
  `[Authorize(Policy = "Toolset:memory")]`. A new `ToolsetAuthorizationHandler` passes it when the group is `search`,
  or when the group is allowed both by the `toolset` claims (none means all) and by the URL path (none means all).
  Both attributes must pass, so a tool is listed only when the token may use it and the connection asked for it.

**What the server sends to the agent's app.** The same `tools/list` response as today, only shorter. Each tool's
name, description, input schema and annotations are unchanged; `search_knowledge` and `get_document` also carry
`_meta: {"anthropic/alwaysLoad": true}` (4.2). The list already differs by caller and is marked
`cacheScope: private` (#846), so a list that differs by URL needs no new caching rules. The server instructions
get one fixed sentence: tools come in groups (search, documents, memory, code, admin), and if a tool the user needs
is missing, this connection does not include its group, which a person can add on the token page or in the URL.

**When an agent needs a tool outside its groups.** The agent cannot see the tool, and calling it by name is
refused the same way a call outside the token's scopes is refused today. Nothing loads mid-session, because that
fails in every client (section 2). The agent tells the user what is missing, prompted by the sentence in the
instructions. A person then widens the choice by ticking the group on the token page or changing the URL, and the
app picks it up on its next fetch of the tool list, which for most apps means reconnecting or starting a new
session. For one-off admin operations, the `connapse_api_*` tools in `admin` still reach every REST operation.

**What changes in Connapse's code.**
- `Connapse.Identity`:
  - the `Toolsets` column on the two token entities, plus a migration;
  - `toolset` claims in `ApiKeyAuthenticationHandler`;
  - `ToolsetRequirement`, `ToolsetAuthorizationHandler` and five `Toolset:*` policies, beside the `Scope:*`
    policies in `IdentityServiceExtensions`.
- `Connapse.Web`:
  - a `Toolset:*` attribute on every tool in `Mcp/McpTools*.cs` and `Mcp/McpApiTools.cs`;
  - a second route, `/mcp/x/{toolsets}`, beside `app.MapMcp("/mcp")` with the same authorization and rate limit;
  - the `toolsets` field on the token create endpoints and the checkboxes on the token pages;
  - the sentence in `McpServerConfig`.
- **Tests:**
  - extend `McpPerTokenToolListTests` to cover the choice on the token, the choice in the URL, the overlap of
    the two, and `search` always being present;
  - add a test that every tool has exactly one `Toolset:*` attribute, so no new tool can skip its group.

**Example.** A developer adds Connapse to Claude Desktop to get help with a codebase. They sign in as an Editor
with the URL `https://connapse.example/mcp/x/code`.
- **Today:** Claude Desktop loads all 28 tools, about 6,300–6,500 tokens, on every message. That includes seven
  memory tools and six admin tools this person never uses.
- **With toolsets:** it loads `search` and `code`: `container_list`, `search_knowledge`, `list_files`,
  `get_document`, `code_find_symbol`, `code_references`, `code_impact` and `code_repo_map`. That is 8 tools and
  about 1,800 tokens (900 + 911): roughly 70% fewer tokens, and 8 choices instead of 28.
- **A request outside the groups:** the developer asks the agent to "remember that we moved to PostgreSQL 16". The
  agent has no memory tool, so it says this connection does not include memory. The developer changes the URL to
  `/mcp/x/code,memory` and reconnects, and the list grows to 15 tools, about 4,100 tokens.

#### When an agent needs several groups

An agent can have any combination of groups; picking more than one is the normal case, not an exception.

**How to pick several.** In the URL, list the groups with commas: `/mcp/x/documents,memory`. On the token, tick
more than one box. Both places also offer `all`: `/mcp/x/all` in the URL or an "All groups" box on the token.
`all` differs from ticking every box in one way: it also takes in groups added in later releases, while a list
stays fixed to what was named. Plain `/mcp` and an empty token setting both mean `all`.

**What each combination costs.** These totals include `search`, which every selection gets (o200k tokens). The
server's instructions add 273 tokens to every list.

| Groups chosen | Tools | Tokens |
|---|---|---|
| `admin` | 8 | about 1,600 |
| `code` | 8 | about 1,800 |
| `documents` | 13 | about 2,400 |
| `memory` | 11 | about 3,200 |
| `documents,code` | 17 | about 3,300 |
| `code,memory` | 15 | about 4,100 |
| `documents,memory` | 20 | about 4,700 |
| `documents,memory,code` (everything except `admin`) | 24 | about 5,600 |
| `all` | 28 | about 6,300 |

`memory` is the expensive group: any selection that includes it costs at least 3,200 tokens. Section 4.2's
description cuts fall hardest on `memory`, so they shrink every row that has it.

**Groups that ride together.** Two pairings are built in, and no others are needed:
- `search` comes with every selection, because the other groups assume the agent can find documents. Memory facts
  cite document IDs that the agent gets from `search_knowledge`; the code tools return file paths that the agent
  opens with `get_document`.
- The two approval tools live in `documents` (see the table above), so an agent that can delete can follow its
  held deletes.

`code` and `memory` do not depend on `documents` or on each other, so they stay separate.

**A preset for "everything except admin".** The token page should offer this as a one-click choice, and it should
be the suggested choice for agents shared by a team. It gives 24 tools for about 5,600 tokens and leaves out
`connapse_api_write`, which can call any REST endpoint the token allows, and `request_deletions`, which deletes in
bulk. Those are the two tools whose mistakes cost the most, and most agents never need them. In the URL the same
choice is written out as `/mcp/x/documents,memory,code`; no separate keyword is needed.

**When an agent's needs change mid-task.** The list cannot grow during a session. MCP lets a server tell the
client that its list changed (`notifications/tools/list_changed`), but the groups come from the token and the
URL, and neither changes while the agent is connected. So the agent says the tool is missing (the server's
instructions name the groups this connection lacks), the person widens the URL or the token setting, and the app
reconnects and fetches the new list. Claude Code, and other apps that load tool descriptions only when the agent
searches for them, are the exception: for them, `all` costs little, so they need no narrow selection to begin
with.

**Example with three groups.** A team runs a shared assistant in Claude Desktop that files meeting notes, answers
questions from past notes, and remembers decisions. It connects with `https://connapse.example/mcp/x/documents,memory`.
- **It gets** `search`, `documents` and `memory`: 20 tools and about 4,700 tokens, against about 6,300 for all 28.
  It can upload notes (`upload_file`), find them (`search_knowledge`), save a decision with the note's ID as its
  source (`memory_remember`), and check on a held delete (`deletion_status`).
- **It does not get** `code` or `admin`. It cannot call arbitrary REST endpoints or bulk-delete, which suits an
  agent everyone on the team talks to.
- **A new need.** A developer asks it which services call the billing API. That needs `code_references`, which
  it lacks, so it says so. The team adds `code`: the URL becomes `/mcp/x/documents,memory,code`, the app
  reconnects, and the list grows to 24 tools and about 5,600 tokens. That is still 700 tokens fewer than `all`,
  and `connapse_api_write` and `request_deletions` stay out.

### 4.2 Shorter descriptions: how it works

**In short: rewrite each tool's text so its first words say what it is for, in words a user would search with;
cut whatever the agent does not need to pick and call it; and pin the two core search tools so apps that load
tools on demand never have to search for them.**

What gets cut, largest saving first:
1. **"Or null" markings on optional parameters.** The C# MCP SDK writes every optional parameter as
   `"type": ["string", "null"], "default": null`. A parameter missing from `required` is already optional, so
   plain `"type": "string"` says the same thing. Removing these markings takes today's list from 6,740 to 6,408
   tokens (332 fewer, 5%) without changing a word. It is done once at startup, by rewriting each tool's input schema
   (or through the SDK's schema-creation options), not tool by tool.
2. **Description text.** Lead with the purpose and keep one example. Advice about using the result moves into the
   result itself; Connapse already does this in places, such as `memory_space_create`'s "Save facts here with
   memory_remember…" note.
3. **Deprecated parameters.** Old aliases, such as `memory_remember`'s `containerId`, stay accepted by the server
   for one release but leave the advertised schema.
4. **Parameter descriptions that repeat the name**, such as `value`: "The value", are dropped.

**Pinning.** `search_knowledge` and `get_document` get `_meta["anthropic/alwaysLoad"] = true`, so Claude Code keeps
them loaded even when its tool search defers the rest; other apps ignore the field. Existing tools are not renamed,
because a rename breaks saved agent prompts and permission rules. New tools take their group's prefix (`code_`,
`memory_`).

**Where it lives in the code.** The text is the `[Description]` attributes in `Mcp/McpTools*.cs`, and the
null-marking cleanup is a few lines in `Program.cs`'s MCP setup. A unit test counts the `tools/list` output with
`o200k_base` (`Microsoft.ML.Tokenizers` has it), so the 30% bar in section 5 is checked on every build and later
tools cannot quietly regrow the list.

**Example: `memory_remember`, the most expensive tool (o200k tokens, compact JSON).**

| | Before | After |
|---|---|---|
| Whole tool | 499 | 275 (45% fewer) |
| Description | 172 | 92 |
| Input schema | 303 | 162 |

Description before:
> Store one fact in a memory space: a subject ("Halcyon"), an attribute ("database") and a value ("PostgreSQL 15"),
> with the date it became true and the documents you learned it from. Name a `space` that exists: memory_spaces
> lists yours, and memory_space_create makes one if none fits. No space is made for you. Save to a personal space if
> it is about the user, their preferences or their own work; to a team space if it would be true and useful for
> others. If unsure, ask. Cite the document IDs that `search_knowledge` or `get_document` gave you: a fact citing
> sources is shown only to people who can read all of them. The answer says who can see what you saved. When a value
> you stored before has changed, call `memory_correct` instead.

Description after:
> Save a fact to long-term memory as subject, attribute and value ("Halcyon", "database", "PostgreSQL 15"). Use a
> personal space for facts about the user, a team space for facts others need; ask if unsure. Pick the space with
> memory_spaces. Put the document IDs you learned it from in `sources`: the fact is then shown only to people who
> can read them. If a saved value changed, use memory_correct.

Schema changes in that tool:
- **`space`:** `"type": ["string","null"], "default": null` becomes `"type": "string"`, and the same change applies
  to the other five optional parameters.
- **`containerId`:** the deprecated alias leaves the schema.
- **`value`:** loses "The value".
- **Other parameters:** descriptions are cut to their core, for example `validFrom` becomes "Date it became true,
  YYYY-MM-DD".

The facts the agent needs stay: what to save, which space, citing sources, and when to use `memory_correct`
instead. Whether the shorter text keeps answers as good is what the test plan checks.

**Not now:**
- **Mid-session tool loading** (`list_changed` or an `enable_toolset` tool): reliable in no client, and the
  stateless spec gives it nowhere to keep state.
- **A generic dispatcher for curated tools:** defeats per-tool approval everywhere and fails Anthropic's directory
  rule. The existing `connapse_api_search/read/write` stay as they are, for the long tail of admin operations,
  because they already split read from write and route destructive calls through `McpApprovalGate`.
- **Broad tools with an action parameter** beyond the near-twins (CON-344): only merge tools of the same risk
  class, so `readOnlyHint` stays true for every action of a read tool.
- **Separate MCP servers per area:** the toolset URL path gives the same split with one sign-in; separate servers
  would make users connect and authorise up to five times.

## 5. Test plan

**Claim: each recommendation ships only if it cuts tool tokens by a bar fixed now and keeps agent answers
non-inferior on our agent harness, measured with client-side tool search both off and on.**

Harness: the `memory-search` agent command in `tests/Connapse.Eval` (a headless Claude Code session given the real
MCP tool descriptions through a loopback server; see `tests/Connapse.Eval/README.md`). Two additions are needed
before any run, both test code:
- **Arms by tool list.** Today the harness offers only `search_knowledge` and `get_document`. Add a `--toolset`
  option that offers the full post-CON-344 list, the default "search" toolset, or the trimmed-description list.
- **A routing set.** `memory-search` only asks document questions. Add a small held-out set (about 60 tasks: 20
  document, 20 code, 20 memory) whose gold answer names the tool that must be called, built and frozen before any
  run, with a dev split for building the command.
- **Tool search pinned per arm.** The harness does not set `ENABLE_TOOL_SEARCH`, and it starts Claude Code with
  `--tools ""` (`ClaudeCliAgent.cs`), which removes every built-in tool, ToolSearch included, so no tool is
  deferred today. Every arm runs twice: `ENABLE_TOOL_SEARCH=false` (stands in for Desktop, ChatGPT and Gemini CLI,
  which load everything) and `true` (stands in for Claude Code, Codex, Cursor and VS Code). In the `true` arm the
  harness must allow ToolSearch (`--tools ToolSearch`, with `ToolSearch` added to `--allowedTools`); every other
  built-in tool stays off. A `true` run that records no ToolSearch call over the whole task set is invalid and is
  rerun after fixing the setup, not scored.

Fixed for every arm: model `claude-sonnet-5-5` at effort `medium`, 3 repetitions per task, at most 12 tool calls,
`test` split scored once, the same commit and machine; a provider error is retried 3 times and a run that still
fails scores 0. A gain counts only under the harness's Holm-corrected paired permutation test at p < 0.05.
"Non-inferior within 0.05" follows `eval/pass-bars.md`: the bar passes only when the lower end of the 90% BCa
interval of the paired difference (new minus current) is above −0.05. An interval that reaches −0.05 or below
fails, whether the change is worse or the run is too small to tell.

Bars, fixed on 2026-11-12 before any run:

| Change | Tool tokens | Right answers | Reported, no bar |
|---|---|---|---|
| **Toolsets per token** (default "search" toolset vs the full post-CON-344 list) | Default toolset's `tools/list` at most 25% of the full list's tokens, by `o200k_base` and by Anthropic's token-counting endpoint | `memory-search` test score non-inferior within 0.05 in both tool-search modes; routing set: the in-toolset tasks' right-tool-first rate non-inferior within 0.05 | Total input tokens per task, tool calls per task |
| **Description diet** (trimmed descriptions vs current, same tools) | Description and schema text at least 30% fewer tokens over all tools | `memory-search` non-inferior within 0.05 and routing set's right-tool-first rate non-inferior within 0.05, in both tool-search modes; with tool search on, no task where the agent never finds the right tool more often than today | Tool-search calls per task (tool search on) |

Rules carried over from `eval/pass-bars.md`: a change that fails is redesigned and judged on a fresh held-out set,
and a bar is never changed after a result exists. The token bars are checked by a unit test over `tools/list`
output, so they also stop later features from quietly growing the list.

Gaps in this plan: the harness drives Claude Code only, so ChatGPT, Codex, Cursor and Gemini CLI are represented by
the two tool-search modes, not run directly; and permission prompts are not measured, because the harness allows
every tool.

## Conflicts and uncertainties

- **Claude Code `list_changed`:** the docs say the CLI refreshes tools; issue #66084 (still open) says the tool-search
  index is not updated, and a maintainer said on 2026-08-15 it works once the server advertises `listChanged`.
- **VS Code `list_changed`:** its token-efficiency blog says mid-session changes are "reflected immediately"; #303012
  says they are not searchable until the next user message (closed with no stated fix).
- **Cursor tool cap:** the 40-tool cap is in 2025 forum quotes of old docs; current docs state none. One user reports
  80+ tools working, with no staff confirmation.
- **ChatGPT confirmations:** the developer guide says write actions require confirmation by default; a help page
  (403 to us, quoted in search results) is looser.
- **Token counts are `o200k_base`, not Claude's tokenizer.** Absolute numbers for Claude will be higher; ratios
  between areas should hold.
- **Codex release date:** tool search is "always enabled" on `main`, but the release that shipped it was not found.

## Gaps — what we did not find

- No published accuracy numbers for lists of 20–30 tools; vendor numbers are for 50+ tools or for built-in tools.
- No documented tool search in Claude Desktop or claude.ai chat, ChatGPT or Gemini CLI.
- No evidence that Cursor uses `readOnlyHint`.
- Antigravity CLI (Gemini CLI's successor for some users) was not researched.

## Source quality assessment

The client matrix rests almost entirely on primary sources: vendor documentation, source code (Codex `lib.rs`) and
issue trackers with maintainer replies. Cursor is the weak spot: its approval and `list_changed` behaviour comes
from forum threads, some without staff replies. The token measurements are first-hand from Connapse's own
`tools/list`. Accuracy figures are vendor-published (Anthropic, GitHub, Cursor, VS Code) and measured on their own
tasks, so they show direction, not what Connapse will see.

## Sources

**First-hand:** Connapse `tools/list` at `main` 24e6a7b7, counted with tiktoken 0.14.0 `o200k_base` and
`cl100k_base`; `src/Connapse.Web/Mcp/`; `tests/Connapse.Integration.Tests/Authorization/McpPerTokenToolListTests.cs`;
`tests/Connapse.Eval/README.md`; `eval/pass-bars.md`.

**Primary: client documentation, source and issue trackers**
- Claude Code: https://code.claude.com/docs/en/mcp, https://code.claude.com/docs/en/permissions,
  https://github.com/anthropics/claude-code/issues/66084, https://github.com/anthropics/claude-code/issues/88483
- Claude Desktop and claude.ai: https://claude.com/docs/connectors/custom/remote-mcp,
  https://claude.com/docs/connectors/building/review-criteria, https://claude.com/docs/connectors/building/index,
  https://github.com/anthropics/claude-code/issues/88172, https://github.com/anthropics/claude-code/issues/92858
- Anthropic API: https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool,
  https://platform.claude.com/docs/en/agents-and-tools/mcp-connector
- Cursor: https://cursor.com/docs/context/mcp, https://cursor.com/docs/agent/security/run-modes,
  https://cursor.com/blog/dynamic-context-discovery
- VS Code Copilot: https://code.visualstudio.com/docs/agents/run/tools,
  https://code.visualstudio.com/docs/agents/run/approvals, https://code.visualstudio.com/docs/agents/reference/ai-settings,
  https://code.visualstudio.com/api/extension-guides/ai/mcp, https://code.visualstudio.com/updates/v1_103,
  https://code.visualstudio.com/blogs/2026/06/17/improving-token-efficiency-in-github-copilot,
  https://github.com/microsoft/vscode/issues/303012, https://github.com/microsoft/vscode/issues/256521,
  https://github.com/microsoft/vscode/issues/319330, https://github.com/microsoft/vscode/issues/341709,
  https://github.com/microsoft/vscode/issues/270618
- ChatGPT and OpenAI API: https://developers.openai.com/api/docs/guides/custom-mcp-server,
  https://developers.openai.com/api/docs/guides/tools-connectors-mcp,
  https://developers.openai.com/api/docs/guides/tools-tool-search.md,
  https://developers.openai.com/api/docs/guides/function-calling, https://developers.openai.com/api/docs/mcp
- Codex: https://learn.chatgpt.com/docs/extend/mcp?surface=cli,
  https://github.com/openai/codex/blob/main/codex-rs/features/src/lib.rs, https://github.com/openai/codex/issues/10105,
  https://github.com/openai/codex/issues/21503, https://github.com/openai/codex/issues/56973,
  https://github.com/openai/codex/issues/33608
- Gemini CLI: https://geminicli.com/docs/tools/mcp-server/, https://github.com/google-gemini/gemini-cli/pull/14375,
  https://github.com/google-gemini/gemini-cli/pull/21050, https://github.com/google-gemini/gemini-cli/issues/25650,
  https://github.com/google-gemini/gemini-cli/issues/29041
- MCP spec: https://modelcontextprotocol.io/specification/2025-11-25/server/tools,
  https://modelcontextprotocol.io/specification/2026-07-28/changelog,
  https://github.com/modelcontextprotocol/modelcontextprotocol/pull/3376
- GitHub MCP server: https://github.com/github/github-mcp-server/blob/main/README.md,
  https://github.com/github/github-mcp-server/blob/main/docs/remote-server.md,
  https://github.com/github/github-mcp-server/blob/main/docs/server-configuration.md,
  https://github.com/github/github-mcp-server/pull/2512, https://github.com/github/github-mcp-server/issues/275,
  https://github.com/github/github-mcp-server/issues/2525, https://github.com/github/github-mcp-server/pull/3791,
  https://github.com/github/github-mcp-server/issues/3501,
  https://github.blog/changelog/2025-10-14-github-mcp-server-now-supports-github-projects-and-more/,
  https://github.blog/changelog/2026-01-28-github-mcp-server-new-projects-tools-oauth-scope-filtering-and-new-features/

**Primary: vendor engineering posts (numbers measured on the vendor's own tasks)**
- https://www.anthropic.com/engineering/advanced-tool-use
- https://www.anthropic.com/engineering/writing-tools-for-agents
- https://www.anthropic.com/engineering/code-execution-with-mcp
- https://blog.cloudflare.com/code-mode-mcp/
- https://engineering.block.xyz/blog/blocks-playbook-for-designing-mcp-servers
- https://amplitude.com/blog/lessons-from-consolidating-our-mcp-server
- https://github.blog/ai-and-ml/github-copilot/how-were-making-github-copilot-smarter-with-fewer-tools/

**Secondary: papers and forum threads**
- https://arxiv.org/abs/2505.03275 (RAG-MCP), https://arxiv.org/abs/2411.15399 (Less is More),
  https://arxiv.org/abs/2602.14878 (MCP tool description smells), https://arxiv.org/html/2508.01780v2 (LiveMCPBench)
- Cursor forum: https://forum.cursor.com/t/tools-limited-to-40-total/67976,
  https://forum.cursor.com/t/regarding-the-quantity-limit-of-mcp-tools/153432,
  https://forum.cursor.com/t/mcp-server-regression-does-not-reload-tools-disconnect-does-nothing-ignores-mcp-json-changes-etc/166216,
  https://forum.cursor.com/t/mcp-notifications-tools-list-changed-not-acted-on-mid-session/161459,
  https://forum.cursor.com/t/mcp-allowlist-entries-are-written-but-never-matched-every-mcp-call-re-prompts/169566,
  https://forum.cursor.com/t/add-the-ability-to-pre-configure-which-mcp-tools-are-enabled-disabled-via-the-mcp-json-configuration-file-eliminating-the-need-to-manually-enable-tools-in-the-ui-after-every-cursor-restart/148139
- https://www.speakeasy.com/blog/how-we-reduced-token-usage-by-100x-dynamic-toolsets-v2 (read only in search
  snippets; medium confidence)
