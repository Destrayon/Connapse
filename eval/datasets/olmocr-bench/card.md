# olmocr-bench

- **What it tests:** whether Connapse's PDF parsing and chunking keep the text a reader needs. The tests check that text is present, that page headers and footers are absent, that multi-column text stays in reading order, and that table cells sit next to the cells they belong beside.
- **Source:** https://huggingface.co/datasets/allenai/olmOCR-bench (revision 54a96a6…). olmOCR-bench (Poznanski et al., https://arxiv.org/abs/2502.18443). The test logic is ported from olmocr commit f7cfe4c2 (`tests/Connapse.Eval/Checks/OlmOcr`).
- **Upstream licence:** ODC-BY 1.0. The PDFs are downloaded into `eval/.cache` at run time and never committed.
- **Size:** 1,403 single-page PDFs (about 335 MB) and 7,019 tests in 7 category files. Every PDF with no `baseline` test gets a default one (benchmark.py's convention), in a separate `baseline` category.
- **Labels:** unit tests written and verified by the olmOCR authors, not relevance judgments. `checked` is "verified" on all but 9 non-math tests.
- **Categories:**
  - **Headline (native PDFs):** `headers_footers` (absent tests), `long_tiny_text` (present), `multi_column` (order), `table_tests` (table).
  - **Scans, reported separately:** `old_scans` and `old_scans_math`. They need OCR, which Connapse does not have.
  - **`arxiv_math`:** 2,927 math tests, all skipped, because they compare LaTeX rendered in a browser. Its PDFs still get baseline tests and the no-silent-failure check.
- **File list:** the manifest pins the JSONL files. The PDFs are listed with their SHA-256 in `files.sha256`, written by `datasets pin`.
- **Known limitations:**
  - `table` tests look for markdown tables only (the HTML table parser is not ported). Connapse emits plain text, so table tests measure whether table structure survives at all.
  - olmOCR scores several repeated runs per page; Connapse's parser is deterministic, so each test runs once.
