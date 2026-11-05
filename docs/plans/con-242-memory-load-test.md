# Plan: memory spaces load test at 50,000 memories (CON-242)

Proves the speed targets in [con-238-memory-spaces.md](con-238-memory-spaces.md) (Targets and the load test) on
the code CON-240 and CON-241 merged into `epic/con-e3-memory-spaces`. Branch `pm/con-242-memory-load-test`, PR into
that epic branch.

1. **Load category.** Goal: a `Category=Load` test runs in no CI step and only the load test may use it.
   Done when `TestSizeRules` accepts Load for the listed class only, with unit cases.
2. **Load test.** Goal: `MemorySpacesLoadTests` seeds 50,000 current facts (about 65,000 versions) in one team space,
   200 spaces in all, a 500-version fact and a read-only viewer who can read 1% of the cited documents, then times
   every operation in the targets table 20 times per reader. Done when it builds and fails on a missed target.
3. **After numbers.** Goal: the test passes on a Postgres capped at 2 CPUs and 4 GB. Done when every target is met
   and the table of p95 times is saved.
4. **Before numbers.** Goal: the same data and readers timed on the pre-epic code (`bcce100d`), with the reads it
   had. Done when the before table is saved.
5. **Ship.** Goal: Release build with CI's flags, tests, PR with both tables, Codex review fixed. Done when the PR is
   open with green CI.

## Progress

- Phase 1: `TestSizeRules` accepts `Category=Load`, reserved to `MemorySpacesLoadTests` like LiveAws; unit cases in
  `TestSizeTests`. No CI filter selects Load. Verified: TestSizeTests 16/16. Next: write the load test.
- Phase 2: `tests/Connapse.Integration.Tests/Memory/MemorySpacesLoadTests.cs` seeds by SQL, times the application
  operations (list, count, spaces list, history) wired to the real Postgres stores, prints a p50/p95 table and fails on
  any missed target. Own collection, so it doesn't start the shared web app. Builds. Next: run it.
- Phase 3: passed twice with Ryuk off (2 min each, seed 2 s); every p95 under target, no code change needed. Worst
  margins: viewer every-version first page 140 ms of 150 (p50 65), full count 546 ms of 2 s. Table in
  E:\tmp\con-242\after.md. Next: before numbers on bcce100d.
