# links-connapse-github

Link questions over Destrayon/Connapse's own issues and pull requests (CON-30): the answer is a record linked to
the one the question describes, one or two links away. Part of suite `links-v1` with `links-flink-jira`.

- **Corpus:** the 562 issues and pull requests created before 2026-10-01T00:00Z, with the comments made before
  then, as the GitHub REST API returns them. The harness renders each with `GitHubRecordRenderer` and ingests it
  as `<number>.md` with the Record chunking strategy, as the GitHub connector does.
- **Links (the answer keys):** a pull request closes an issue (GraphQL `closingIssuesReferences`, which also
  counts links made in the sidebar) and an issue is a sub-issue of another (REST `sub_issues`). 180 links.
  Connapse never produced any of them.
- **Questions:** 98. Kinds and hops are tags (`kind:gh-closed-by`, `hops:2`):

  | Kind | Hops | Asks for |
  |---|---|---|
  | gh-closed-by | 1 | the pull request that fixed an issue |
  | gh-closes | 1 | the issue a pull request resolved |
  | gh-parent | 1 | the epic a sub-issue belongs to |
  | gh-subissues | 1 | the sub-issues of an epic |
  | gh-siblings | 2 | the other sub-issues of the same epic |
  | gh-epic-delivery | 2 | the pull requests that closed an epic's sub-issues |

## How the questions were made

1. `build_link_suite.py packets` sampled link paths with a fixed seed (20261024). A record starts at most one
   question; scarce two-step kinds were sampled first. Lists of more than 10 answers were skipped.
2. The split was fixed in the same step, before any question existed: within each kind, alternate questions
   are dev (tuning) and test (held-out). Headline numbers are the test split's.
3. Each question was written by a model that saw only the start record's title, its body (first 1,500
   characters, no comments) and what to ask for. It never saw the answers, other records or search results.
4. `build_link_suite.py questions` rejected any question that names a record number, key or URL, repeats
   five or more consecutive title words, or falls outside 6 to 45 words. Rejected questions went back to the
   same writer, still without answers, for a rewrite; one that failed twice was dropped.

## Rebuilding

`python eval/tools/build_link_suite.py links-connapse-github` re-snapshots and checks the pins. Records edited
on GitHub after the snapshot change the hash and the build fails; `--repin` takes a new snapshot, which makes
earlier results incomparable.
