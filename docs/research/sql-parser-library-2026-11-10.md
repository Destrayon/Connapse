# Which .NET library should parse agent-written SQL for the table_sql allowlist?
**Date:** 2026-11-10
**Status:** Reviewed
**Built on:** rls-raw-sql-agents-2026-11-10.md (section 2 picked `pgsqlparser`; this note checks that pick against the
packages themselves)

## Executive summary
Use `pgsqlparser` 2.1.0. It wraps libpg_query 18.1.0 (the PostgreSQL 18.6 parser), is MIT-licensed, and is the only
maintained binding that ships native builds for every platform Connapse runs on, Alpine (musl) included. It also parses
deeply nested input on its own large-stack thread and rejects trees deeper than 4,000 levels, so a hostile query can't
crash the server's thread with a stack overflow. That matters for a security check that must never fail open or take
the process down. All three bindings are one-maintainer projects with a handful of GitHub stars, so the check walks the
raw protobuf tree with its own default-deny rules instead of trusting the package's convenience helpers.

## Research brief
**Question:** which .NET library should parse PostgreSQL SQL with the real PostgreSQL parser for CON-325's allowlist?
**Sub-questions:** real grammar version; native builds (win-x64, linux-x64/arm64, linux-musl, macOS); licence;
release recency; typed parse tree; thread safety and crash resistance; known issues.
**Out of scope:** the allowlist rules (CON-320's research sets them); non-.NET bindings.
**Success criteria:** one named package with the evidence for it.

## Findings
Facts below come from downloading each package from nuget.org and reading its nuspec and `runtimes/` folder, plus
each repository's README and docs (primary sources, read 2026-11-10).

| | pgsqlparser 2.1.0 | Npgquery 1.2.0 | PostgresQuery 0.1.4 |
|---|---|---|---|
| Parser | libpg_query 18.1.0 (PG 18.6 parser) | libpg_query 18.1.0 | libpg_query (version not stated) |
| Native builds | win-x64/arm64, linux-x64/arm64, **linux-musl-x64/arm64**, osx-x64/arm64 | win-x64/arm64, linux-x64/arm64, osx-arm64; **no musl, no osx-x64** | same eight as pgsqlparser |
| Licence | MIT (libpg_query BSD-3) | MIT | BSD-3-Clause |
| Releases | 0.2.0 2025-10-25 … 2.0.0 and 2.1.0 2026-10-09; last push 2026-11-06 | 1.0.0 2026-06-15 … 1.2.0 2026-10-07 | four versions 2025-12-07/08, nothing since |
| Tree | protobuf `ParseResult`, `Result<T>` errors, typed visitors | protobuf and JSON | protobuf |
| Threads and depth | static API; nested input parsed on a dedicated big-stack thread; trees over 4,000 levels rejected with an error | README: "The `Parser` class is **not thread-safe**" | not documented |
| Open issues | 0 | 2, one a protobuf schema mismatch against the latest libpg_query | 0 |
| Dependencies | Google.Protobuf 3.36.2 | Google.Protobuf 3.35.1, System.Text.Json, System.Memory | Google.Protobuf 3.25.3 |
| Package size | 9.5 MB | 16 MB | 15.6 MB |

**Pure-managed parsers** (`gudusoft.gsqlparser`, `SqlParserCS` and similar) were ruled out without a package check.
They re-implement the grammar, so they can read a query differently from the server that runs it, and a check built
on such a parser is the "differential parsing" weakness that libpg_query exists to avoid (rls-raw-sql-agents-2026-11-10.md
section 2; pganalyze's libpg_query README). gsqlparser is also commercial.

## Conflicts and uncertainties
- Popularity is low for all three bindings (3, 3 and 1 GitHub stars; pgsqlparser has about 142k NuGet downloads). The
  check must not depend on the binding's own analysis helpers (`Classify`, `GetReferences`), only on the protobuf tree,
  which comes straight from libpg_query.
- pgsqlparser's 4,000-level cap and its large-stack thread are documented by the package itself, not independently
  tested here beyond Connapse's own deep-nesting test.

## Gaps — what we did not find
- No independent security review of any of the three bindings.
- No published benchmark; CON-325's own timing test is the speed evidence.

## Sources
- **Primary:** [pgsqlparser on NuGet](https://www.nuget.org/packages/pgsqlparser) and
  [repository](https://github.com/mysticmind/pgsqlparser-dotnet) (README, docs/guide/errors-offsets.md, analysis.md);
  [Npgquery on NuGet](https://www.nuget.org/packages/Npgquery/) and [repository](https://github.com/JaredMSFT/Npgquery);
  [PostgresQuery on NuGet](https://www.nuget.org/packages/PostgresQuery/) and
  [repository](https://github.com/hbirler/pgquery-sharp); [libpg_query](https://github.com/pganalyze/libpg_query);
  nuget.org registration API for release dates; GitHub API for push dates and issue counts.
