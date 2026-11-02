# How can Stryker.NET's CI wall time be cut without testing less?
**Date:** 2026-10-28
**Status:** Reviewed
**Built on:** no prior corpus material

## Executive summary
Stryker.NET 5.0.0 has no option to skip or shorten its own build, its initial test run, or its coverage pass,
so on Connapse's mutation shards (about 3.5 of each shard's 4.8 minutes is fixed cost) the remaining levers
are outside Stryker: MSBuild properties passed as environment variables, .NET first-run settings, and the
workflow's job graph. Larger GitHub runners are not available on the Free plan, and building once to share
across shards adds a serial job that makes wall time worse. The biggest measured lever is the build: turning
off analyzers and the build-time OpenAPI document cut the test project's Release build by a third locally.

## Research brief
**Question:** how to cut Stryker.NET 5.0.0 wall time on ubuntu-latest (4 vCPU) for Connapse.Core.Tests
without reducing the mutants or tests run.
**Sub-questions:** Stryker build/test-run options; generic dotnet build and first-run speedups; runners,
sharding, and caching.
**Out of scope:** changing the test project, the test filter, coverage mode, or the score gate.

## Findings by sub-question
### Stryker.NET options (primary: docs, source, issues)
- No skip-build or build-args option. Issue #466 (build args) is open since 2019; #2364 (`--skip-build`)
  was closed because Buildalyzer's analysis cleans the build output. Stryker runs
  `dotnet build <proj> -c <cfg> --property:Platform=<p>` (`MsBuildHelper.BuildProject`).
- Environment variables reach both analysis and the build; `Directory.Build.rsp` reaches the build only,
  since analysis passes `/noAutoResponse`.
- No option skips the initial test run; coverage capture is a separate session (`CaptureCoverageInOneGo`).
- `--test-runner mtp` (preview 4.13.0, per-test coverage 5.0.0) needs xUnit v3 and has no `--filter`
  under xUnit v3, which this config depends on. Not usable.
- `perTest` is already the cheapest coverage mode. `--since` and `--with-baseline` shrink only mutant
  testing; the build, initial run and coverage pass always run.
- `--concurrency` defaults to half the CPUs; the shards already pass `$(nproc)`.

### dotnet build and first run (primary: Microsoft docs, SDK source)
- MSBuild reads environment variables as properties unless the project sets them; the SDK defaults
  `RunAnalyzers` and `OpenApiGenerateDocuments` conditionally, so environment variables override them.
- `RunAnalyzers=false` skips diagnostic analyzers but still runs source generators (Razor, logging).
  Analyzers don't change the compiled assembly.
- `DOTNET_GENERATE_ASPNET_CERTIFICATE=false` skips dev-certificate generation on a fresh runner's first
  `dotnet` command; cost unmeasured in sources.
- The .NET 10.0.401 SDK is preinstalled on the ubuntu-24.04 image, so setup-dotnet can skip the 240 MB
  download when the image has it.

### Runners, sharding, caching (primary: docs.github.com)
- Public repos get 4 vCPU / 16 GB on ubuntu-latest and ubuntu-24.04-arm. Larger runners need a Team or
  Enterprise plan and are billed per minute.
- With ~3.5 min fixed per shard, more shards save seconds at most; fewer cost little more.
- Sharing bin/obj across jobs puts a ~2.5 min build job in series before the shards.

## Measured locally (this repo, 2 cores, clean Release build of Connapse.Core.Tests)
| Setting | Build |
|---|---|
| baseline | 55 s |
| RunAnalyzers=false | 44 s |
| OpenApiGenerateDocuments=false | 47 s |
| both | 37 s |

## Conflicts and uncertainties
- Whether setup-dotnet reuses a preinstalled SDK depends on the image version per runner; two of three
  shards downloaded it, one didn't.
- The dev-certificate and environment-inheritance claims rest on SDK source reading, not measurements.

## Gaps
- No public write-up with measured Stryker.NET CI numbers was found.
- No measured cost for `actions/cache` restore of a ~1 GB NuGet folder.

## Sources
Primary: stryker-mutator.io/docs/stryker-net/configuration; github.com/stryker-mutator/stryker-net (issues
#466, #2364, #3844; MsBuildHelper.cs, InputFileResolver.cs, CoverageAnalysisInput.cs);
learn.microsoft.com (msbuild environment variables, response files, disable code analysis, dotnet
environment variables); source.dot.net DotnetFirstTimeUseConfigurer.cs; docs.github.com (hosted runners,
runner pricing, limits); actions/runner-images Ubuntu2404-Readme.md; actions/setup-dotnet README.
