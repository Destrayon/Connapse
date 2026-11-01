# CON-198: What gets an open-source repo found and starred, and the Connapse 4.0 launch plan

**Date:** 2026-11-01 · **Status:** Proposed · **Feeds:** the 4.0 launch work (README, GIFs, repo settings, docs, launch day)

People find a GitHub repo through four things: its name, its description, its topics, and outside links (Hacker News, awesome lists, newsletters). GitHub's repo search does not read the README unless the searcher asks it to [GH1]. Once people land, the README's first screen decides whether they understand the project. In most of the 13 comparable repos below, the first screen is a logo, a one-line category claim, a few badges and a product image, followed by a quickstart of one to five commands. Major-version announcements barely register on Hacker News (1–7 points). Posts that pitch the product in the founder's voice score 167–565 points [L1–L9]. So 4.0 should launch as "what you can now do with Connapse", not as "v4.0 is out".

Connapse today has the right category (self-hosted, MCP, RAG). Its weak points are the opening line, the 2026-03 GIFs, a repo description that undersells it ("5 connectors"), no homepage link, and a changelog that stops at v0.3.2. GitHub traffic for the 14 days to 2026-11-01 was 235 views (203 unique visitors), and Google sent 5 of those visitors [C1].

Facts about Connapse come from `main` at `3eeb409d` and from `origin/epic/con-e3-links-and-memory` (the links-and-memory epic, which CON-197 is bringing to main). Facts about other repos come from `gh api` on 2026-11-01.

**Decision needed from the user: the version label.** The repo milestone is `v0.4.0`, but this ticket says "4.0". Every comparable project that put a version number in its launch headline got 1–7 HN points [L1][L5][L6]. Settle the label before the README, the release title and the social preview are made, because all three show it.

## 1. Connapse's GitHub page compared with 13 similar projects

