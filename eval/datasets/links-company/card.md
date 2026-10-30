# links-company

A company-sized link set, built for CON-130. Suite `links-company`. It holds real Jira, Confluence and GitHub
records with their own links in one dataset, so one container holds them all, and asks links-v3's and links-v2's
questions over the bigger corpus.

- **Corpus (42,572 records):**
  - the 40,816 FLINK Jira issues created before 2026-10-01: links-v3's 3,423 from 2023
    ([links-flink-atlassian](../links-flink-atlassian/card.md)), plus 37,393 fetched from issues.apache.org on
    2026-10-29 with the same fields and Epic Link;
  - links-v3's 815 FLINK wiki pages;
  - links-v2's 941 Destrayon/Connapse GitHub issues and pull requests
    ([links-connapse-github-2](../links-connapse-github-2/card.md)).

  A 100–1,000-user company is Atlassian's "Small" Data Center tier (up to 150,000 issues), so the set sits inside
  it. The filler is the same company's records, not random documents, because random filler flatters retrieval
  ([research](../../../docs/research/company-link-set-2026-10-29.md)).
- **Links (19,764):** the Jira and wiki links are found as links-v3 finds them, now over every issue, so a link to an
  issue outside 2023 counts. The GitHub links are links-v2's pinned ones. Jira and GitHub records never link each
  other, since neither side names the other.
- **Questions (390, 197 held out):** links-v3's 307 and links-v2's 83, with their ids, kinds and split. Each
  question's answers are widened to every record its start links to the same way in the bigger corpus, because a
  linked record judged absent from the smaller corpus is a right answer once it is indexed (pooling bias). 36
  questions were widened, the largest answer set has 21 records, and no record is in both halves. Old answers are
  always kept.

## Rebuilding

The records are saved outside the repository, as links-v3's are. The build reads three snapshots from one folder,
with no network call, and checks each against its pin:

    python eval/tools/build_link_suite.py links-company --snapshots=<dir>

- `<dir>/links-flink-atlassian/records.jsonl.xz`: links-v3's snapshot;
- `<dir>/links-company/fill.jsonl.xz`: the other FLINK issues, pinned by `COMPANY_FILL_SHA`;
- `<dir>/links-connapse-github-2/records.jsonl.xz` and `links.jsonl`: links-v2's pinned records and links.

On the project's eval PC the folder is `E:\tmp\con-130\snapshots`. `--fetch` fetches the fill again into that
folder and prints its hash; setting `COMPANY_FILL_SHA` to it and building with `--repin` writes new pins, and
earlier results are then not comparable. The build streams its
input in two passes and peaked at 0.19 GiB; three rebuilds were byte-identical and matched the pins.
