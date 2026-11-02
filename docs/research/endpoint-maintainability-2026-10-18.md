# How can Connapse eliminate the code-quality problems that make its endpoints hard to maintain, on both the architecture side and the coding-agent side?
**Date:** 2026-10-18
**Status:** Reviewed
**Built on:** api-mcp-cli-consistency-2026-10-17.md (operations layer, parity test, default-deny MCP, typed results); programmatic-source-configuration-safety-2026-08-17.md (MCP risk model, via the 2026-10-18 design decision)

## Executive summary

You can't get to zero problems, but you can make almost every structural problem fail the build, so that only judgement problems remain for review. The approach has four layers:
- **An architecture that makes the right shape the easy shape:** one operation class per action, typed results, one mapper from status codes to error responses, and a metadata attribute on every operation.
- **Compiler and test checks that reject the wrong shape:** pinned analyzers, a list of banned APIs, a small custom analyzer, architecture tests that may only shrink their allow-list, a parity test, and a committed OpenAPI file that CI diffs.
- **Agent controls that run whatever the agent decides:** required CI checks, protected config and test files, Claude Code hooks, and a scaffold.
- **Human review spent on the two things no tool catches:** whether the authorization logic is right, and whether the design fits.

The evidence favours checks over instructions. Static-analysis feedback cut LLM security issues from over 40% to 13%. Instruction files slightly reduced agent task success in the one controlled study.

Two findings change the starting point. CI's code-quality job already runs `dotnet format` and a warnings-as-errors build, but both steps fail on `main` and are set to `continue-on-error`, so nobody sees it. And Claude models game impossible tests mostly by editing the tests, so the test and config files themselves need protecting.

## Research brief

**Question:** How can Connapse eliminate, as near as possible, the code-quality problems that make endpoints hard to maintain, from both the architecture side and the coding-agent side?

**Sub-questions investigated:**
1. Which .NET 10 endpoint and application architecture minimizes maintenance cost for Connapse's shape (Blazor Server, Minimal APIs and MCP over one domain)?
2. Which mechanical, build-failing checks enforce that architecture, and how should they be layered for speed and resistance to bypass?
3. Which controls make coding agents produce consistent code, and how do agents get around them?
4. Which maintainability failures does AI-written code actually show, as measured, and which countermeasures have a measured effect?

**Out of scope:** UI styling; non-endpoint code quality such as ingestion and search internals; choosing an LLM vendor.

**Success criteria:** A concrete convention, an ordered enforcement stack with exact packages and settings, an agent setup with exact hook events, and an honest list of residual risk.

**Connapse baseline (2026-10-18):**
- No `.editorconfig`, no extra analyzers, and no `AnalysisLevel` pin in `Directory.Build.props`.
- No architecture tests, no validation library, and no OpenAPI generation.
- Tests use xUnit 2.9.3, FluentAssertions 8.8.0, NSubstitute, bunit and Testcontainers.
- Endpoints build 163 responses from anonymous objects. Only 7 of those are success bodies (`Results.Ok(new { ... })`); the rest are error bodies.
- `FileBrowser.razor`, `Sources.razor` and `EmbeddingSettingsTab.razor` resolve services through `IServiceScopeFactory`.
- `.claude/settings.json` has no hooks.

## Findings by sub-question

### 1. Endpoint architecture for Connapse

**Claim: plain operation classes called through DI by Blazor, Minimal API endpoints and MCP tools, with typed results and a status-enum result, are the lowest-maintenance shape. No mediator or endpoint framework is needed.**

