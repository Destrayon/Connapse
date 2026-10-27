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
- **Seed:** sampling and the split use 20261028. 305 packets, 167 tuning and 138 held-out, split by shared
  record as before. The held-out half has 106 Jira and 32 Confluence questions: the page tree joins most pages
  into a few large groups.

Each question was written by a model that saw only the start record's title, its body (first 1,500 characters,
tags stripped for pages) and what to ask for, and was checked by `build_link_suite.py questions` with the same
rules. Rejected questions went back to the same writer once. The questions were committed before any run.

## Gate (CON-34)

Scored once on the test split, `hybrid-atlassian-links` against `hybrid`. The Jira questions (`jira-*`) and the
Confluence questions (`cf-*`) must each gain at least +0.05 MRR@10, significant under the Holm-corrected paired
permutation test (p < 0.05). No `kind:` or `hops:` group may drop significantly. Settings are tuned on the dev
split only.

## Rebuilding

`python eval/tools/build_link_suite.py links-flink-atlassian` re-snapshots and checks the pins. Records edited
after the snapshot change the hash and the build fails; `--repin` takes a new snapshot.
