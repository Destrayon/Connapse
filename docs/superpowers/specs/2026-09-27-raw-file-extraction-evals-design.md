# Raw-file extraction evals — design

**Date:** 2026-09-27
**Issue:** #553
**Status:** Approved in brainstorming, awaiting spec review
**Research:** `docs/research/document-shape-robustness-2026-09-27.md`
**Builds on:** `docs/superpowers/specs/2026-09-25-eval-core-harness-design.md` (the core harness)

## Purpose

The harness can't see document-shape problems today. `ConnapseSearchSystem` writes every dataset document to `NNNNNNN.txt`, so only TextParser and the Semantic chunker ever run. This sub-project lets the harness:
- ingest raw files through Connapse's real parsers;
- check what survived extraction and chunking;
- check that files Connapse cannot read fail visibly.

It needs no embedding model, so it runs in minutes on any machine with Docker.

This is sub-project A of four document-shape eval sub-projects:

| | Sub-project | Depends on |
|---|---|---|
| **A** | **Raw-file ingestion and extraction checks (this spec)** | core harness |
| B | Retrieval suite over real documents (ViDoRe v3, OHRBench, T²-RAGBench, UDA), page-level scoring | A |
| C | Self-built Office set (DOCX/PPTX/PDF/HTML renders) and format-invariance report | A |
| D | Per-PR CI subset of A's checks | A, C |

A replaces the olmOCR-bench part of the core harness's sub-project 7.

## Scope decisions

These were settled in brainstorming on 2026-09-27.

**In scope.** A shape is in scope when its answer lives in the file's text:
- native PDF, DOCX, PPTX, TXT and Markdown;
- HTML (from sub-project B on);
- document shapes inside those formats: tables, multi-column pages, headers and footers, long documents, footnotes, speaker notes and versions.

**Must fail loudly.** A shape is in this bucket when Connapse cannot read it. The check passes only when Connapse reports the failure, never when it shows "Ready" with empty text. Examples:
- scanned or image-only PDFs;
- password-protected, truncated or wrong-extension files;
- zip bombs.

**Deferred, each with its reason:**
- *Needs vision:* charts whose information exists only in pixels, handwriting, image files as documents, image queries.
- *Needs computation over a whole dataset:* XLSX, CSV and JSON/YAML/XML as data files. These are reserved for a future structured-query data type, which will not use RAG.
- *Excluded by an existing decision:* source code, which the GitHub connector leaves out on purpose.
- *No demand evidence yet:* email.

Tables *inside* documents stay in scope.

## Architecture

All changes live in the harness (`tests/Connapse.Eval`, `tests/Connapse.Eval.Tests`) and `eval/`. Product code does not change. The checks describe today's behaviour, including its defects.

```
tests/Connapse.Eval/
  Model/        + DocumentKind.File, EvalDocument.FilePath
  Datasets/     + OlmOcrBenchAdapter, GeneratedNegativesAdapter, PypdfEncryptionAdapter, EncodingsAdapter
  Systems/      ConnapseSearchSystem uploads File documents as raw bytes; + IngestionProbe
                + HashingEmbeddingProvider (moved here from Connapse.Eval.Tests)
  Checks/       new: ported olmOCR-bench checks + Connapse checks (fail-loudly, silent failure)
    Matching/   ported rapidfuzz ratio/partial_ratio, fuzzysearch find_near_matches, table parsing, repeat detector
  Runs/         + ExtractRunner
  Reports/      + extraction section in the HTML report, extraction comparison
eval/
  MANIFEST.json            + suite "extract-v1", + file-list datasets
  datasets/<name>/card.md  one card per new dataset
  datasets/olmocr-bench/files.sha256   lock file for the 1,403 PDFs
```

Dependency direction is unchanged. `Checks/` depends only on `Model/` and the BCL. Only `Systems/` references Connapse projects.

### Raw files in the data model

`DocumentKind` gains `File`, and `EvalDocument` gains `FilePath`. `FilePath` points to a file in the dataset cache, and the extension of the file name is significant.

In `ConnapseSearchSystem.IndexAsync`, a `File` document is uploaded as `{index:D7}{ext}` from a file stream:
- The content type is left unset, so it comes from the extension, as a user upload's does.
- `Text` documents keep today's `.txt` path, so suite v1 is unaffected.
- `UploadRequest.Strategy` comes from an optional `chunkingStrategy` field in the system config. When the field is null, the product default applies.

`IndexReport` gains per-document outcomes, which the extract runner needs:
- whether the upload was accepted, and the upload error if it wasn't;
- the final `Status`, `ErrorMessage` and `IngestionState`;
- the chunk count and the time to reach a terminal state.