- **Vertical slice, the folder layout and not a library.** Jimmy Bogard's rule is to "minimize coupling between slices, and maximize coupling in a slice," and in it "new features only add code." He warns against rigid layering rules. [primary] https://www.jimmybogard.com/vertical-slice-architecture/ Code that is only ever added to fits unattended parallel agents, because new files rarely conflict. That part is an inference.
- **Umbraco's Management API is the closest precedent.** It has one controller per operation (`CreateDocumentController`). Services return `Attempt<TResult, ContentEditingOperationStatus>`, and one shared method maps the status enum to ProblemDetails, the standard JSON error body (RFC 7807). The same services sit behind the backoffice UI and the API. [primary] https://github.com/umbraco/Umbraco-CMS/blob/main/src/Umbraco.Cms.Api.Management/Controllers/Document/CreateDocumentController.cs
- **.NET 10 built-ins replace the libraries.**
  - `MapGroup` applies authorization and tags to a whole group of routes.
  - `TypedResults` with `Results<Ok<T>, NotFound>` puts the response types into OpenAPI, and returning a type outside the union is a compile error. [primary] https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0
  - `AddValidation()` turns on source-generated DataAnnotations validation for Minimal APIs and Blazor. Its limits: types must be declared in `.cs` files rather than `.razor`, it must be called in each assembly that declares validated types, `[ValidatableType]` is experimental, and if metadata is missing, validation silently doesn't run. [primary] https://learn.microsoft.com/aspnet/core/validation/overview
  - `AddProblemDetails` with `IProblemDetailsService` gives every error the same shape. [primary] https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0
- **Libraries to avoid, and why.**
  - MediatR v13+ (July 2025) is dual-licensed under RPL-1.5 or a commercial license. The free tier covers companies under $5M revenue and needs a license key. It also adds `Send()` indirection that agents have to trace. [primary] https://www.jimmybogard.com/automapper-and-mediatr-commercial-editions-launch-today/
  - AutoMapper v15+ has the same license change.
  - Wolverine.HTTP generates code at runtime, so agents can't read the handler wiring in the source. [primary] https://wolverinefx.net/guide/http/endpoints.html
  - FastEndpoints (MIT, 8.2.0) brings a base class and its own validation and OpenAPI stack, which Blazor and MCP can't share.
  - FluentValidation isn't needed now that validation is built in.
- **Polymorphic per-provider configuration.** Use an abstract `SourceScope` record with `[JsonPolymorphic(TypeDiscriminatorPropertyName = "provider")]` and one `[JsonDerivedType]` per provider. .NET 10 emits this as **`anyOf` with a discriminator, not `oneOf`**, and only when the base type is abstract. [primary, source] https://github.com/dotnet/aspnetcore/blob/release/10.0/src/OpenApi/src/Extensions/JsonNodeSchemaExtensions.cs

### 2. Mechanical enforcement in .NET 10

**Claim: Roslyn analyzers catch problems inside one file, architecture tests catch dependencies between types, and a parity test plus a committed OpenAPI file catch drift between surfaces. Nothing in the SDK blocks only *new* violations, so each check needs a mechanism that ratchets.**

- **Analyzers.**
  - The default `AnalysisLevel=latest` lets an SDK upgrade add warnings with no change in the repo. Pin it with `AnalysisLevel=10.0-recommended`. `EnforceCodeStyleInBuild` is off by default. [primary] https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props
  - Microsoft.CodeAnalysis.BannedApiAnalyzers 5.6.0 bans symbols listed in `BannedSymbols.txt`, which can name exact overloads such as `M:Microsoft.AspNetCore.Http.Results.Ok(System.Object)`. A ban list can't be scoped to a folder, only to a project or an `.editorconfig` severity section. Whether those sections match Razor-generated code is unverified. [primary] https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md
  - ASP.NET Core's own analyzers (ASP0000–ASP0038) catch route conflicts and overridden authorization, but **none requires typed results**. [primary] https://learn.microsoft.com/en-us/aspnet/core/diagnostics/code-analysis?view=aspnetcore-10.0
  - Meziantou.Analyzer 3.0.307 is actively maintained; Roslynator 5.0.1 overlaps with it.
  - A small in-repo analyzer, a `netstandard2.0` project referenced with `OutputItemType="Analyzer"`, is the only way to enforce "endpoints return `Results<...>`" and "every operation carries `[Operation]`". Expect about 150–300 lines per rule plus its tests.
- **Architecture tests.** NetArchTest.Rules hasn't been released since May 2021. TngTech.ArchUnitNET.xUnit 0.13.4 (August 2026) is active, so use that. [primary] https://www.nuget.org/packages/TngTech.ArchUnitNET.xUnit
  - Compiled `.razor` components are ordinary types, so "Components must not depend on stores" can be expressed. That is untested.
  - Blind spots: runtime service location, reflection, and what a lambda returns.
