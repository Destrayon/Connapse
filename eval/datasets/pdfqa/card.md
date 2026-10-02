# pdfqa

- **What it tests:** whether the passage that answers a question about a real PDF is retrieved. Parser gaps that break a table row, scramble columns, split a sentence around a running header, or misread a scan show up as a missed passage (#643).
- **Judging:** each question names its PDF and 1–3 evidence strings copied from the page. A retrieved chunk of that PDF is the answer if it contains every evidence string, ignoring case, punctuation, markup, whitespace and thousands separators (`PassageJudge`). The strings must also sit close together: at most 15 other words between them for table questions (a row label and its cell) and 60 for the rest, or the question's own `maxGapWords` where a table's cells are prose. A chunk holding the words scattered apart does not count. Only the first such chunk counts, so each question has exactly one relevant item and Recall@k, nDCG@10, MRR@10 and hit@k read as usual. Judgments depend on the run's chunks, so they are written into the run's `qrels/`.
- **Corpus (13 PDFs, all US government works, public domain):**
  - Census P60-279 *Income in the United States: 2022*: multi-column text, tables without rules (leader dots), running footers.
  - IRS Publication 15 (2024): two columns, ruled tables, running headers.
  - Federal Register 2023-28800 and Congressional Record S93 (11 Jan 2024): three columns.
  - Eight JFK Records Act releases (National Archives, 2025): image-only typewritten scans, from clean to badly degraded.
  - NASA NTRS 19930090935 (NACA report): degraded scan carrying an existing OCR text layer.
- **Questions:** 92, written for #643 from the rendered pages, not from parser output or any other benchmark. Kinds (the `kind:` breakdown in reports): columns 24, scan-typed 18, scan-degraded 18, header-break 12, table-ruled 11, table-borderless 9. Native-PDF evidence was checked against the text layer; scan evidence was checked by eye.
- **Files:** the PDFs download from their publishers into `eval/.cache`; `questions.jsonl` is committed here and copied by the `repo:` manifest URL. Editing it changes its SHA-256: set its `sha256` in the manifest to `null`, bump the dataset version, and run `datasets pin --suite pdfqa-v1`.
- **Known limitations:**
  - Evidence containment shows a passage holds the right words, not that a table cell stays aligned with its column header.
  - A few answers are stated twice in the same PDF (text and table, or two tables); a chunk from either place counts.
  - The NACA tables were too blurred to write questions from; scan questions use body text only.
