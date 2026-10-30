# What should a company-sized test set with real link data look like?
**Date:** 2026-10-29
**Status:** Reviewed
**Built on:** no prior corpus material (Connapse MCP was not available in this session)

## Executive summary
A company-sized link test set for Connapse (CON-130) should be built from real records of one organisation that carry their own links, not from synthetic padding. About 40,000–45,000 records sits inside Atlassian's "Small" Data Center tier for 100–1,000 users: up to 150,000 Jira issues. Apache Flink alone supplies that. It has 41,220 public Jira issues and 816 Confluence pages, served without auth. Unrelated or random filler documents tend to raise scores and hide differences between systems. Same-domain records are the harder and more honest stress. The main uncertainty is that no study measures directly how same-domain filler, compared with unrelated filler, changes the ranking of retrieval systems. That recommendation is inferred.

## Research brief
**Question:** How big should a realistic company knowledge base with Jira, Confluence and GitHub links be, which public sources can fill it with pinnable real data, and what filler and pinning practice keeps the result honest and reproducible?

**Sub-questions:**
1. How large is a company knowledge base, and what is its source mix and link density?
2. Which public Jira, Confluence and GitHub sources with real links can be fetched and pinned, and under what terms?
3. Should filler be same-domain hard records or random documents, and how is a dataset pinned against source drift?

**Out of scope:** building a new question set (links-v3's and links-v2's questions are reused), and generation or answer quality.

**Success criteria:** a recommended corpus mix with a size anchor, named sources, and a pinning method.

## Findings by sub-question

### 1. Size, source mix and link density of a company knowledge base
A 100–1,000-person company fits Atlassian's "Small" tier: up to 150,000 Jira issues and up to 500,000 Confluence content items.
- **Size tiers (primary):**
  - Jira Data Center's Small profile: up to 150,000 issues, 200 projects and 1,000 users. Its Medium profile, for 1,000–10,000 users, covers 150,000–600,000 issues.
  - Confluence Data Center's Small profile counts every version, attachment and comment, so distinct pages are far fewer than its 500,000 items.
  - These are hardware-sizing ceilings, not averages.
- **Link density (primary):**
  - Montgomery, Lüders and Maalej (MSR 2022) report 2.7M issues and about 1M links across 16 public Jira instances. Apache averages 0.26 links per issue.
  - Lüders et al. ("Beyond Duplicates", MSR 2022) find that 34.8% of issues carry at least one link. Of 607,208 links:
    - general relation: 36.0%
    - composition (sub-task, epic): 31.4%
    - duplicate: 16.2%
    - temporal or causal: 13.9%
- **Code-to-ticket linking:** commit studies (Rath et al., ICSE 2018) find only 48–60% of commits reference an issue. No primary figure exists for pull requests.
- **Source mix:** EnterpriseRAG-Bench's synthetic corpus (about 512,000 documents) has GitHub 8,052, Jira 6,120 and Confluence 5,189 next to 285,605 Slack messages. That is one design choice, not measured data.
- **Confidence:** high for the size tiers and link density, low for the source mix.

### 2. Public, pinnable sources with real links
Apache Flink alone is large enough, and it already backs links-v3. Counts were taken live on 2026-10-29.
- **Flink Jira:** 41,220 issues. Per year: 2019 4,203; 2020 5,370; 2021 4,666; 2022 5,036; 2023 3,423; 2024 3,015; 2025 1,869.
- **Flink Confluence (cwiki FLINK space):** 816 pages.
- **apache/flink GitHub:** about 29,614 pull requests, with GitHub issues disabled.
  - Its pull requests name tickets only as "[FLINK-123]" in the title. Connapse does not read that as a link, so they would be link-free filler.
  - GitHub links must therefore come from Destrayon/Connapse.
- **Other Apache "departments" for a later, larger tier:**
  - Kafka: 20,110 issues and 1,670 wiki pages, with Kafka Improvement Proposals that link heavily to Jira.
  - Solr: 18,417 issues and 842 pages.
- **Montgomery et al.'s Public Jira Dataset** (Zenodo concept DOI 10.5281/zenodo.5882881, CC-BY-4.0, about 5.8 GB):
  - It is pinned, but it is Jira-only and stops in January 2022.
  - It is not needed, because the live REST API still serves Flink and Connapse can pin its own snapshot.