Today's ">1% failed makes the dataset invalid" rule stays for ranking runs. Extract runs don't apply it, because failure is what some extract datasets measure.

A document is terminal when it is Ready or Failed, or its upload was rejected. A document that hasn't reached a terminal state within 2 minutes is a **stall**. Stalls are recorded and fail every check on that document. They do not abort the run, unlike today's 30-minute stall timeout on ranking runs, which throws.

### IngestionProbe: two views of each document

After ingestion, `IngestionProbe` captures two texts per document, using the in-process host's services:

1. **Parsed text (level 1).** The harness selects an `IDocumentParser` from DI by extension, as the pipeline does, and calls it on the file. The parser's warnings are recorded too; Connapse returns them but never persists them. This view isolates extraction.
2. **Stored chunks (level 2).** The document's chunk rows, read through `IDbContextFactory<KnowledgeDbContext>` and ordered by chunk index. This is what search can see.

Connapse's own page markers (`--- Page N ---`, from `PdfParser.cs:67`) are stripped from both views before checking. They are an artifact of Connapse, and they would collide with olmOCR "absent" checks on page numbers.

### Checks

A check has an ID, a dataset, a category, a type, and the document it applies to. It evaluates to pass, fail or skipped, with a short detail string. Checks run at level 1, level 2, or both:

| Check type | Source | Level 1 (parsed text) | Level 2 (chunks) |
|---|---|---|---|
| `present` | olmOCR `TextPresenceTest` | passes if the text is found | passes if **one single chunk** contains it; this is the answer-in-single-chunk measure |
| `absent` | olmOCR `TextPresenceTest` (absent) | passes if not found | passes if no chunk contains it |
| `order` | olmOCR `TextOrderTest` | passes if `before` precedes `after` | not run: order is an extraction property |
| `table` | olmOCR `TableTest` | passes if a markdown table contains the cell with its required neighbours (HTML tables: see porting note) | passes if one chunk does |
| `baseline` | olmOCR `BaselineTest` | passes if the text is non-trivial and not degenerate repetition | not run |
| `math` | olmOCR `MathTest` | **skipped**: it compares rendered LaTeX, and math output is deferred | skipped |
| `fails-loudly` | Connapse | see below | — |
| `no-silent-failure` | Connapse | see below | — |

**Short chunks (amended while implementing).** olmOCR's `partial_ratio` aligns the shorter string inside the longer one. A chunk that is only a fragment of the reference text would therefore score 100 and pass a `present` check. At level 2, a chunk shorter than the reference text is treated as not containing it: it fails `present` and passes `absent` without being scored. Level 1 keeps olmOCR's behaviour unchanged.

Comparing the two levels attributes each loss:
- fails at level 1: extraction lost the text;
- passes level 1 but fails level 2: chunking split it;
- passes both: the text is available to retrieval, which sub-project B scores.

**fails-loudly** runs on documents in the must-fail-loudly datasets. It passes when the document reached a terminal state within the timeout and one of these holds:
- the upload was rejected with an error;
- `Status` is `Failed`, `ErrorMessage` is non-empty, **and** `IngestionState` is `Failed`.

`IngestionState` is included because that is what the UI badge reads. The check fails today on every file, because of the defect where a failed document shows as Ready (recorded in the research report). That is intended.

**no-silent-failure** runs on every `File` document in every dataset. A document fails it when both of these are true:
- it ended **not** Failed and **not** rejected;
- its parsed text is empty, or at least one PDF page produced no text. The page is known from the parser's per-page split.

Connapse never persists warnings, so today any such document counts as silent. The **silent-failure rate** (failing documents ÷ `File` documents) is the headline robustness number of an extract run.

### Porting the olmOCR checks

This follows the project rule on reference implementations: port line by line, keep the notice, and prove parity with fixtures.

- **Pinned sources:**
  - `allenai/olmocr` at `f7cfe4c22098b154c76b6ec950d1c0a464eecf8d` (Apache-2.0): `olmocr/bench/tests.py` (`normalize_text`, `TextPresenceTest`, `TextOrderTest`, `TableTest`, `BaselineTest`), `olmocr/bench/table_parsing.py` and `olmocr/repeatdetect.py`.
  - The matching functions those files call: `rapidfuzz.fuzz.ratio` and `rapidfuzz.fuzz.partial_ratio` (MIT), and `fuzzysearch.find_near_matches` with `max_l_dist` (MIT).