| Repo | Stars | Description (shortened) | Custom social preview | Hero visual | Quickstart | MCP in README |
|---|---|---|---|---|---|---|
| **[Destrayon/Connapse](https://github.com/Destrayon/Connapse)** | 21 | "Open-source, self-hosted knowledge backend for AI agents — hybrid search…, MCP server, 5 connectors, Docker-ready" | Yes (logo, "Knowledge Backend for AI Agents", `docker compose up -d`) | 720-px GIF under 9 badges, recorded 2026-03 | 5 commands | Its own section, plus 2 collapsed GIFs |
| [langgenius/dify](https://github.com/langgenius/dify) | 160,171 | "Build Agentic workflows, RAG pipelines…" | Yes | Full-width cover PNG | 4 commands, with hardware stated | One clause |
| [infiniflow/ragflow](https://github.com/infiniflow/ragflow) | 93,302 | "…leading open-source RAG engine… superior context layer for LLMs" | No | Two GIFs, just below the first screen | ~5 commands | None |
| [mem0ai/mem0](https://github.com/mem0ai/mem0) | 68,242 | "The Memory Layer for AI Agents…" | No | Banner PNG, then a benchmark table | 2–4 commands | None (sells through CLI and agent skills) |
| [Mintplex-Labs/anything-llm](https://github.com/Mintplex-Labs/anything-llm) | 67,829 | "Stop renting your intelligence…" | Yes | 720p GIF on the first screen | 11 one-click deploy buttons | One bullet |
| [HKUDS/LightRAG](https://github.com/HKUDS/LightRAG) | 40,468 | "[EMNLP2025] LightRAG: Simple and Fast RAG" | No | Diagram PNG under ~12 badges | 3 commands | None |
| [The-Vibe-Company/quivr](https://github.com/The-Vibe-Company/quivr) | 39,623 | "…turns continuous content streams into search and monitoring…" | Yes | Mermaid diagram, no image | 3 commands | One bullet (`quivr mcp`) |
| [khoj-ai/khoj](https://github.com/khoj-ai/khoj) | 37,931 | "Your AI second brain. Self-hostable…" | Yes | GIF mid-page | Links to the docs | None |
| [onyx-dot-app/onyx](https://github.com/onyx-dot-app/onyx) | 33,150 | "Open Source AI Platform…" | Yes | PNG under a one-line install | 1 command (`curl … \| bash`) | Feature and contrast: "same access controls as the person running them" |
| [Cinnamon/kotaemon](https://github.com/Cinnamon/kotaemon) | 25,846 | "An open-source RAG-based tool for chatting with your documents." | No | PNG directly under the title | 1 `docker run` | None |
| [neuml/txtai](https://github.com/neuml/txtai) | 13,058 | "All-in-one AI framework for semantic search…" | Yes | Light and dark diagrams | 1 install + 4 lines of code | One bullet |
| [SciPhi-AI/R2R](https://github.com/SciPhi-AI/R2R) | 8,054 | "SoTA production-ready AI retrieval system…" (no push since 2025-11) | No | Screenshot PNG first | 3 commands | None |
| [basicmachines-co/basic-memory](https://github.com/basicmachines-co/basic-memory) | 4,440 | "AI conversations that actually remember…" | No | None at the top | 1 command, then client config | It is the product: MCP badges, a table of clients, every tool listed |
| [dluc/kernel-memory](https://github.com/dluc/kernel-memory) (formerly microsoft/) | 2,233 | "Research project. A Memory solution…" | No | Architecture PNG | 1 `docker run` | None |

**What the well-starred repos share, and where Connapse stands:**

1. **The first screen is centred: logo, a one-line category claim, a row of badges, a product picture.** Most of them open with a logo, banner or screenshot; Quivr and Kernel Memory are the exceptions. The claim names a category: "memory layer", "RAG engine", "AI second brain". Connapse's tagline, "Stop losing context between AI sessions", describes a pain but never says *what Connapse is*. The words "knowledge base", "RAG" and "MCP" first appear in the body paragraph.
2. **Install is one block of one to five commands.** Connapse's 5 commands are within range. Onyx and Kotaemon get it down to one, and Connapse ships a prebuilt image in `release/` that the README never mentions.
3. **MCP is usually a footnote.** Only Onyx and Basic Memory sell it, and both name the clients they reach and say permissions carry through. Connapse's per-user permission trimming over MCP is a claim only Onyx also makes. An MCP-first README stands out in this group, it doesn't follow it.
4. **No peer compares itself feature by feature with named competitors.** Proof comes as benchmark tables (mem0, LightRAG) or as a choice of editions (Dify, mem0). Connapse has retrieval-eval numbers in `tests/Connapse.Eval` that it could show the same way.
5. **The .NET slot is empty.** Kernel Memory, the only .NET peer, is now an archived research project on a personal account, with its NuGet packages retired. Only 371 public repos carry both the `rag` and `dotnet` topics [T1]. "The self-hosted RAG and MCP server for .NET shops" is a position nobody else holds.
6. **Social previews:** 6 of the 13 upload a custom image. Connapse already has one.
7. **Hosted first:** five of the six repos in the first comparison batch (RAGFlow, AnythingLLM, Onyx, Khoj, Kotaemon and Basic Memory) point visitors to a cloud trial before self-hosting. Whether the README mentions a hosted Connapse is the user's call.

## 2. Search terms and where each should appear

Counts come from GitHub topic search (`topic:X`) and HN story titles since 2025-01-01 [T1]. They measure developer attention, not Google volume; see [Gaps](#9-conflicts-and-gaps).

| Term | Evidence | Where it goes |
|---|---|---|
| **MCP server** | `mcp` 110,959 repos, `mcp-server` 41,986, `model-context-protocol` 43,116; 1,203 HN titles | Tagline, description, topics (all three), README H2, release title |
| **RAG** | `rag` 61,067 (a GitHub featured topic), `retrieval-augmented-generation` 9,558; 980 HN titles | Description, topics (both). Not the tagline: top HN threads now argue against plain RAG ("The RAG Obituary", 290 points) |
| **self-hosted** | `self-hosted` 51,093 (featured topic); 1,397 HN titles | Tagline, description, topic |
| **knowledge base** | `knowledge-base` 8,419; 114 HN titles | Tagline, description, topic, docs titles |
| **agent memory / memory for AI agents** | `agent-memory` 5,208, `ai-memory` 2,486; "memory for AI agents" in 1,390 repo descriptions; 206 HN titles | README H2 "Memory for agents", topic, the memory concept page title |
| **semantic search** | 9,869 repos; 138 HN titles | Topic, the search concept page |
| **hybrid search** | 3,072 repos; 41 of the 367 Knowledge & Memory entries in awesome-mcp-servers say "hybrid" | Description, topic, README H2 |
| **vector search** | 7,906 repos | Topic only. Connapse isn't a vector database |
| **pgvector** | 6,488 repos; 4,336 tagged `rag` too | Description ("pgvector"), topic, install page |
| **reranking** | 1,268 repos | Description, topic, tune-search guide title |
| **context engineering** | 4,203 repos; 173 HN titles, rising in 2025–26 | Topic, one README line, launch blog title |
| **local RAG / Ollama** | "Ask HN: How are you doing RAG locally?" got 413 points | README H2 "Run fully local with Ollama", topic `ollama` |
| **.NET / C# / Blazor** | `dotnet` 69,631, `csharp` 112,304, `blazor` 6,265, but `rag`+`dotnet` only 371 | Topics, description ("Built on .NET 10"), README H2, the r/dotnet post |
| **permission-aware search** | No shared vocabulary yet (2 HN titles) | README H2 "Search trimmed to what each person can open". Connapse gets to coin the term here |
| **NotebookLM / Glean alternative** | 123 / 0 HN titles; the web results are vendor comparison pages | At most one comparison page in the docs. Never in the tagline |
| **chat with your documents** | 859 repo descriptions, 4 HN titles | Avoid: Connapse ships no chat UI, so it would mislead |

## 3. Repo description, topics and homepage

GitHub search matches the name, the description and the topics [GH1]. Google writes snippets from the page text, so the description should be one plain sentence, not a list of keywords [GO1]. The current description's "5 connectors" is wrong: there are now nine kinds (uploads, filesystem, SFTP, S3, Azure Blob, GitHub, Confluence, Jira and custom sources).

**Description:**

> Self-hosted knowledge base and memory for AI agents. MCP server and REST API with hybrid search, reranking and per-user permissions over your files, S3, Azure Blob, GitHub, Confluence and Jira. RAG on .NET 10 and pgvector.

**Topics (20, the maximum [GH2]):** `mcp-server`, `mcp`, `model-context-protocol`, `rag`, `retrieval-augmented-generation`, `self-hosted`, `knowledge-base`, `agent-memory`, `ai-agents`, `context-engineering`, `semantic-search`, `hybrid-search`, `vector-search`, `reranking`, `pgvector`, `ollama`, `llm`, `dotnet`, `csharp`, `blazor`.

This drops `ai`, `docker`, `document-management`, `embeddings`, `generative-ai` and `knowledge-management`. They are either too broad to rank in or they bring the wrong audience: "knowledge management" means note-taking tools. It adds `mcp`, `knowledge-base`, `agent-memory`, `reranking`, `context-engineering` and `csharp`.

**Homepage:** set the repo's website field to the docs landing page. Today it is empty, so the sidebar shows no link at all. Until a docs site exists, link to `docs/index.md` on GitHub.

## 4. README outline

Ordered by what the peer repos put on the first screen and by what the README studies tie to stars. The studies found that installation, usage, images, links and a license correlate with stars [R1][R2][R3]. They also found that badges reporting real data (build, tests, coverage) signal quality, while decorative ones don't [R4].

1. **Logo, then an H1 that states the category.** For example: *"Connapse: the self-hosted knowledge base your AI agents search, and remember with, over MCP."* Keep the current pain line ("Your AI agents forget everything between sessions") as the line under it. Memory now ships, so the claim is literally true.
2. **Five badges, all reporting real data:** build, tests, latest release (new), license, Glama MCP score. Drop the stars, issues, "PRs welcome" and "Docker ready" badges, which report nothing.
3. **Hero GIF**: shot 1 below.
4. **Three-sentence what-and-why** that uses the terms from section 2 once each: self-hosted, knowledge base, MCP server, RAG, hybrid search, agent memory, .NET.
5. **Quick start**: keep the 5 commands. Add the prebuilt-image path from `release/` as the shorter route, and say what you see when it works.
6. **Connect an agent**: the `claude mcp add` line. Name the clients covered in [Connect an MCP client](../guides/mcp-clients.md) (Claude Code, Claude Desktop, VS Code, Cursor).
7. **What your agent gets**: a table of MCP tools grouped as Search and documents, Memory, Code, and Containers. Basic Memory shows this works for an MCP-first audience. After CON-197 there are 21 named tools (section 6).
8. **Features**, one H2-level item each, worded with the search terms: Hybrid search with reranking · Memory for agents (dated facts with sources and history) · Connect your data (nine kinds) · Search trimmed to what each person can open · Linked results (opt-in), described by the links Connapse actually stores: a GitHub issue with the PR that closes it and its sub-issues, a Jira issue with its epic, parent and Confluence pages, code with its definitions, imports and calls · Run fully local with Ollama · Built on .NET 10, PostgreSQL and pgvector.
9. **Proof**: one small table from the retrieval eval (for example, hybrid with reranking against vector only on the v1 suite). It must use numbers already published in `docs/research/`, with a link to the run.
10. **Documentation, Support, Contributing, License**: as today.

Leave out: translated READMEs and a star-history chart (each in 2 of 13 peers), a feature table against named competitors (none of the 13 has one), and any "chat with your docs" wording.

## 5. GIF shot list

Keep each GIF under 10 MB, GitHub's upload limit [GH3]. The current hero is 3.7 MB. Record at 1280 px wide and display at 720, as today and as AnythingLLM does. Use a real-looking corpus (the Flink snapshot the eval already pins works). Each GIF shows one flow, loops cleanly, and needs no narration. No study found gives an ideal length, so these lengths come from what the peer GIFs run.

| # | Flow | Length | Where it goes | Replaces |
|---|---|---|---|---|
| 1 | **Agent remembers.** In Claude Code with Connapse connected, the user asks a question. The agent calls `search_knowledge` and answers with citations from a Confluence page and a GitHub issue, then calls `memory_remember` on a decision. Cut to a new session: "what did we decide about X?" → `memory_recall` returns the fact with its source and date. | 20–25 s | README hero | `hero-upload-search.gif` |
| 2 | **From zero to a cited result.** Terminal: `docker compose --profile with-ollama up -d`, then `docker compose exec ollama ollama pull nomic-embed-text` (cut the wait for the pull), then the browser: create the owner account, upload a PDF, search, open a result with its citation. | 15–20 s | Quick start section, and the Quickstart doc | The current hero, re-recorded on the 4.0 UI |
| 3 | **Same question, two people, different answers.** Add a GitHub or S3 source and sync it. Then the same search signed in as two users: the second user's results drop what they can't open in the source. | 15–20 s | "Search trimmed to what each person can open", and the permissions concept page | New |
| 4 | **Memory keeps history.** `memory_correct` with `kind="changed"`, then `memory_timeline` shows the old value ending and the new one starting on a date. | 10–15 s | "Memory for agents", and the memory concept page | New |
| 5 | **Linked results.** With link search on, a search that finds a GitHub issue also brings back the PR that closes it, or one that finds a Jira issue brings back its linked Confluence page. Connapse stores no Jira-to-GitHub links, and a search scoped to one source doesn't reach another, so confirm the pair is in one container and reachable before recording. | 10–15 s | "Linked results" | New. Record only if link search is documented and supported in 4.0, since it ships off by default |
| 6 | **Code tools.** `code_find_symbol`, then `code_impact` on a C# method, in an agent session. | 10–15 s | "What your agent gets" | New |

Retire `settings-providers.gif` from the README; runtime settings are not why people star. Keep `mcp-agent-integration.gif` only if shot 1 isn't ready.

## 6. Social preview image

GitHub's specification is a PNG, JPG or GIF under 1 MB, ideally 1280×640 on a solid background [GH4]. Link previews on X, Slack and LinkedIn crop toward 1.91:1, so text must stay in the central ~1200×600. HN shows no images at all. The current image (1280×640, dark, logo, "Knowledge Backend for AI Agents", `docker compose up -d`) is well made. Update three things:

- **Line one**, the category claim from the new H1: "Self-hosted knowledge base and memory for AI agents."
- **Line two**, three plain-text proof points: "MCP server · hybrid search · per-user permissions". Use no third-party logos, since they are trademarks.
- **Bottom strip**: "Open source · .NET 10 · PostgreSQL + pgvector", plus the version label once it's decided.

Drop the bare `docker compose up -d`: on its own it starts no embedding model (Ollama is behind the `with-ollama` profile), so a reader who copies it gets a failing first search. A preview is read in about a second.

## 7. Docs pages that are wrong or missing for 4.0

**Wrong today, on `main`:**

1. `CHANGELOG.md`. The last entry is v0.3.2 (2026-03-15), so nothing since March is recorded. It needs a 4.0 entry with **Breaking changes** and **Upgrade** sections first; the Connector/Source split and the endpoint changes are the obvious candidates. Mem0's 1.0 notes left breaking changes out and pushed them to a separate migration guide [L2].
2. `README.md:77` says "12 named tools". After CON-197 it is 21: the 5 `memory_*` tools and 4 `code_*` tools are added. The features list mentions none of memory, links or code.
3. `docs/reference/mcp-tools.md:9` has the same count, and the page has no sections for the `memory_*` or `code_*` tools. **Watch in CON-197:** the epic wrote its memory documentation into `docs/mcp-tools.md`, which on `main` is now a "Moved" stub. That text must land in `docs/reference/mcp-tools.md` and the stub must stay a stub.
4. `docs/sources/github-setup.md:17` and `:156` say "Source code isn't indexed". With 4.0's code parsing and code tools, the page must say how code reaches Connapse and what the GitHub source still leaves out.
5. `docs/self-host/install.md:42` pins `v0.3.2` as its example release.
6. The repo description ("5 connectors") and the empty homepage field, as covered in section 3.
7. **GitHub wiki.** 11 old pages are still public, and `Connectors-Guide` was opened 5 times in the 14 days to 2026-11-01 [C1]. It still describes the removed InMemory connector and the old connector-per-container model. Either replace each page's body with a link to its `docs/` replacement, or turn the wiki off. Both are repo-setting changes for the user.
8. **Releases.** The newest GitHub release is `v0.3.3-alpha.1` (2026-03-31), and "Latest" still points at `v0.3.2`. Anyone who arrives from search sees a project that last shipped seven months ago.

**Missing for 4.0:**

9. `docs/concepts/memory.md`: what a fact is (subject, attribute, value, the dates it was true, its sources), why a fact is shown only to people who can read every source it cites, how corrections keep history, and that forgetting really deletes.
10. A page on link search, `docs/concepts/links.md` or a section of `concepts/search.md`. It should cover what links are stored for GitHub, Jira, Confluence and code; that `EnableLinkSearch` is off by default; and how to turn it on. Add the setting to `docs/reference/configuration.md`.
11. Code support in `docs/reference/file-types.md`: C#, TypeScript and Python are parsed with tree-sitter (a code parser), one passage per definition. Also a short guide to the four `code_*` tools.
12. `docs/get-started/connect-an-agent.md`: the tutorial should end with the agent saving and recalling a memory, because that is the 4.0 story.
13. `docs/self-host/upgrade.md`: a "from 0.3.x to 4.0" section. I haven't checked whether it already covers this; whoever does the docs work should check.
14. `docs/llms.txt` (the index file AI agents read, see [GO2]): add the memory, links and code pages.

## 8. How comparable projects launched major versions, and the plan for 4.0

**Evidence [L1–L9]:**

| Launch | What they did | HN result |
|---|---|---|
| Dify 1.0 (2025-02) | Blog post with video 11 days before the tag, 7 named launch partners. The release note had a headline, 5 highlights, an upgrade command and a numbered migration with a backup warning | 2 points and 1 point for the version posts. Their best HN post (185) was a third party's, a year earlier |
| R2R V2 (2024-06) | Show HN framed as "a major update… 3 months intensely" | 251 points; the later "V3 API Release" docs link got 1 |
| Mem0 (2024-09) and 1.0 (2025-10) | Founder Show HN; a public beta tag a month before 1.0 | 201 points; 1.0 had no HN post |
| Danswer / Onyx | Show HN (189), Launch HN (231). Then a silent rename that confused users, fixed with a second Launch HN that opened with a video | 254 points for the relaunch |
| AnythingLLM | v1.0.0 was a bare note. The real launch was a desktop-app Show HN in the same week as Product Hunt | 368 points |
| Khoj | Show HN timed to the Llama 2 news cycle | 565 points |
| Skyvern (data post) | Demo video, a README that stands alone, Discord, feedback from HN members beforehand, written targets | #1 on HN, 420 points, 3k stars |

HN's own rules exclude routine version releases from Show HN, allow a major overhaul, and require an easy trial with no signup [L10]. Across 138 AI-tool launches, the mean star gain was +121 in 24 hours and +289 in a week. Posting time mattered; the "Show HN" tag did not [S2]. HN coverage correlates with significant star gains in two other studies [S1][S3].

**Do before launch day (all in the repo):**

1. Ship sections 3–7: description, topics, homepage, README, GIFs, social preview, docs fixes.
2. Tag a public 4.0 beta 2–4 weeks before GA, as Mem0 did [L2], and fix what it turns up.
3. Write the GA release note on Dify's pattern [L1]: a one-line headline about what people can now do, one GIF, 3–5 highlights (memory, linked results, code tools, permission-trimmed MCP), the upgrade command, numbered migration steps with "back up first", a link to the upgrade doc, then the full changelog.
4. Make "Latest release" point at 4.0 on the same day.

**Launch day: all of this needs the user, because posting anywhere is theirs.**

5. **Show HN in the founder's voice.** For example: "Show HN: Connapse – a self-hosted knowledge base and memory for AI agents (MCP, .NET)". Open with who you are and why you built it, then two or three technical choices (per-user permission trimming over MCP, dated memory facts that are hidden unless the caller can read every source, hybrid search on pgvector), then the trial: the Quick start commands, with no signup. No version number in the title. Don't ask for upvotes [L10]. Stay in the thread all day.
6. **Same day:** posts to r/selfhosted, r/LocalLLaMA ("fully local with Ollama"), r/dotnet (".NET is the empty slot"), and r/mcp. Reddit's reception is unmeasured, see gaps.
7. **Lists and registries:** a PR to [awesome-mcp-servers](https://github.com/punkpeye/awesome-mcp-servers) under "Knowledge & Memory", with the C# and local-service icons. A PR to [awesome-selfhosted](https://github.com/awesome-selfhosted/awesome-selfhosted) under "Generative Artificial Intelligence (GenAI)", where AnythingLLM, Onyx and Khoj are listed; read its inclusion rules first. A PR to [Awesome-RAG](https://github.com/Danielskry/Awesome-RAG) under "Frameworks that Facilitate RAG". Also a listing in the official MCP registry. Connapse is already on Glama.
8. **A long-form post with a demo video** a few days before HN, which Dify and Onyx both did: "Giving AI agents memory that respects permissions" fits the context-engineering conversation.
9. **Optional:** Product Hunt on the same day, as AnythingLLM did. There are no upvote numbers to judge it by.
10. **Expect a fade.** Plan a second piece of content within a few weeks (Lago regretted treating HN as one shot [L8]). Set small written targets in advance, as Skyvern did (for example: 300 stars in the first week, 5 strangers running it).

## 9. Conflicts and gaps

- **How prominent MCP should be.** 11 of the 13 peers treat MCP as a footnote; Onyx and Basic Memory lead with it. This plan leads with it because MCP is Connapse's distribution channel and the largest term in section 2. That is a judgement, not something the peers prove.
- **Whether to say "RAG".** It is the second-largest topic, but HN sentiment is turning against plain RAG. The plan keeps RAG in the description and topics and out of the tagline.
- **The image placement studies are correlations.** They show more stars go with images, install steps and licenses, not that adding them causes stars [R1–R3].
- **Gaps:**
  - How GitHub ranks search results and Trending is unpublished; Trending appears to reward star velocity [T2].
  - There is no Google Trends comparison of the terms.
  - Reddit posts couldn't be fetched (the JSON API was blocked), so the Reddit advice is unmeasured.
  - GitHub now hides stargazer timelines, so every star-lift number is self-reported or comes from a study.
  - There is no data on star lift from awesome lists.
  - There is no evidence on ideal GIF length.
  - There is no source on how LinkedIn or Slack crop previews.
  - Most of Connapse's 166 referrals in the 14 days came from `aocr.org`, an unknown site that is probably a bot, so the traffic numbers are noisy.

## 10. Sources

Tiers: **P** is primary (official docs, peer-reviewed papers, the project's own repo or blog, the HN item itself). **S** is secondary (preprints, maintainer data posts). **T** is tertiary.

- **[GH1]** P. GitHub, Searching for repositories: https://docs.github.com/en/search-github/searching-on-github/searching-for-repositories
- **[GH2]** P. GitHub, Classifying your repository with topics: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics
- **[GH3]** P. GitHub, Attaching files: https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/attaching-files
- **[GH4]** P. GitHub, Customizing your repository's social media preview: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/customizing-your-repositorys-social-media-preview
- **[GO1]** P. Google Search Central, Snippets: https://developers.google.com/search/docs/appearance/snippet
- **[GO2]** P. llms.txt proposal: https://llmstxt.org/
- **[R1]** P. Prana et al., Categorizing the content of GitHub README files: https://arxiv.org/abs/1802.06997
- **[R2]** P. Fan et al., What makes a popular academic AI repository: https://ink.library.smu.edu.sg/sis_research/6713
- **[R3]** S. Venigalla & Chimalakonda, README study of 1,950 repos: https://arxiv.org/abs/2206.10772
- **[R4]** P. Trockman et al., Adding sparkle to social coding (badges): https://par.nsf.gov/servlets/purl/10063054
- **[S1]** S. Effect of HN posts on stars: https://arxiv.org/pdf/1908.04219
- **[S2]** S. 138 AI-tool launches on HN: https://arxiv.org/abs/2511.04453
- **[S3]** S. 1,814 repos shared on HN (ICSME 2025): https://arxiv.org/abs/2506.12643
- **[T1]** P. Topic and phrase counts from `gh api search/repositories` and https://hn.algolia.com/api/v1/search, 2026-11-01
- **[T2]** T. On GitHub Trending: https://dev.to/yvonnickfrin/i-wonder-how-github-s-trending-algorithm-works-any-clue-3ebf
- **[L1]** P. Dify 1.0 release: https://github.com/langgenius/dify/releases/tag/1.0.0 · blog post: https://www.dify.ai/blog/dify-v1-0-building-a-vibrant-plugin-ecosystem · HN: https://news.ycombinator.com/item?id=43218661
- **[L2]** P. Mem0 Show HN: https://news.ycombinator.com/item?id=41447317 · v1.0.0 release: https://github.com/mem0ai/mem0/releases/tag/v1.0.0 · migration guide: https://docs.mem0.ai/migration/v0-to-v1
- **[L3]** P. Danswer Show HN: https://news.ycombinator.com/item?id=36667374 · Launch HN: https://news.ycombinator.com/item?id=39467413 · Onyx Launch HN: https://news.ycombinator.com/item?id=46045987
- **[L4]** P. AnythingLLM Show HN: https://news.ycombinator.com/item?id=41457633
- **[L5]** P. RAGFlow HN: https://news.ycombinator.com/item?id=39896923
- **[L6]** P. R2R Show HN: https://news.ycombinator.com/item?id=39510874 · V2: https://news.ycombinator.com/item?id=40799791 · V3: https://news.ycombinator.com/item?id=42322498
- **[L7]** P. Khoj Show HN: https://news.ycombinator.com/item?id=36933452 · txtai on HN: https://news.ycombinator.com/item?id=41024362 · Kotaemon on HN: https://news.ycombinator.com/item?id=42571272
- **[L8]** P. Skyvern: https://www.skyvern.com/blog/we-open-sourced-and-ended-up-1-on-hackernews/ · PostHog: https://posthog.com/blog/after-the-hn-launch · BookStack: https://www.bookstackapp.com/blog/9000-stars-and-the-effects-of-hacker-news/ · Lago: https://getlago.com/blog/how-we-got-our-first-1000-github-stars
- **[L9]** S. Kotaemon's week on GitHub: https://gitstars.substack.com/p/gittrends-september-1-2024
- **[L10]** P. Show HN guidelines: https://news.ycombinator.com/showhn.html
- **[C1]** P. Connapse traffic for the 14 days to 2026-11-01, from `gh api repos/Destrayon/Connapse/traffic/*`

Peer repo metadata (stars, descriptions, topics, social preview flags) comes from `gh api repos/<owner>/<repo>` and GraphQL `usesCustomOpenGraphImage`, both on 2026-11-01.

[GH1]: https://docs.github.com/en/search-github/searching-on-github/searching-for-repositories
[GH2]: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics
[GH3]: https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/attaching-files
[GH4]: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/customizing-your-repositorys-social-media-preview
[GO1]: https://developers.google.com/search/docs/appearance/snippet
[GO2]: https://llmstxt.org/
[R1]: https://arxiv.org/abs/1802.06997
[R2]: https://ink.library.smu.edu.sg/sis_research/6713
[R3]: https://arxiv.org/abs/2206.10772
[R4]: https://par.nsf.gov/servlets/purl/10063054
[S1]: https://arxiv.org/pdf/1908.04219
[S2]: https://arxiv.org/abs/2511.04453
[S3]: https://arxiv.org/abs/2506.12643
[T1]: https://hn.algolia.com/api/v1/search
[T2]: https://dev.to/yvonnickfrin/i-wonder-how-github-s-trending-algorithm-works-any-clue-3ebf
[L1]: https://github.com/langgenius/dify/releases/tag/1.0.0
[L2]: https://news.ycombinator.com/item?id=41447317
[L3]: https://news.ycombinator.com/item?id=36667374
[L4]: https://news.ycombinator.com/item?id=41457633
[L5]: https://news.ycombinator.com/item?id=39896923
[L6]: https://news.ycombinator.com/item?id=39510874
[L7]: https://news.ycombinator.com/item?id=36933452
[L8]: https://www.skyvern.com/blog/we-open-sourced-and-ended-up-1-on-hackernews/
[L9]: https://gitstars.substack.com/p/gittrends-september-1-2024
[L10]: https://news.ycombinator.com/showhn.html
[L1–L9]: #10-sources
[R1–R3]: #10-sources
