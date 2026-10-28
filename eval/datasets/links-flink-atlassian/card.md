# links-flink-atlassian

Held-out link questions over Apache Flink's Jira and its Confluence wiki, built for CON-34. Suite `links-v3`.
Built the way [links-flink-jira](../links-flink-jira/card.md) was, with these differences:

- **Corpus:** the 3,423 FLINK Jira issues created in 2023 (issues.apache.org, public REST v2, with comments) and
  the 815 current pages of the FLINK space on cwiki.apache.org (REST `content`, storage body, ancestors and
  labels, without comments). One dataset, so a link from an issue to a page, or back, stays inside one container.
- **Links (2,923), from the APIs and the records' own text, never from Connapse:** Jira's Blocker, Duplicate,
  Problem/Incident and Reference (relates) links and sub-task parents; the wiki pages an issue's description
  links by page id or `/display/` title; a page's last ancestor as its parent; the pages a page links by
  `ri:page` title or URL; and the issues it names by a Jira macro or `/browse/` URL. Only links whose both ends
  are in the snapshot count. FLINK's 2023 issues set no Epic Link, so no question asks for an epic.
- **Kinds:** `jira-siblings` (2 hops), `jira-page`, `jira-duplicate`, `jira-blocked-by`, `jira-caused-by`,
  `jira-relates`, `jira-parent`, `jira-subtasks`; `cf-issues`, `cf-links-to`, `cf-parent`, `cf-children`.
  Labels and fix versions are stored but not asked about: their questions ("pages tagged like this one") have
  answers too broad to rank.
- **Seed:** sampling and the split use 20261029 (`packets links-flink-atlassian --second`). This is the second
  question set; see below.

## Second question set

The first set (seed 20261028, commit 8dc6f463) was scored once and its Confluence half was not a sound test: the
held-out split held no `cf-parent` question, because 24 of 30 parent questions shared one hub page as their
answer and the record-sharing split moved them all to tuning together; 32 Confluence questions could only
detect a gain of about +0.067; and 13 `cf-issues` answers restated the start page's title, so they tested plain
search rather than links. The second set fixes the sampling, not the system (link weights are unchanged):

- at most 2 questions share an answer record within a kind, so no hub dominates a kind;
- a packet is dropped when an answer's title restates the start's (content-word Jaccard of 0.5 or more,
  ignoring "FLIP" and numbers), for Jira and Confluence alike;
- the split balances each kind across the halves, still keeping questions that share a record in one half;
- no start or answer record of the first set's held-out half is used, so the new held-out half is unseen;
- higher Confluence quotas, for at least 60 held-out Confluence questions.

307 packets: 147 tuning and 160 held-out (90 Jira, 70 Confluence: 27 `cf-parent`, 22 `cf-links-to`, 15
`cf-issues`, 6 `cf-children`). 27 questions were rejected on first writing and all passed when rewritten once. The corpus and links are
the first set's, so its index is reused.

Each question was written by a model that saw only the start record's title, its body (first 1,500 characters,
tags stripped for pages) and what to ask for, and was checked by `build_link_suite.py questions` with the same
rules. Rejected questions went back to the same writer once. The questions were committed before any run.

## Gate (CON-34)

Scored once on the test split, `hybrid-atlassian-links` against `hybrid`. The Jira questions (`jira-*`) and the
Confluence questions (`cf-*`) must each gain at least +0.05 MRR@10, significant under the Holm-corrected paired
permutation test (p < 0.05). No `kind:` or `hops:` group may drop significantly. Settings are tuned on the dev
split only.

## Rebuilding

The records are saved outside the repository (CON-132), because they hold Apache contributors' names and comments.
On the project's eval PC the snapshot is `E:\tmp\eval-snapshots\links-flink-atlassian\records.jsonl.xz`.

    python eval/tools/build_link_suite.py links-flink-atlassian --snapshot=<path>/records.jsonl.xz

rebuilds `records.jsonl` and `links.jsonl` from that file, with no network call, and checks them against the pins.
The snapshot's contents must hash to the pinned `records.jsonl`, so a wrong or damaged file stops the build.
**This set can be rebuilt only where that file exists.** Without `--snapshot` the build fetches from the APIs,
which no longer return the pinned bytes. `--repin --snapshot=<path>` fetches the records again, writes new pins
and replaces the snapshot at that path; earlier results are then not comparable.

The records first pinned for this suite (sha256 `e32d05b3…`, used for CON-34's gate) were never saved, and the
APIs now return different bytes for some of them, so they cannot be rebuilt. The saved snapshot is the re-fetch
of 2026-10-28 (sha256 `164bb398…`), which CON-107's runs used. It has the same 3,423 issues and 815 pages, and its
`links.jsonl`, and so every answer key, is byte for byte the one pinned before. Only record text changed.
