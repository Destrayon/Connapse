# CON-239: The Memory page, memory spaces in the nav, and Copy to team

Ticket 5 of [con-238-memory-spaces.md](con-238-memory-spaces.md); the pictures are in `docs/images/memory-spaces/`.
Base branch and PR target: `epic/con-e3-memory-spaces`.

## Phases

1. **Copy to team.** Goal: `CopyMemoryFact` operation and `POST /api/memory/facts/{id}/copy`.
   Done when tests show it copies only the current version and its citations, leaves the personal fact unchanged,
   is refused for read-only users and non-owners, and the copy stays hidden from a teammate who can't read a cited document.
2. **Paged fact list.** Goal: one fact list component that pages through a space on the server (Previous/Next,
   search, Current/All, count loaded after the page) and replaces the container tab's 100-row recall.
   Done when the bUnit tests for the container tab pass on the new list.
3. **Container tab label.** Goal: the container Memory tab reads "Team memory" and points to the Memory page,
   in commits of its own so CON-244 can drop it. Done when its bUnit test passes.
4. **Remove Agent memory from Create Container.** Goal: Create Container makes document containers only.
   Done when the picker is gone and Home's tests pass.
5. **Memory page.** Goal: Memory in the nav, `/memory` with personal and team cards and the containers table,
   the New space dialog, and `/memory/{id}` with the Viewer notice and the Copy to team dialog.
   Done when bUnit tests cover an Editor and a Viewer on each view.
6. **50,000-memory browser test.** Goal: a real browser opens a 50,000-fact space; the first page, page 2 and a
   search each show within 1 second. Done when the test passes locally.
7. **Hand-off.** Goal: screenshots of every view for an Editor and a Viewer, Release build with CI's flags, tests,
   Codex adversarial review and fixes, draft PR into the epic branch, message to ClaudePM.

## Progress

**Phase 1 (done).** `CopyMemoryFact` (plan, run, destinations) and `POST /api/memory/facts/{id}/copy` with a 409 `memory-fact-exists` unless `anyway`.
Files: `CopyMemoryFact.cs`, `MemoryOperations.cs`, `MemoryFactsEndpoints.cs`, openapi.json, the authz and capability catalogs, `MemoryCopyTests.cs`.
Verified: 9 copy tests (current version only, personal unchanged, Viewer 403, non-owner 404, hidden from a teammate who can't read a source, REST) plus the authz, capability and spec tests pass.
Next: the paged fact list component.

**Split proposed to ClaudePM (2026-11-04).** The ticket is ~2,000+ lines, so I asked for four PRs: (1) this PR,
Copy to team plus removing Agent memory from Create Container; (2) the paged fact list and the 50,000-memory browser
test; (3) the Memory page, New space dialog and the container tab's "Team memory" label; (4) the space view with the
Viewer notice, group by subject and the Copy to team dialog. Phases 2, 3, 5 and 6 wait on the PM's answer; this PR
does Phases 1, 4 and 7.

**Split approved (2026-11-04).** CON-239 is now only Copy to team and removing Agent memory. The fact list, the
Memory page and the space view moved to CON-247, CON-248 and CON-249, so Phases 2, 3, 5 and 6 are dropped here.

**Phase 4 (done).** Create Container makes document containers only: the type picker is gone and a note points to the
Memory page. Files: `Home.razor`, `ContainerKindPicker.razor(.css)` deleted, `MemoryContainerPagesTests.cs`, the
component budget, `roles-and-scopes.md`. Verified: the dialog test checks there is no type choice and the kind sent is Documents.

**Phase 7 (done).** Release build with CI's flags is clean; unit, architecture, web and memory-copy tests pass.
Codex (gpt-6.1-sol) found three real bugs, fixed in d61110c0: copy now needs knowledge:read, refuses a version a hidden
newer one replaced, and answers Unchecked when the duplicate search was refused or cut off. Left as is: two copies at
the same instant can both pass the duplicate check; it is a warning and nothing is overwritten.
`CrossContainer_ObjectAccess_Returns404` fails locally only because Ollama isn't on 11434. Next: review changes.
