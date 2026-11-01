# Which tests prove that each endpoint-consistency problem in Connapse is actually solved?
**Date:** 2026-10-18
**Status:** Reviewed
**Built on:** endpoint-maintainability-2026-10-18.md, api-mcp-cli-consistency-2026-10-17.md, and the draft epic (artifact https://claude.ai/artifact/WbqaCPaZ53skPCrtjn4svz)

## Executive summary

Every problem in the endpoint epic except one can be given a product test that fails today and passes only when the problem is gone. The exception is agent behaviour itself.

The strongest proofs:
- **A committed capability manifest** asserted against the real routes and MCP tools (UI-only capabilities).
- **A same-action-every-surface theory** comparing outcomes and side effects across REST, MCP and the page (behaviour drift).
- **Exact-match allow-list architecture tests** (pages using stores, service location, untyped responses).
- **A reflection-driven authorization matrix** over every route and MCP tool (missing authorization).
- **A response-schema check** against the committed OpenAPI document (shape consistency).
- **Version-skew CLI tests** against the current and previous server images.
- **Suite-speed tests:** every test has exactly one size trait, the fast tier stays within a time budget, and no test uses fixed delays.

Applying the maintainer's rule (only product tests, or tests of suite speed) reshapes the epic:
- The agent-layer tickets (hooks, scaffold, rules files) and the custom analyzers can't be proven by a product test, so they are deferred until product tests show they're needed.
- The parity and architecture tests move to the front, as the failing tests the rest of the epic must turn green.

## Research brief

**Question:** Which tests or evals best prove that each problem the endpoint epic targets is solved, so that tickets whose tests don't go green are scrapped and redesigned?

**Problems:**
- **P1** UI-only capabilities
- **P2** the same action behaving differently across surfaces
- **P3** inconsistent response and error shapes
- **P4** architecture violations
- **P5** breaking API changes nobody notices
- **P6** MCP exposure and authorization
- **P7** the CLI lagging, and version skew
- **P8** regressions during the migration
- **P9** agent-written code (missing authorization, weak tests, special-casing, duplication)
- **P10** test-suite speed

**Constraint:** tests may only concern the product (Connapse code and behaviour) or the speed of running the tests. No tests of agents, hooks, docs or process.

**Success criteria:** For each problem, the first tests to write, why they fail today, what makes them pass, and their cost. Plus a rule for scrapping tickets.

## Findings by problem

### P1 UI-only capabilities: an executable capability manifest

**Claim: a committed manifest of capabilities, asserted against the routes and tools, is the only test that fails today for every UI-only capability.**

- **Test:** `CapabilityManifest_EveryCapability_HasOperationRouteAndTool`.
  - `capabilities.json`, built from the surface map, has one row per capability: its operation id, its expected REST route, and its MCP exposure. A capability that should stay off MCP gives a reason.
  - The test resolves `EndpointDataSource` from the test host, and lists tools through the MCP SDK's `HttpClientTransport` with the factory's `HttpClient`, so it runs in-process.
  - [primary] https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/src/ModelContextProtocol.Core/Client/HttpClientTransport.cs
- **Fails today:** connections, provider setup, invites, summary settings, source search over REST and the other UI-only rows have no route or tool.
- **Cost:** about 1 second in the shared fixture.
- **Where the proof leaks.** "Pages only call operations" plus "every operation has a route" leaves four ways for a capability to stay UI-only:
  - a page handler that chains several operations with logic in between;
  - validation or preflight that runs only in the component;
  - sorting or filtering computed in the page;
  - a route that exists but binds different inputs or defaults.
  P2's comparison test covers the meaning of an action. A per-component logic budget (P4) limits page-side composition.

### P2 and P8 behaviour drift and migration regressions: the same action through every surface

**Claim: a differential test is the failing-first proof for drift. It runs the same case through REST, MCP and the page, and compares a normalized outcome including side effects. Characterization snapshots guard migrations but can't be failing-first, because they pass by definition.**

- **Test:** `Operation_SameCase_SameOutcomeOnEverySurface`.
  - An xUnit `[Theory]` over (operation, case, surface), with valid, invalid, under-scoped-token and preflight-failure cases.
  - One adapter per surface returns an `Outcome` record: success or error code, ProblemDetails `type`, a database projection filtered to the test's own container, audit rows (action, actor, resource), and enqueued Hangfire jobs.
  - The surfaces are driven like this:
    - **UI:** bUnit, with `Services.AddFallbackServiceProvider` set to the host's scoped provider, so the page runs on real DI. Whether scoped services need `CreateScope()` is uncertain. [primary] https://bunit.dev/docs/providing-input/inject-services-into-components.html
    - **REST:** the factory's HttpClient.
    - **MCP:** an `McpClient` over that same client.
  - Today's bUnit page tests substitute the stores and `IAuditLogger`, so they prove nothing about side effects.
- **Fails today:** wherever the surfaces really diverge, and wherever a surface is missing. Rows that pass on first run aren't evidence for P2; keep them only as regression guards.
- **Migration guard:** `{Operation}_{Case}_MatchesCharacterizationSnapshot`.
  - Verify.Xunit (Connapse uses xUnit v2) snapshots the *semantic* `Outcome`, recorded before each migration ticket starts. It replaces GUIDs with stable placeholders.
  - Keep wire-shape snapshots separate, because P3 changes error shapes on purpose.
  - [primary] https://github.com/VerifyTests/Verify/blob/main/docs/guids.md, https://github.com/VerifyTests/Verify.Http
- **Cost:** medium. Roughly 30 operations × 4 cases × 3 surfaces, at 50–200 ms each (unmeasured). The bUnit path costs the most.
- **Shared-host safety:** filter by the test's own ids, never global counts, because only one in-process host per test process works with Hangfire's static storage.

### P3 and P5 response shapes and breaking changes: contract tests against the committed spec

**Claim: validating every integration-test response against the committed OpenAPI document proves shape consistency. A snapshot of the generated spec plus oasdiff proves breaking changes get noticed. Both become meaningful once the spec exists (ticket 2.5); before that they fail for the trivial reason that there is no spec.**

- `IntegrationSuite_EveryResponse_ConformsToCommittedSpec`: a `DelegatingHandler` on the test HttpClient validates each body against its operation's schema. It uses Microsoft.OpenApi and JsonSchema.Net 7.x or newer (which supports draft 2020-12, as OpenAPI 3.1 requires), collects failures and asserts them at the end. The overhead is milliseconds per call. [primary] https://www.nuget.org/packages/JsonSchema.Net/7.2.1
- `Every4xx5xx_IsProblemJson`: the same handler checks for `application/problem+json`.
- `Endpoints_NoAnonymousResponses_ExceptAllowList`: an in-test Roslyn walk with an exact-match list. This is the failing-first form of P3, meaningful today.
- `CommittedSpec_MatchesGenerated` catches the spec silently drifting from the code. oasdiff `breaking` (Action 0.0.51 or later) decides whether a change breaks clients. Use both. [primary] https://github.com/oasdiff/oasdiff
- **Schemathesis 4.32.0** (released 2026-10-18) is black-box OpenAPI fuzzing that needs no .NET integration. Run it nightly against the composed app: no 5xx, no undocumented status codes, bodies match the schema. [primary] https://pypi.org/project/schemathesis/
  - In Kim et al. (ISSTA 2022), white-box EvoMaster found the most faults. EvoMaster's .NET support is research-only, so Schemathesis is the practical choice. [primary] https://arxiv.org/abs/2204.08348

### P6 and P9-security authorization: matrices generated from metadata

**Claim: authorization tests generated from endpoint and tool metadata make "a new route or tool without an authorization expectation" a failing test. Several fail today.**

- **What exists today:** Connapse calls the MCP SDK's `AddAuthorizationFilters()` (`Program.cs:124`), which hides unauthorized tools from `tools/list` and refuses calls to them. But none of the 12 tools carries `[Authorize]`, so nothing is filtered. The `Scope:*` policies are defined but unused. [primary] https://csharp.sdk.modelcontextprotocol.io/v1/concepts/identity/identity.html
  - This makes most of #782's scope filtering a metadata change rather than new machinery.
- **MCP tests:**
  - `ToolsList_ReadOnlyScopeToken_ExcludesWriteAndDestructiveTools` (fails today: all 12 are listed)
  - `ToolsCall_HiddenToolByName_ReturnsForbidden` (fails today: `container_delete` runs)
  - `ToolsCall_DestructiveTool_ReturnsPendingApproval_AndNothingChanged`
  - `EveryMcpTool_HasScopeAndRiskMetadata`
- **REST tests:**
  - `EveryEndpoint_HasExplicitAuthorizationExpectation`: enumerate `EndpointDataSource`, read `IAuthorizeData` and `IAllowAnonymous`, and compare against an expectations table.
  - `AuthzMatrix_RouteByRole_MatchesExpectation`: route × {anonymous, viewer, editor, admin, agent with each scope}, using a test authentication handler.
  - `CrossContainer_ObjectAccess_Returns404`: substitute another container's ids into every route that has `{containerId}` or `{fileId}`.
  - This follows OWASP API Security 2023, items API1 (object-level authorization) and API5 (function-level authorization). [primary] https://api-security.owasp.org/editions/2023/en/0xa5-broken-function-level-authorization
- **Protocol check:** the official MCP conformance suite has a server mode and a GitHub Action, with an expected-failures file. It checks the protocol, not per-scope authorization, and the 2026-07-28 scenarios are still alpha. [primary] https://github.com/modelcontextprotocol/conformance
- **Why this matters for agent-written code:** in BaxBench, about half of the backends the best model got correct were still exploitable. These matrices are the product-side defence. [primary] https://mlanthology.org/icml/2025/vero2025icml-baxbench

### P4 and P9-quality architecture and test strength: exact-match allow-lists, canaries, mutation, properties

**Claim: exact-match allow-lists are the right failing-first pattern. Each ticket's failing test is its entry deleted from the list. Architecture tests also need a canary, because ArchUnitNET currently misses some dependencies.**

- **The pattern.** The set of violators must equal the committed list exactly. A new violation fails, and so does a fixed one still on the list.
  - Don't use `Skip` (invisible), a `≤ N` count (violations can be swapped), or ArchUnitNET's freeze feature. Freeze records violations silently when a rule has no entry, keys entries by the rule's description text, and rewrites the store file on every run. [primary, source] https://github.com/TNG/ArchUnitNET/tree/main/ArchUnitNET/Fluent/Freeze
- **ArchUnitNET blind spots.** In Release builds it dropped dependencies inside `async` methods (#498) and didn't analyze async lambdas (#230). Both are fixed on main (2026-10-09) but in no release yet. Run the architecture tests on Debug and add `ArchRule_DetectsPlantedViolation`. Pin `TngTech.ArchUnitNET.xUnit` 0.13.4; the package named just `ArchUnitNET` is a different project. [primary] https://github.com/TNG/ArchUnitNET/issues/498, https://github.com/TNG/ArchUnitNET/issues/230
- **Tests and today's state:**
  - `Components_DoNotDependOnStores_ExceptAllowList`: 12 of 60 `.razor` files inject `I*Store`.
  - `NoType_DependsOnIServiceScopeFactory_ExceptAllowList`: FileBrowser, Sources, EmbeddingSettingsTab and SettingsEndpoints.
  - `EndpointHandlers_DependOnlyOnApplication`.
  - `ComponentLogicBudget_DoesNotExceedRatchet`: a Roslyn parse of each component's `@code` and code-behind, with a per-file budget for lines, `@inject` count and cyclomatic complexity. Today Providers.razor has 2,228 `@code` lines and 23 injections, Connections.razor 1,566 and 20, Sources.razor 1,144 and 17.
- **Test strength.**
  - Detecting seeded bugs ("mutants") correlates with detecting real faults, independently of coverage. [primary] https://homes.cs.washington.edu/~mernst/pubs/mutation-effectiveness-fse2014-abstract.html
  - Google mutates only changed code and shows the surviving mutants in review (82% rated useful) rather than gating on a score. [primary] https://ar5iv.labs.arxiv.org/html/2102.11378
  - Plan: Stryker.NET 5.0.0 `--since:main` on Connapse.Application with `thresholds.break` starting at 60 (no evidence supports a specific number), plus a full nightly run.
  - CsCheck property tests on each operation's invariants (validation rejects every malformed input; results match a reference model) defeat special-casing of example inputs. In a Python study, each property test killed about 50x more mutants than an average unit test; real-bug evidence is mixed. [primary abstract] https://2025.splashcon.org/details/OOPSLA/102
- **Duplication.** PMD CPD's options for ignoring renamed identifiers and literals don't apply to C#, so it misses renamed near-duplicates. An in-test hash over normalized statement windows is possible, but has no published false-positive rate. Treat it as experimental, behind a high threshold. [primary] https://pmd.github.io/pmd/pmd_userdocs_cpd.html

### P7 CLI compatibility: generated client and a version-skew matrix

**Claim: a compile check of the generated client plus smoke tests against the current and previous server images prove the CLI keeps up. Pact is too heavy for one team owning both sides.**

- `GeneratedClient_CompilesAgainstCommittedSpec`, in the CLI repo.
- `Cli_SmokeCommands_AgainstServerImages_N_and_NMinus1`: Testcontainers runs both published images, and the CLI runs list, upload, search and delete. Expect 30–60 s per image (unmeasured).
- `Cli_NewerThanServer_ReportsUnsupportedFeatureClearly`: fails today, because the CLI has no version negotiation.
- PactNet 5.0.1 (March 2025) brings a broker and provider-state setup, which isn't worth it here. [primary] https://www.nuget.org/packages/PactNet

### P10 suite speed: measured state, tiers and budget tests

**Claim: the current "Unit" tier is too slow to gate on (88 s) because slow PDF, OCR and inference tests carry the Unit tag. Most CI time goes to one integration assembly that runs serially.**

- **Measured on main run 39097835077:**
  - build-and-test 9m48s, of which build 82 s, Unit 88 s wall, Integration 6m02s wall; code-quality 2m40s.
  - Integration.Tests: 768 tests in 5.76 min. Their summed per-test times are only about 82 s, so the rest is probably fixture setup and serialization (unverified).
  - 91 classes share one `[Collection("Integration Tests")]`, and test code has 19 `Task.Delay` calls.
  - The slowest Unit-tagged tests take 42 s, 39 s and 31 s, and 175 tests take 1 s or more.
  - [primary: job logs]
- **Tiers:**
  - **Small** (Stop-hook and local gate): Unit + Architecture + snapshot tests, no Docker or models, under 30 s.
  - **Medium** (PR CI): one host with containers, the authorization matrix and schema validation. Whole PR under 6 min wall.
  - **Large** (nightly): Ollama ingestion, Eval systems tests, PDF models, Stryker full runs, Schemathesis deep runs.
  - Bazel's size classes come with enforced timeouts, which shows budgets like these are maintained in practice. [primary] https://bazel.build/reference/test-encyclopedia
- **Speed tests to write first:**
  - `EveryTest_HasExactlyOneSizeTrait`: fails today, since only Unit and Integration exist.
  - `SmallTier_TestsUnder200ms_MediumUnder5s` from TRX durations: fails today.
  - `SmallTier_WallTimeUnder30s`: fails today at 88 s.
  - `TestCode_HasNoFixedDelays`: fails today, with 19.
- **Speedups ranked by time saved per effort:**
  1. Retag slow tests as Large: the local tier drops to an estimated 25 s.
  2. Split CI into parallel jobs: saves about 1.5–2 min.
  3. Move Ollama and Eval tests to nightly: saves about 2–3 min.
  4. Replace fixed delays with polling.
  5. Move to xUnit v3. Version 4.0 (2026-08-14) has full parallelization within a shared collection, which would let one host serve parallel tests. That's high effort, and only works if each test's data is isolated; it could take Integration from 5.8 min to 1–2 min (unverified). [primary] https://xunit.net/releases/v3/4.0.0
  6. Testcontainers `WithReuse`, locally only. It's experimental and disables the resource reaper. [primary] https://dotnet.testcontainers.org/api/resource_reuse/

## The scrap rule, and what it does to the epic

**Claim: every ticket must name proof tests that are red before it starts and green when it's done, with no other proof test turning red. A ticket with no product proof is either enabling (a proof test can't gate without it) or deferred.**

1. **Proof tests come first, in their own PRs.** They must be seen red in CI, or ratcheted with an exact-match allow-list.
2. **Done means red to green, with no other proof test turning red.** Characterization snapshots are guards, not proof.
3. **If the planned PRs don't turn the proof green, stop.** Record why in the ticket and open a redesign ticket. Never loosen the test to get to green; changing a proof test takes its own PR, reviewed by the maintainer.
4. **Applied to the draft epic:**
   - **Enabling, kept:** 0.1–0.4. A gate can't block while the build is red, or with nothing required.
   - **Folded in:** 1.1 (conventions) into the authorization expectations and Spectral data.
   - **Became proof tests and moved to the front:** 2.3 (architecture) and 2.4 (parity).
   - **Cut, because the tests prove the same thing:** 2.1 and 2.2 (banned APIs, custom analyzers), as exact-match tests.
   - **Deferred, with no product proof:** 0.5 and 3.1–3.5 (hooks, scaffold, rules, parallel-work). Revisit when the Small tier is under its budget and CI history shows agent PRs repeatedly failing proof tests. At that point the hooks' value is running the same tests sooner.
   - **Folded:** 6.4 into the CLI skew tests. 7.1 into the test-strength ticket.

## Conflicts and uncertainties

- **MCP tool count.** I count 12 `[McpServerTool]` attributes; one worker counted 13 tool methods. It doesn't change any test.
- **Fixture time.** The integration fixture probably accounts for most of the 5.76 min. That is inferred from summed per-test durations, not profiled.
- **Data isolation.** Whether integration tests already isolate their data well enough for xUnit v3 parallelization is unverified, and it decides whether the v3 migration pays off.
- **bUnit and scoped services.** bUnit with the host's provider may need explicit scope creation. Untested.
- **Verify on CI.** Whether Verify always fails on mismatch on CI servers, rather than auto-accepting, is unconfirmed. Configure it explicitly.
- **Snapshots as a P9 hazard.** Accepting a changed `.verified` file can "fix" a test. Changed snapshot files must appear in the PR diff and are reviewed like test changes.
- **Mutation threshold.** The starting break value of 60 is a guess.

## Gaps — what we did not find

- No published false-positive rate for clone detection on C# endpoint handlers.
- No head-to-head benchmark of Postgres template databases, Respawn and TRUNCATE for this workload.
- No confirmation that EvoMaster's .NET support ships in current releases.
- No evidence-based mutation-score threshold.
- No measured runtime for the cross-surface theory or the authorization matrix at Connapse's scale.

## Source quality assessment

The tooling and API claims are primary: SDK source and docs, NuGet, project repos, release notes, and the Connapse CI job logs measured this run. Effectiveness claims rest on primary academic work (Kim et al. ISSTA 2022, Just et al. FSE 2014, Google's mutation papers, ImpossibleBench, BaxBench), with the property-testing result an abstract-level read. The runtime estimates for new tests are the workers' inferences and are marked as such.

## Sources

**Primary:**
- **SDKs and repos:** MCP C# SDK HttpClientTransport source and identity docs; MCP conformance repo; bUnit DI docs; Verify and Verify.Http; ArchUnitNET Freeze source and issues #498 and #230.
- **NuGet:** JsonSchema.Net, PactNet, TngTech.ArchUnitNET.xUnit.
- **Tools and docs:** Schemathesis on PyPI; oasdiff; OWASP API Security 2023 API5; PMD CPD docs; Stryker.NET configuration; xUnit v3 4.0.0 release notes; Testcontainers resource reuse; Bazel test encyclopedia.
- **Papers:** arXiv 2204.08348 (Kim et al.); Just et al. FSE 2014; arXiv 2102.11378 (Google); arXiv 2510.20270 (ImpossibleBench); BaxBench ICML 2025; OOPSLA 2025 property-based testing abstract.
- **Connapse CI:** run 39097835077 job logs.

**Secondary:**
- InfoQ fitness functions article; Ed-Fi Respawn benchmark; OpenCVE CVE-2026-53507; DevClass on GitClear.

**Tertiary:**
- doria.fi RESTler thesis; Verify.XunitV3 NuGet note.
