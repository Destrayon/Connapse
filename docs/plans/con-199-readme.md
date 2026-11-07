# CON-199: README that explains Connapse in a minute (v0.4.0)

Outline, pitch and search terms come from [con-198-launch-plan.md](con-198-launch-plan.md) section 4. GIFs come from CON-200; this PR leaves marked placeholders at the shot-list paths.

1. **Rewrite the README.** Goal: follow the CON-198 outline. Done when every outline item is present and the GIF placeholders name their planned files.
2. **Run the quickstart from scratch.** Goal: a fresh clone in `E:\tmp\con-199` reaches a cited search result using only the README's commands. Done when it works as written, or the README/docs are fixed so it does, and nothing is left running.
3. **Check links and badges.** Goal: every relative link resolves and every badge and external link returns 200. Done when a script reports no failures.
4. **Review and test.** Goal: one Codex adversarial review with real findings fixed, and the repo's tests pass. Done when the review is handled and `dotnet test` is green.
5. **Open the PR.** Goal: PR from `pm/con-199-readme` into `main`, PM told. Done when the PR is open and the PM has the link.

## Progress
- Phase 1 done: `README.md` rewritten to the CON-198 outline (H1 pitch, five data badges, what/who, quick start, connect, tool table, seven features, eval table). GIF placeholders are HTML comments naming `docs/demos/{agent-remembers,quickstart-zero-to-result,permission-trimmed-search,memory-history,linked-results,code-tools}.gif`; the old hero stays until shot 1 lands. Next: fresh-clone quickstart run.
- Phase 2 done: fresh clone of `main` in `E:\tmp\con-199\Connapse`, the README's five commands, then setup form → `my-notes` → upload → hybrid search returned the passage labelled `halcyon-notes.md`. Agent key from **Admin** → **Agents** worked against `/mcp` (initialize + `search_knowledge`). Only deviation: host port 5099 and project `con199qs`, because the user's stack holds 5001. Stack, volumes and image removed. No README fix needed. Next: link and badge check.
- Phase 3 done: a script checked all 38 README links: every relative path and anchor exists, every external link and badge returns 200, and badges read build passing, tests 9849 passing, release v0.3.2, Glama rated A. Release badge now tracks GitHub's "Latest" release, not pre-releases. Next: Codex review and tests.
- Phase 4 done: one Codex adversarial review (gpt-6.1-sol) found 2 issues, both real and fixed. S3 and Azure permission filtering is off until an admin switches it on, so the README now says so and links the setup pages. The eval table's 0.565 was measured at the old fusion weight 0.75, so the rows are relabelled and today's default (reranker + 0.65 = 0.677) is added. Unit tests: 8,435 passed, 0 failed. Integration tests were not run, since the diff touches only Markdown. Links rechecked: 0 failures. Next: open the PR.