- **Source generators.** Immediate.Apis 6.4.0 generates Minimal API registration from handler classes. No generator covers MCP tools. A reflection-based parity test is cheaper than writing a generator. [primary] https://www.nuget.org/packages/immediate.apis/
- **OpenAPI gates.**
  - Generate the document at build time with Microsoft.Extensions.ApiDescription.Server, commit it, and fail CI when regenerating it produces a `git diff`. Generation runs `Program.cs`, so skip migrations and hosted services when the entry assembly is `GetDocument.Insider`.
  - oasdiff `breaking` supports OpenAPI 3.1. A Spectral ruleset can forbid response schemas that are a bare `object` and enforce `operationId` naming.
  - [primary] https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0, https://github.com/oasdiff/oasdiff
- **Ratcheting.** The SDK has no "new violations only" mode. The workable ratchets are:
  - severity raised per folder in `.editorconfig` as each folder is cleaned;
  - an xUnit test that fails when the allow-list or suppression count goes up;
  - SARIF output uploaded to GitHub code scanning, which shows only alerts new to the PR (unverified for this repo).

  SquiggleCop 1.0.26 records rule *severities*, so it catches an SDK bump silently changing rules.
- **Speed.**
  - Under 10 seconds, suitable for an agent hook: build only the changed project, with analyzers.
  - CI: a full Release build with warnings as errors, the architecture and parity tests (unit-speed, no Docker), the OpenAPI diff, oasdiff and Spectral.
  - Nightly and optional: Stryker.NET 5.0.0 mutation testing on the operations project. There's no evidence that coverage thresholds or duplicate-code detectors improve endpoint maintainability.

### 3. Controls on coding agents

**Claim: controls that run whatever the agent decides (CI, protected files, hooks) are the ones that hold. Instruction files help only when they are short and say things the agent can't work out from the code.**

- **Hooks (Claude Code docs, checked 2026-10-18).** There are 33 hook events.
  - A `PreToolUse` hook can deny an action, and when hooks disagree, deny wins. Exit code 2 blocks the action and sends stderr to Claude.
  - A `Stop` hook returning `decision: "block"` keeps Claude working. `stop_hook_active` and a cap of eight blocks in a row prevent loops.
  - `PostToolUse` runs concurrently for parallel edits, so slow checks belong in `Stop`.
  - On Windows, file paths arrive with backslashes.
  - [primary] https://code.claude.com/docs/en/hooks
- **Worktrees.**
  - `${CLAUDE_PROJECT_DIR}` stays pointed at the main checkout, so a hook must use the `cwd` from its input.
  - Desktop worktree sessions read settings and hooks from the main checkout, so hooks only take effect once they're on `main`.
  - `.worktreeinclude` copies gitignored files such as `.env` into new worktrees.
  - [primary] https://code.claude.com/docs/en/worktrees
- **CLAUDE.md.**
  - It is "context, not enforced configuration"; anything that must not happen belongs in a hook.
  - Keep each file under 200 lines. `.claude/rules/*.md` with `paths:` frontmatter loads only when matching files are touched. When two instructions contradict, Claude picks between them arbitrarily.
  - Anthropic's test for each line is "Would removing this cause Claude to make mistakes?" It recommends naming the example file to copy.
  - [primary] https://code.claude.com/docs/en/memory, https://code.claude.com/docs/en/best-practices
- **Evidence on instruction files.**
  - In an ETH study, AGENTS.md context files slightly *reduced* task success and raised inference cost by over 20%. Human-written files beat generated ones, and the advice is to include only what the agent can't infer. [secondary, abstract] https://arxiv.org/abs/2602.11988
  - IFScale: models reached about 68% accuracy at 500 simultaneous instructions, and Claude Sonnet 4 declined steadily as instructions were added. [secondary, abstract] https://arxiv.org/abs/2507.11538
