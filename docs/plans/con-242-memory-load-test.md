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
- Phase 4: a scratch copy of the seed (no scope, audience or owner columns; never committed) timed `RecallAsync` on
  bcce100d, the only read it had. The 1% viewer's first page took p95 955–1,020 ms, because hidden facts were dropped
  in app code 200 rows at a time; every other read was under target. Table in E:\tmp\con-242\before3.md. Next: ship.
- Phase 5: Codex (gpt-6.1-sol) found three real gaps, all fixed. The test used a fake document access (it now runs the
  real `UserDocumentAccess` over 5,000 seeded documents, faking only the S3 scope resolver and the verifier). The
  500-version chain cited nothing (its versions now cite documents, the viewer reading the even ones). The Load
  reservation matched the bare class name (it now matches project, namespace and class). With real access the full
  reader first missed 150 ms. Profiling showed Postgres spends about 3 ms checking 5,000 ids, but the request stalled
  about 40 ms in Docker Desktop's host port forwarding when its array was sent as 8 KB pieces. With a 1 MB Npgsql write
  buffer in the test, it passed twice; the worst page p95 was 88 ms. Table in E:\tmp\con-242\after.md. Verified: Release
  build with CI flags, all unit tests. Next: open the PR.