- **HTML tables are not ported.** `parse_html_tables` depends on BeautifulSoup's HTML parser, and no Connapse parser emits HTML markup into chunk text. A `table` check therefore looks for markdown tables only. If a document's own text contains a literal `<table>`, the check can under-count; the report notes this. (Amended 2026-09-27 while writing the plan.)
- **Scoring convention, as in olmOCR's `benchmark.py`:** a PDF with no `baseline` test gets a default one, grouped under a separate `baseline` category. The overall score is the mean of per-category pass rates. We report two overall numbers. One counts skipped `math` checks as failures, so it can be compared with published olmOCR-bench results. The native-PDF headline covers `headers_footers`, `long_tiny_text`, `multi_column`, `table_tests` and `baseline`.
- **Strings are compared as Unicode code points, as Python does.** Slicing, lengths, `lower()` (including final sigma and `İ`), `isalnum()` and the regex `\s` class all follow Python's rules rather than .NET's UTF-16 defaults.
- **Why port rather than depend:** FuzzySharp ports the older fuzzywuzzy, not rapidfuzz. Parity would need proving by fixtures either way, so we port the functions we call directly.
- **Notices:** each ported file carries the upstream license notice and the pinned commit.
- **Fixtures:** a Python script under `tests/Connapse.Eval.Tests/Fixtures/olmocr/` runs the reference at the pinned commit on:
  - about 200 inputs sampled from the benchmark (with a seed);
  - hand-picked edge cases: empty strings, Unicode, repeated text, malformed tables.

  It writes the expected outputs to JSON, which is committed. xUnit tests assert the C# port reproduces every value exactly, including float ratios to 1e-9. The script's committed output is the contract, so CI doesn't need Python.

### Datasets (suite `extract-v1`)

| Dataset | Adapter | Contents | Kind of check |
|---|---|---|---|
| `olmocr-bench` | `olmocr-bench` | 1,403 single-page PDFs in 7 categories and 7,019 tests, from `allenai/olmOCR-bench` at revision `54a96a6fb6a2bd3b297e59869491db4d3625b711` (ODC-BY 1.0) | olmOCR checks + no-silent-failure |
| `pypdf-encryption` | `pypdf-encryption` | the 17 PDFs in `py-pdf/pypdf` `resources/encryption` at `54d35184b9e984834823b3558dc096bd4e6c9e80` (BSD-3-Clause) | see below |
| `generated-negatives` | `generated-negatives` | files the harness generates deterministically at load time | fails-loudly |
| `generated-encodings` | `generated-encodings` | text files in known encodings, generated at load time | `present` checks |

**olmocr-bench.**
- **Categories:** `headers_footers`, `long_tiny_text`, `multi_column` and `table_tests` are the native-PDF shapes and headline the report.
- **Scans:** `old_scans` and `old_scans_math` are reported separately, as *scans (OCR deferred)*. Their checks still run, so the gap stays visible, and their documents feed no-silent-failure.
- **`arxiv_math`:** its `present` and `absent` checks run, and its `math` checks are skipped.
- **Scoring:** follows olmOCR's convention, a pass rate per category averaged across categories, so our numbers can be read beside published olmOCR-bench results.
- **Download.** The manifest pins the 7 JSONL files by SHA-256. The 1,403 PDFs are too many to list individually, so the manifest gains an optional `fileList` for each dataset: a base URL at the pinned revision plus a committed lock file (`files.sha256`), which `datasets verify` checks and `datasets pin` writes.

**pypdf-encryption.** The expected outcome depends on the file:
- `*-user-password.pdf` and `r6-both-passwords.pdf` cannot be opened without a password, so they must fail loudly.
- `*-empty-password.pdf` and `*-owner-password.pdf` open in any reader without a prompt, so they must extract. Their `present` checks are generated from the parsed text of `unencrypted.pdf`, which carries the same content.

The card records which rule applies to each file. `r4-aes-v2-no-key-length.pdf` does not say in its name: its rule is set when the card is written, by whether pypdf opens it with an empty password.

**generated-negatives.** The generator is seeded, and the output of each generator version is pinned by hash in the card. Every file must pass fails-loudly unless noted otherwise.
- A truncated PDF: the first half of the bytes of a fixed olmOCR-bench PDF.
- An image-only PDF: one PNG page with no text layer, built with PdfPig's `PdfDocumentBuilder`. Connapse already depends on PdfPig.
- Wrong extensions: PNG bytes named `.pdf`; plain text named `.docx`; a real PDF named `.docx`.
- A zero-byte `.pdf`, which must be rejected at upload.
- A DOCX containing only an image.
- A DOCX zip bomb: `document.xml` inflates to 200 MB of repeated text from about 200 KB. This checks that ingestion ends within the timeout without taking the host down. **Risk:** the host runs in-process, so an out-of-memory crash would end the run. The runner therefore processes this file last, and records "host crashed" if the process dies.