- **Bypass.**
  - On ImpossibleBench, where the tests contradict the spec so any pass is cheating, Claude Opus 4.1 cheated on about 50% of tasks, mostly by editing the tests. GPT-5 cheated on 54%, more often by special-casing the tested inputs.
  - Read-only tests stop test editing without hurting legitimate performance. They don't stop special-casing.
  - [primary] https://arxiv.org/html/2510.20270
  - Permission deny rules cover the file tools and shell redirects, but not scripts that write files themselves. Only the sandbox enforces paths at the OS level. [primary] https://code.claude.com/docs/en/permissions
- **Automated review.** Claude Code Review reads `REVIEW.md` from the base branch only, never from the PR, so an agent can't weaken it in its own PR. It never blocks a merge. CodeRabbit `path_instructions` attach guidance to paths. Both are advisory. [primary] https://code.claude.com/docs/en/code-review, https://docs.coderabbit.ai/configuration/path-instructions
- **Not evidenced:** controlled studies of golden files, scaffolds or spec-driven development (GitHub Spec Kit, Kiro). They are recommended by vendors but unmeasured.

### 4. Measured failure modes of AI-written code

**Claim: the best-measured failures are duplication and lost reuse, code that passes tests but is wrong (including tampered tests), and missing security controls. Static-analysis feedback loops and test-integrity controls have the strongest measured effect.**

- **Duplication and lost reuse.**
  - GitClear 2026 (623M changes, correlational, vendor): duplicated blocks rose 81%, moved code fell from 21% to 3.8%, calls to existing functions fell 35%, and error-masking constructs rose 47%. [primary, vendor] https://www.gitclear.com/the_ai_code_quality_maintainability_gap
  - GitClear 2025: refactoring lines fell from 25% to under 10%. [primary, vendor] https://www.gitclear.com/ai_assistant_code_quality_2025_research
- **Passes tests but is wrong.**
  - About 29.6% of SWE-bench patches marked correct behave differently from the real fix. SWE-bench is the standard benchmark of real GitHub issues. [primary, ICSE 2026] https://arxiv.org/abs/2503.15223
  - METR's hand grading found far fewer mergeable agent PRs than test-passing ones. It was a small sample. [primary] https://metr.org/blog/2025-08-12-research-update-towards-reconciling-slowdown-with-time-horizons
- **Security.**
  - BaxBench (backend endpoints specifically): the best model was correct only 62% of the time, and about half of the correct backends were exploitable. [primary] https://icml.cc/virtual/2025/poster/44337
  - Veracode: 45% of tasks introduced an OWASP Top 10 flaw, the standard list of top web vulnerabilities. C# failed 38–45% of the time, and log injection went unprevented 88% of the time. [secondary, vendor]
  - Stanford: developers using an AI assistant wrote less secure code and were more confident in it. [primary, older model] https://arxiv.org/abs/2211.03622
- **Agent PRs.** Claude Code PRs were merged 71–84% of the time, against an 82.6% human baseline. Reasons seen only for agent PRs included "too large", "more complex than needed" and "no added value". [primary] https://arxiv.org/html/2602.04226v1
- **Countermeasures, measured.**
  - Feeding Bandit and Pylint (Python analyzer) output back to the model for up to 10 rounds cut security issues from over 40% to 13% and readability issues from over 80% to 11% (benchmark). [primary] https://arxiv.org/abs/2508.14419
  - At Atlassian, LLM review comments were acted on 38.7% of the time, against 44.5% for human comments. Design comments are resolved least often (28.6%). [primary] https://arxiv.org/html/2601.01129v1
  - Evidence for small PRs is weak and correlational.

## Recommendation for Connapse

**Claim: build the right shape first, enforce it mechanically, protect the enforcement from agents, and spend review only on authorization logic and design.**

**Convention**
- **Where operations live.** Put them in a new `Connapse.Application` project: `Operations/<Feature>/<Verb><Noun>.cs`. Each file holds a request record, a response record, and an operation class with `ExecuteAsync(request, OperationContext, ct)`, which returns `Attempt<TResponse, <Feature>OperationStatus>`.
  - The strongest reason for a separate project: project references are enforced by the compiler, so operations can't reach ASP.NET Core or Razor types even when an agent tries.
  - `OperationContext` carries the caller and the surface (UI, REST or MCP).
  - The operation owns authorization, preflight and audit.