- **Terms:**
  - No ASF licence explicitly covers redistributing Jira or wiki text. That is why snapshots stay out of the repository, as links-v3's already does, and only hashes, keys and the fetch script are committed.
  - Destrayon/Connapse is the user's own repository.
- **Confidence:** high for the counts (live API), low for the redistribution terms.

### 3. Filler choice and pinning
Same-domain records are the right filler. Every file is pinned by sha256 and rebuilt from a saved snapshot, never re-fetched.
- **Random filler flatters results:** in RAG, related distractors lower answer accuracy, while random documents raised it by up to 35% (Cuconasu et al., SIGIR 2024, which measures generation rather than ranking).
- **Dense retrieval degrades with size:** it loses more effectiveness than sparse retrieval as the index grows (Reimers & Gurevych, ACL 2021). A 2026 preprint finds that system rankings flip across corpus sizes. So results at one size do not transfer to another.
- **Pooling bias:** pooled relevance judgements miss answers in documents that were added later (Buckley et al., 2007). The same applies here: a linked record that was outside the smaller snapshot is a right answer once it is in the corpus.
- **Pinning:**
  - ir_datasets verifies every download against a pinned checksum.
  - NeuCLIR's hash broke when maintainers edited a date field. That is why Connapse rebuilds from a saved snapshot (CON-132's practice) instead of re-fetching.
- **Confidence:** medium. The filler recommendation is inferred, not directly measured.

## Conflicts and uncertainties
- **erb-50k's size:** the brief described EnterpriseRAG-Bench as 50k documents. erb-50k is Connapse's 50,000-document sample of a corpus of about 512,000 documents. That corpus also adds deliberate noise (misfiled documents and near-duplicates), so it is not pure random padding.
- **Size effects:** whether link search's gain holds at company size is exactly what this set measures. One single-source preprint says rankings flip with corpus size.
- **Wiki count:** the wiki has 816 pages live against 815 in the links-v3 snapshot. links-company keeps the pinned 815.

## Gaps — what we did not find
- No controlled study of same-domain against random filler on retrieval system rankings.
- No primary figure for the share of pull requests that link a ticket.
- No explicit ASF licence for redistributing Jira or cwiki text.

## Source quality assessment
- The size tiers, link density, the distractor effect, dense scaling and pooling bias rest on primary sources: Atlassian docs and peer-reviewed MSR, SIGIR, ACL and IR papers.
- Corpus-size ranking flips rest on single preprints.
- The live counts are primary: the APIs themselves.

## Sources
**Primary**
- Atlassian, Jira Data Center size profiles — https://confluence.atlassian.com/enterprise/jira-data-center-size-profiles-955171062.html
- Atlassian, Confluence Data Center load profiles — https://confluence.atlassian.com/enterprise/confluence-data-center-load-profiles-946603546.html
- Montgomery, Lüders, Maalej, "An Alternative Issue Tracking Dataset of Public Jira Repositories", MSR 2022 — https://arxiv.org/abs/2201.08368 ; data https://doi.org/10.5281/zenodo.5882881
- Lüders, Montgomery, Maalej, "Beyond Duplicates", MSR 2022 — https://arxiv.org/abs/2204.12893
- Rath et al., "Analyzing requirements and traceability information to improve bug localization", ICSE 2018 — https://arxiv.org/abs/1804.02433
- Cuconasu et al., "The Power of Noise", SIGIR 2024 — https://arxiv.org/abs/2401.14887
- Reimers & Gurevych, "The Curse of Dense Low-Dimensional Information Retrieval for Large Index Sizes", ACL 2021 — https://aclanthology.org/2021.acl-short.77
- Hawking & Robertson, "On Collection Size and Retrieval Effectiveness", Information Retrieval 2003 — https://doi.org/10.1023/A:1022904715765
- Buckley et al., "Bias and the limits of pooling for large collections", Information Retrieval 2007 — https://www.nist.gov/node/650056
- MacAvaney et al., "Simplified Data Wrangling with ir_datasets", SIGIR 2021 — https://arxiv.org/abs/2103.02280
- Apache Jira REST API and Confluence REST API counts, fetched 2026-10-29

**Secondary**
- EnterpriseRAG-Bench dataset card — https://huggingface.co/datasets/onyx-dot-app/EnterpriseRAG-Bench
- NeuCLIR hash discussion — https://huggingface.co/datasets/neuclir/neuclir1/discussions/6
- Corpus-size ranking flips (preprint) — https://arxiv.org/abs/2607.26497