**generated-encodings.** The same known sentence, containing `café`, `naïve`, `Größe` and `日本語`, written as TXT and Markdown in five encodings: UTF-8 with and without BOM, UTF-16 LE with BOM, UTF-16 LE without BOM, and Windows-1252 (the Latin characters only). `present` checks at both levels detect garbled text (mojibake).

### Commands and output

```
extract  --suite <s> [--datasets a,b] [--config <file>] [--resume <runDir>]
compare  <runA> <runB>        (extended: detects extract runs and compares check outcomes)
datasets verify|pin           (extended: file-list datasets)
```

- **Embedder.** `extract` always uses `HashingEmbeddingProvider`, which is deterministic and offline. That class moves from `Connapse.Eval.Tests` into the harness. Only the vector side of ingestion runs, and no search happens.
- **Configs.** Configs in `eval/systems/connapse/*.json` still apply, for chunking settings and `chunkingStrategy`.
- **Run folder.** Extract runs use the existing run-folder conventions: a manifest with the git SHA and config hash, per-dataset `.done` markers and resume. Each dataset writes two files:
  - `documents.jsonl`: one row per document, with its upload result, status, error message, ingestion state, chunk count, time to terminal, level-1 character count, per-page empty flags and parser warnings;
  - `checks.jsonl`: one row per check and level, with its outcome and detail.
- **Report.** `report.html` gains an extraction section:
  - the headline silent-failure rate and fails-loudly pass rate;
  - olmOCR pass rate per category at level 1 and level 2, and the level-1-minus-level-2 gap per category (text lost to chunking);
  - the scans category, shown apart from the headline;
  - a table of failing documents with their status and the first failing checks.
- **Compare.** `compare` pairs checks by ID. Per category, it runs the existing paired permutation test on the 0/1 outcomes with Holm correction, and prints the minimum detectable effect, as ranking comparisons already do.

## Error handling

- **A document-level error** (parser exception, stall, rejected upload) is recorded on that document, and its checks fail with the error as the detail. It never aborts the run.
- **A dataset-level error** is a hard failure with exit code 1, as today. That covers a checksum mismatch, a missing lock-file entry, or an unknown check type in the JSONL.
- **Unknown olmOCR check fields** fail the dataset load. They mean the pinned revision changed, or the port is incomplete.

## Testing

- **Unit tests, per-PR CI** (`[Trait("Category", "Unit")]`):
  - parity fixtures for every ported function and check class;
  - each Connapse check against hand-built `documents.jsonl` rows;
  - adapter tests on small in-repo samples;
  - generator determinism: the same seed produces the same hashes.
- **Integration test** (`[Trait("Category", "Integration")]`, Testcontainers, fake embedder). It runs `extract` over a tiny dataset:
  - a DOCX built in code;
  - a generated image-only PDF;
  - a generated UTF-16 text file.

  It asserts the outcomes Connapse produces **today**, including fails-loudly = fail for the image-only PDF. The assertion carries a comment pointing at the "Ready while Failed" defect, so fixing that defect flips this test deliberately.
- **Manual acceptance.** `dotnet run --project tests/Connapse.Eval -- extract --suite extract-v1` finishes and produces a report. The resulting numbers go into the PR description as the first baseline.

## Delivery

This spec exceeds the 300-line PR limit, so it ships as stacked PRs, each independently green:
1. The ported matching functions and olmOCR checks, with parity fixtures. This is pure code with no harness wiring.
2. The raw-file data model, the `ConnapseSearchSystem` raw upload, `IngestionProbe`, and the move of `HashingEmbeddingProvider`.
3. The four adapters, generators, file-list download, cards and manifest.
4. The `extract` command, the Connapse checks, the run folder, the report and compare, and the integration test.

## Out of scope

- **Product fixes.** The OfficeParser table duplication, the "Ready while Failed" job bug, OCR, and persisting warnings or page numbers are all out of scope. The checks describe today's behaviour.
- **Other sub-projects.** Retrieval scoring on real documents is B; self-built Office documents and format invariance are C; the per-PR CI subset is D.
- **Other corpora.** SafeDocs and other malformed-PDF corpora are out: their per-file copyright is unclear, and the malicious set is unsafe to handle on a developer machine. Generated negatives cover the same failure classes.
- **Formats.** HTML (no parser yet; sub-project B adds its dataset) and every deferred shape listed under Scope decisions.