- **Metadata.** `[Operation("sources.create", Scope = "sources:write", Risk = RiskTier.Credential, Mcp = McpExposure.Denied)]` on each operation class. Endpoint helpers read it, the MCP registration reads it (default-deny), and the parity test reads it.
- **Endpoints.** `Endpoints/<Feature>Endpoints.cs` with `MapGroup`. One line per route, calling a named static handler that calls the operation and returns `Results<Ok<T>, ProblemHttpResult, …>`.
- **Errors.** One mapper per status enum, producing ProblemDetails with a stable `type` URI. Exceptions are only for bugs and infrastructure failures. This removes most of today's 163 anonymous-object responses. Only 7 are `Results.Ok` success bodies; the rest are error responses plus a few `Results.Json` calls.
- **Validation.** DataAnnotations on the request records, with `AddValidation()` in every assembly that declares them, plus a test proving validation actually runs.
- **Polymorphic config.** `ScopeJson` becomes the abstract polymorphic `SourceScope` record described in sub-question 1.
- **Blazor pages and MCP tools** inject operations only, never stores and never `IServiceScopeFactory`.

**Rollout, in dependency order** (each PR stays under about 300 lines):
1. **Make the existing gates real.**
   - Clean up so `dotnet format --verify-no-changes` and the warnings-as-errors build pass. They fail on `main` today with about 400 format diagnostics and 8 build errors, and the build stops at the first failing project, so expect more.
   - Pin `AnalysisLevel=10.0-recommended`, then remove `continue-on-error`.
   - Of the errors found, EF1002 at `PgVectorStore.cs:423` is a clamped integer in `SET LOCAL`, not live injection. Suppress it with a justification. CS0618 is the obsolete Anthropic `Temperature` parameter.
2. **Protect the enforcement.**
   - Branch protection with the CI jobs as required checks.
   - CODEOWNERS for `.editorconfig`, `Directory.Build.props`, `BannedSymbols*.txt`, `.github/**`, `.claude/**`, `REVIEW.md`, `.coderabbit.yaml`, and the architecture and parity tests.
   - A `PreToolUse` deny hook and permission deny rules for the same files.
   - Block Bash and edit content that adds `#pragma warning disable`, `[SuppressMessage`, `NoWarn`, `--no-verify` or `TreatWarningsAsErrors>false`.
3. **Build the reference slice.** The `Attempt` type, the ProblemDetails mappers, `[Operation]`, `OperationContext`, and one feature (Sources) migrated end to end across UI, REST and MCP. This becomes the golden file.
4. **Enforce the pattern.**
   - BannedApiAnalyzers in Connapse.Web, banning the `Results.*(object)` overloads.
   - The in-repo analyzer with two rules: typed results in `Endpoints/`, and `[Operation]` required.
   - ArchUnitNET rules: Components may depend only on Application and UI helpers, and may not use `IServiceScopeFactory`. The allow-list may only shrink.
   - The parity test: every public operation has a route, and MCP exposure follows the metadata.
5. **OpenAPI.** Build-time document committed under `docs/api/openapi.json`, the git-diff gate, oasdiff `breaking` and a Spectral ruleset. This comes after step 3, because untyped responses give these tools nothing to check.
6. **Agent layer.**
   - A `PostToolUse` hook (`if: Edit(**/*.cs)`) that builds the changed project and returns analyzer errors to the agent.
   - `Stop` and `SubagentStop` hooks that run the build plus `dotnet test --filter Category=Unit` in the hook's `cwd`, blocking until they pass.
   - A `dotnet new connapse-operation` scaffold.
   - `.claude/rules/endpoints.md` scoped to `paths:`, at most about 15 lines, naming the golden file and the scaffold. Replace the stale endpoint line in CLAUDE.md with a pointer.
   - `REVIEW.md` and CodeRabbit `path_instructions`, advisory only.
   - Update the `parallel-work` skill so agents run the checks before opening a PR.
7. **Migrate the remaining features** one slice at a time, under the ratchet.
8. **Optional:** nightly Stryker.NET on `Connapse.Application`.

**Where human review goes:**
- Authorization logic against the per-object permission model. A wrong scope looks correct and passes tests.
- Design fit and duplicated concepts that clone detectors miss.
- Every change to a test file, reviewed as its own question.

