# links-connapse-github-2

Fresh held-out link questions over Destrayon/Connapse's issues and pull requests, built for CON-33's retest after
the first attempt read `links-connapse-github`'s test split. Suite `links-v2`. Built exactly as
[links-connapse-github](../links-connapse-github/card.md) was, with three differences:

- **Corpus:** the 941 issues and pull requests created before 2026-10-27T00:00Z, with the comments made before then.
- **Starts:** a question starts only from a record created on or after 2026-10-01, so no start record was a start
  in `links-connapse-github`. Answers may be older records.
- **Seed:** sampling and the split use 20261027. 248 links; 83 packets, 46 tuning and 37 held-out, split by
  shared record as before.

Answer keys come from GitHub (GraphQL `closingIssuesReferences`, REST `sub_issues`), never from Connapse. Each
question was written by a model that saw only the start record's title, its body (first 1,500 characters) and
what to ask for, and was checked by `build_link_suite.py questions` with the same rules. The questions were
committed before any run against them.

## Rebuilding

`python eval/tools/build_link_suite.py links-connapse-github-2` re-snapshots and checks the pins.