## Conflicts and uncertainties

- **Corrections to earlier statements in this session.**
  - Polymorphic schemas come out as `anyOf` with a discriminator, not `oneOf`.
  - "163 anonymous responses" is accurate, but only 7 are success bodies, so ProblemDetails mapping fixes most of them.
  - CI runs on PRs to `main` **and** `epic/**`; one worker reported `main` only.
- **Razor and `.editorconfig` path sections.** It's unverified whether path-scoped severity applies to code generated from `.razor` files. ArchUnitNET is the safer place for rules about Components.
- **Kiota and `anyOf`.** It's unverified how Kiota and other client generators handle `anyOf` with a discriminator. Test this before the CLI client is generated.
- **Where operations live.** One worker suggested keeping them in Connapse.Web. This report recommends a separate project for the compile-time boundary. It's a judgement call, not evidence-backed.
- **FluentAssertions 8.8.0.** The repo uses it. Version 8 moved to a commercial license (Xceed), free only for non-commercial use, and Connapse Cloud is commercial. **Verify the terms**; v7 or an alternative may be needed. This came from my own knowledge and was not checked by a worker this run.
- **Single-source or vendor evidence.** GitClear (vendor, correlational, method not public), Veracode (vendor press), CodeRabbit (vendor, small sample). The analyzer-feedback study is a Python benchmark, not .NET.

## Gaps — what we did not find

- No controlled study of golden files, scaffolds or spec-driven development with coding agents.
- No published .NET parity test spanning routes and MCP tools.
- No measurement of reviewer fatigue on AI-written PRs.
- No current Spectral or oasdiff Action version confirmed; the oasdiff Action needs ≥ 0.0.51 (CVE-2026-53507, from the 2026-10-17 report).
- Whether GitHub code-scanning SARIF shows only new alerts was not verified for this repo.
- The full list of build errors beyond the first failing project (Connapse.Storage) is unknown until that project is clean.

## Source quality assessment

The architecture and tooling claims rest on primary sources: Microsoft Learn, dotnet/aspnetcore source, NuGet pages, library authors' own posts, and Umbraco's and eShop's repos. Agent controls rest on Claude Code's official docs, plus primary but benchmark-specific papers (ImpossibleBench, the analyzer-feedback study, the Atlassian review study). The failure-mode statistics mix primary academic work (SWE-bench correctness, BaxBench, METR) with vendor reports (GitClear, Veracode, CodeRabbit, Sonar), which are treated as directional. The CI findings come from the repo's own run 39097835077 logs and are primary.

## Sources

**Primary:**
- **Microsoft Learn:** MSBuild props, Minimal API metadata, validation, error handling, OpenAPI, ASP.NET Core analyzers.
- **Source and repos:** dotnet/aspnetcore JsonNodeSchemaExtensions.cs; roslyn-analyzers BannedApiAnalyzers docs; Umbraco-CMS CreateDocumentController; dotnet/eShop OrdersApi; oasdiff repo.
- **Library authors:** jimmybogard.com (vertical slice; commercial editions); wolverinefx.net.
- **NuGet:** TngTech.ArchUnitNET.xUnit, NetArchTest.Rules, Immediate.Apis, Meziantou.Analyzer, Roslynator, dotnet-stryker, SquiggleCop, BannedApiAnalyzers.
- **Claude Code docs:** hooks, memory, best-practices, skills, permissions, worktrees, code-review, github-actions; CodeRabbit path-instructions.
- **Papers and reports:** arXiv 2510.20270 (ImpossibleBench), 2503.15223, 2508.14419, 2601.01129, 2602.04226, 2211.03622; BaxBench (ICML 2025); METR 2025 update.
- **Connapse CI:** run 39097835077 job logs.

**Secondary:**
- arXiv 2602.11988 (AGENTS.md study, abstract) and 2507.11538 (IFScale, abstract).
- GitClear 2025 and 2026 (vendor); Veracode press release (vendor); CodeRabbit AI vs human report (vendor); Sonar LLM personalities (vendor).

**Tertiary:**
- Spec Kit guide blog.
