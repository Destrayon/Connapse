# Which parsers should Connapse adopt to support more file types, more accurately and more resiliently?
**Date:** 2026-09-30
**Status:** Reviewed
**Built on:** document-shape-robustness-2026-09-27.md (failure modes per shape, Connapse code audit), the extract-v1 eval run `20260928-020641-967d761-extract-v1-connapse-extract` (Connapse's baseline extraction scores). Connapse MCP was not connected, so the pre-check used local reports.

## Executive summary
Connapse's biggest ingestion losses come from three things, and none of them needs a Python ML parser to fix. First, it fails silently. Second, it detects file type by extension only. Third, it uses a PDF text call that ignores layout, through an unofficial 2023 fork of PdfPig. The recommended path has three tiers:
1. A resilience layer: explicit Unsupported and Partial statuses, magic-byte and encoding detection, per-file timeouts and caps, and images skipped at sync time rather than failed.
2. A better in-process .NET fast path: official PdfPig with layout analysis, Tabula for tables, RapidOcrNet for scanned pages, and MimeKit, MsgReader, SmartReader and ReverseMarkdown for email and HTML.
3. An optional out-of-process "heavy engine" behind a swappable HTTP interface, for hard PDFs.

Docling is the best-licensed and best-served heavy engine (MIT code, Apache-2.0 models, a mature async REST server, structured output with page numbers). Its only published olmOCR-bench score (50.3) was run by a competitor and is well below MinerU's pipeline (72.7) and PaddleOCR-VL (80.0). So the heavy engine should be chosen by a bake-off on Connapse's own extract-v1 eval, not by reputation. The largest uncertainty is that almost every cross-parser accuracy number is vendor-run.

## Research brief
**Question:** How can Connapse, a self-hosted .NET 10 RAG platform, support the file types it handles badly or not at all, with better quality, accuracy and speed, and fail loudly instead of silently?

**Sub-questions:**
1. Docling: licensing, accuracy, speed, serving, failure modes.
2. Other open-source ML parsers (Marker, MinerU, PaddleOCR, olmOCR, VLM-based OCR): licensing for a hosted cloud, accuracy, CPU feasibility.
3. .NET-native and lightweight options for a fast path: PDF layout, OCR, HTML, email, legacy Office, encoding and type detection.
4. How production RAG systems make ingestion resilient, and how a .NET app should call a Python parser service.

**Out of scope:** image RAG (images are discarded for now); a structured-query path for spreadsheets (CSV/JSON stay in the text path, XLSX stays unsupported); embedding, reranker and chunker tuning; answer generation.

**Success criteria:** a tiered adoption recommendation with named libraries, licences and evidence, tied to Connapse's measured baseline and to its existing extract-v1 eval.

## Connapse's measured baseline (extract-v1, 2026-09-28)
The extract-v1 eval checks what Connapse's parsers keep from raw files. It uses olmOCR-bench, Allen AI's set of 1,403 PDF pages with binary pass/fail checks. It also uses generated encoding files and encrypted pypdf fixtures. The 2026-09-28 run, on commit 967d761, measured:

| Measure | Value |
|---|---|
| olmOCR native-PDF score, parsed text | 38.4% |
| Same, after chunking | 21.3% |
| Tables | 0.1% |
| Multi-column | 43.6% |
| Headers/footers | 49.2% |
| Long tiny text | 17.6% |
| Old scans (no OCR) | 13.3% |
| Silent-failure rate | 19.0% |
| Unreadable files reported as failed | 7.1% |

Latin-1 and BOM-less UTF-16 text files scored 0%. The UTF-16 files never reached a final state. Status fixes from issue #562 (PRs #563–#565) landed after this run, so the two failure-rate numbers are probably stale. Re-run extract-v1 before using them as the "before" figure.

Code facts verified on 2026-09-30:
- **Parser selection is by extension only:** `IngestionPipeline.cs:606`.
- **The file-type allow-list is checked only on upload paths:** UploadService, DocumentsEndpoints and McpTools. Connector syncs (S3, Azure, filesystem, GitHub) never consult it. An image in a synced bucket therefore becomes a Failed document ("Unsupported file type: .png") instead of being skipped.
- **PDF parsing uses `UglyToad.PdfPig 1.7.0-custom-5`.** NuGet lists this package's owner as the account "grinay", published 2023-06-05. The official package is `PdfPig` 0.1.16 (2026-08-22, owners BobLd and EliotJones, Apache-2.0). Connapse is on an unmaintained third-party fork of a renamed package ID. That is both a quality ceiling and a supply-chain risk.

## Docling as a candidate parser
Docling (docling-project, originally IBM Research) is the strongest candidate on licensing and serving, but not on measured PDF accuracy.

- **Licences.** Code and docling-serve are MIT. The heron layout model, the egret variants and granite-docling-258M are Apache-2.0. TableFormer is CDLA-Permissive-2.0 / Apache-2.0 [primary: GitHub, Hugging Face model cards]. This is safe for both open-source and hosted Connapse Cloud.
- **Release cadence.** v2.131.0 on 2026-09-29, releasing every 2–5 days [primary]. Fast-moving, so pin versions.
- **Accuracy:**
  - olmOCR-bench: 50.3 overall, 64.0 on born-digital pages. This was run by Datalab, a competitor [secondary, biased]; there is no per-category breakdown and no Allen AI leaderboard entry.
  - ParseBench: 50.6 overall, tables 66.9 [secondary, vendor benchmark].
  - Tables are its strength: TableFormer 93.6 TEDS [primary]. An independent 27-document run gave Docling the best table score of eight tools (0.83 TEDS) [tertiary].
  - Layout: heron scores 0.776 mAP on DocLayNet [primary, arxiv 2509.11720].
- **Speed and footprint:**
  - Standard pipeline with OCR and tables: 3.1 s/page on an 8-thread x86 CPU, 0.49 s/page on an L4 GPU [primary, arxiv 2501.17887]. A 1,000-page backlog is about 52 minutes per CPU worker.
  - Memory is about 2.5 GiB minimum per worker, about 8 GB practical [tertiary].
  - Images: docling-serve CPU 4.4 GB, CUDA 11.4 GB [primary].
- **Serving:**
  - docling-serve offers sync endpoints (`/v1/convert/file`, 120 s max wait) and async ones (`/async`, then `/v1/status/poll/{id}` and `/v1/result/{id}`) [primary].
  - Configuration covers caps (`MAX_FILE_SIZE`, `MAX_NUM_PAGES`, `MAX_DOCUMENT_TIMEOUT`, all unset or 7 days by default), `table_mode` fast/accurate, OCR engine choice, and an API key.
  - The JSON output carries page numbers, bounding boxes and heading hierarchy. HybridChunker returns heading paths per chunk [primary].
  - .NET clients are third-party only.
- **Formats:** PDF, DOCX, PPTX, XLSX, HTML, EML/MSG (attachments listed by name only), Markdown, EPUB, ODF, RTF. Legacy DOC/PPT need LibreOffice. Images can be dropped (`generate_picture_images=false`, placeholder export) [primary].
- **Open failure modes** [primary, GitHub issues]:
  - Memory leaks in docling-serve (serve #366, #474).
  - Out-of-memory around page 300 of 700-page PDFs (#3345).
  - Since v2.123 the default parser keeps only 5–9% of a scanned PDF's embedded OCR text (#4357, open).
  - Output is not reproducible with more than one worker (serve #701).
  - LibreOffice conversions have no timeout (#3819).

## Other open-source ML parsers
Licensing filters the field more than accuracy does, because Connapse is open source and also sold as a hosted cloud.

| Parser | Licence (code / weights) | olmOCR-bench | OmniDocBench v1.5 | CPU-viable | Notes |
|---|---|---|---|---|---|
| Docling (standard) | MIT / Apache | 50.3 [competitor-run] | not listed | Yes, 3.1 s/page | Best serving and tables |
| MinerU 4 pipeline | Apache + conditions / same | 72.7 [vendor] | 75.5 (old pipeline) | Yes, ONNX, 2–8 GB | Hosted services must show attribution; licence needed above 100M MAU or $20M monthly revenue |
| MinerU 2.5 VLM | same | 75.2 [Allen AI] | 90.67 | No | |
| PP-StructureV3 | Apache / Apache | unknown | 86.73, table 81.68 | Yes (GPU recommended) | Docker plus HTTP |
| PaddleOCR-VL (0.9B) | Apache / Apache | 80.0 [Allen AI] | 94.50 | ~53 s/page (impractical) | vLLM server |
| olmOCR 2 (7B) | Apache / Apache | 82.4 [Allen AI]; old scans 47.7 | 81.79 | No, 12 GB+ VRAM | ~30 GB image |
| Marker / Surya | Apache / revenue-capped (<$5M) | 76.0–83.2 [conflicting] | 71.30 | 43.6 without OCR | Weights need a paid licence for Connapse Cloud |
| Chandra 2 | Apache / <$2M, non-compete | 85.8 [vendor] | unknown | No | Excluded on licence |
| DeepSeek-OCR | MIT / MIT | 75.7 [Allen AI] | 87.01 | No | 2–3% of degraded pages loop |
| GLM-OCR (0.9B) | Apache / MIT | unknown | 94.6 [vendor] | Ollama, speed unknown | |

Sources: Allen AI leaderboard in github.com/allenai/olmocr [primary]; vendor repos and papers [primary, vendor]; MarkTechPost relaying Datalab [secondary].

Risks specific to RAG:
- **VLM-based parsers** (vision-language models that generate text from a page image) can loop: DeepSeek-OCR expands 2–3% of degraded pages up to 71×.
- **They also silently "correct" source text** [primary, arxiv 2606.29213]. Pipeline parsers cannot invent text on born-digital pages.
- **Scanned pages:** every tool tops out around 43–50 on old scans.

Shortlist:
- **CPU heavy engine:** Docling, the MinerU pipeline and PP-StructureV3, to be decided by bake-off.
- **Optional GPU tier:** PaddleOCR-VL (Apache, 80.0 on the Allen AI leaderboard) or olmOCR 2 (best freely licensed score on old scans).

## .NET-native fast path options
A large share of the quality gap can be closed in-process, with no Python. All of the following are .NET libraries with recent releases.

- **PDF layout.**
  - Switch from the fork to official `PdfPig` 0.1.16 (Apache-2.0).
  - Its Document Layout Analysis ships in-package [primary, PdfPig wiki]:
    - NearestNeighbourWordExtractor;
    - RecursiveXYCut and Docstrum page segmenters, for multi-column pages;
    - UnsupervisedReadingOrderDetector;
    - a decoration classifier that flags repeated headers and footers.
  - No published quality comparison against `page.Text` exists. Measure it on extract-v1's multi_column and headers_footers categories.
  - **Tables:** `Tabula` 1.0.1 (MIT, a port of tabula-java with stream and lattice modes; needs PdfPig ≥ 0.1.14) [primary]. Connapse scores 0.1% on tables today, so this is the cheapest large win to test.
  - **Alternative:** `PDFiumCore` (Apache-2.0, current). The only quality claim for PDFium text ordering is vendor-run (Kreuzberg: 69.9% → 90.7% F1 on multi-column) [secondary].
- **Detecting scanned or garbled pages.**
  - Check the quality of the text layer, not just whether one exists.
  - Signals: the tika-eval common-word ratio (share of words in the language's top 30k list) [primary]; non-letter character ratio (~0.25 baseline); images drawn on the page vs text extracted [tertiary, thresholds need tuning].
- **CPU OCR in-process.** Used only on pages the detector flags.
  - `RapidOcrNet` 4.2.0: Apache-2.0, by PdfPig's maintainer. Runs PaddleOCR v5/v6 models via ONNX Runtime and SkiaSharp, AOT-compatible [primary].
  - Alternatives: `Sdcb.PaddleOCR` 3.3.1 (Apache-2.0), or `TesseractOCR` 5.5.2 (Apache-2.0; charlesw's `Tesseract` package is stale since 2022).
  - There is no clean head-to-head on documents [thin].
- **HTML.** `SmartReader` 0.11.1 (a port of Mozilla Readability, Apache-2.0) for main content, then `ReverseMarkdown` 6.2.1 (MIT) to keep headings and tables. Readability had the best median F1 (0.970) in Bevendorff et al. 2023 [secondary]. The UI already offers `.html` in its file picker, but no parser exists.
- **Email.** `MimeKit` 4.18.1 for .eml and `MsgReader` 6.1.2 for .msg (MIT; MsgReader handles RTF and HTML bodies). Recurse into attachments with the same parser registry, dropping image attachments.
- **DOCX/PPTX.**
  - Fix the known defects in the existing OpenXML parser: duplicated tables; dropped headers, footers, footnotes and text boxes; one-run-per-line PPTX; missing speaker notes.
  - Emit tables as Markdown and headings as `#`, so the markdown-aware chunker applies.
  - `Codeuctivity.OpenXmlPowerTools` (MIT, .NET 10) is an option for tracked changes; its coverage is untested.
- **Other formats.** EPUB: `VersOne.Epub` (Unlicense). RTF: MsgReader's converter (RtfPipe is stale since 2021). Legacy .doc/.ppt and ODT: no good .NET library; route them to the heavy engine (Docling with LibreOffice, or Tika) or leave them unsupported.
- **Type and encoding detection.**
  - `FileSignatures` 7.3.0 (MIT) sniffs magic bytes.
  - `UTF.Unknown` 2.7.0 (MPL-1.1, file-level copyleft, fine as an unmodified dependency) guesses charsets.
  - Avoid Mime-Detective's paid TrID signature sets.
- **Rejected for the fast path.** MarkItDown: Python only, and its maintainers refuse an HTTP server [primary]. Kreuzberg/Xberg: MIT, with a .NET 10 binding, but renamed this year, about 5.7k downloads, and only vendor benchmarks. Revisit it in 2027.

## How resilient RAG systems structure ingestion
Production systems survive bad files with five habits:
- isolate and time-limit parsers;
- route cheap files away from expensive engines;
- escalate to OCR per page only when the text layer is bad;
- report partial and failed outcomes explicitly;
- cap everything.

Patterns observed [primary unless marked]:
- **Pluggable engines.** Open WebUI selects one extraction engine by configuration: Tika, Docling, MinerU, Marker, Azure DI, Mistral OCR, or a generic external HTTP loader. Plain text skips the heavy engine. MinerU gets a 300 s timeout.
- **Unknown types and encodings.** Onyx falls back from extension, to MIME type, to a printable-bytes check on the first 1 KB. It decodes strict UTF-8 first, then chardet. AnythingLLM returns a structured `{success, reason}` per file.
- **Protecting the PDF path.** Onyx runs PDF extraction in a subprocess with a timeout. It retries encrypted PDFs with an empty password, since owner-password-only PDFs open that way.
- **Process isolation.** Tika server parses in forked JVMs. `taskTimeoutMillis` kills a stuck parse, `maxFiles` recycles a fork after N files, and crashes come back as 503 with a reason. RAGFlow recycles workers after `MAX_TASKS_PER_WORKER`.
- **Partial results.** Docling's statuses include PARTIAL_SUCCESS and SKIPPED, and `abort_on_error` controls whether one bad page fails the document. Unstructured's client splits PDFs into concurrent page batches and can keep the pages that succeeded.
- **The .NET-to-Python interface.** docling-serve is the reference: async submit and poll, a bounded worker pool and queue (`ENG_LOC_NUM_WORKERS`, `QUEUE_MAX_SIZE`), a readiness check that waits for models to load, and a per-job timeout. Retry only transient failures (503, timeout, connection refused); deterministic parse errors go straight to Failed.
- **Parser version per document.** No system found stores the parser name and version per document to drive selective re-parsing [gap]. Connapse already has atomic reindex (#563), so this is cheap to add and lets a parser upgrade re-parse only affected documents.
- **Output shape.** Element-aware chunking that keeps tables whole and splits on titles beat token chunking on financial QA [primary, arxiv 2402.05131]. HTML tables were best for LLM table understanding [primary, arxiv 2305.13062]. Page numbers matter mainly for citations; no retrieval gain was found for bounding boxes [thin].
- **Bombs and quarantine.** No RAG system found has explicit zip-bomb or PDF-bomb defences or a formal poison-document quarantine [thin]. In practice the defence is isolation plus a memory limit, a timeout and a page cap.

## Recommendation
Do it in this order. Each step is measurable on extract-v1, which already exists.

1. **Resilience first** (no new parsers):
   - Re-run extract-v1 to get a post-#562 baseline.
   - Add an explicit Unsupported/Skipped outcome. Connector syncs check the parser registry and skip images and other unsupported types with a count, instead of creating Failed documents.
   - Add magic-byte sniffing and encoding detection: strict UTF-8, then BOM, then UTF.Unknown, then Latin-1.
   - Wrap every parse in a linked-token deadline, with size, page, decompressed-bytes and output-character caps.
   - Retry encrypted PDFs with an empty password, then fail with an "encrypted" reason.
   - Treat zero or garbled text as Failed or Partial, never Ready.
   - Store `parser` and `parserVersion` per document.
2. **.NET fast path:**
   - Replace the PdfPig fork with official PdfPig, using layout analysis, decoration removal and Tabula tables.
   - Fix the DOCX/PPTX defects.
   - Add HTML (SmartReader + ReverseMarkdown), EML/MSG (MimeKit/MsgReader) and EPUB.
   - Add RapidOcrNet for pages the text-quality check flags.
   - Every parser emits Markdown with headings, tables and page markers, so the existing markdown-aware chunker carries structure.
3. **Optional heavy engine:**
   - Define an `IExtractionEngine` HTTP contract (async submit/poll, caps, health, version).
   - Run a bake-off on extract-v1 between docling-serve, the MinerU pipeline and PP-StructureV3 on CPU, scoring accuracy and seconds per page.
   - Ship the winner as an opt-in Docker Compose profile, routed only for PDFs the fast path scores poorly, and for legacy Office and ODT.
   - Docling is the default candidate because of licensing and serving, but its measured accuracy has to earn it.
   - A GPU profile with PaddleOCR-VL or olmOCR 2 is a later option.

What stays unchanged: CSV, JSON, XML and YAML keep going through TextParser (with the encoding fix). XLSX stays unsupported until the structured-query datatype exists. Images inside documents are dropped.

## Conflicts and uncertainties
- **Docling's accuracy.** The only olmOCR-bench number (50.3) comes from Datalab, a competitor. There is no Allen AI or independent run, and no per-category data. Docling's own strengths (tables at 93.6 TEDS; best of eight in one small independent test) measure something different from olmOCR's pass/fail facts. Only a bake-off on Connapse's eval resolves this.
- **Marker's score** is reported as 76.0 (Datalab), 76.1 (Allen AI), 83.2 (Nanonets leaderboard) and 83.3 (the Surya claim).
- **Speed on the same GPU.** Docling's paper reports MinerU faster (0.21 vs 0.49 s/page on an L4). One independent test reports Docling slower than MinerU (2.0 vs 1.0 s/page). These are consistent, but small-sample.
- **OmniDocBench numbers above 90** for PaddleOCR-VL 1.6, MinerU 2.5-Pro, GLM-OCR and DeepSeek-OCR 2 are vendor-run, and the v1.5 and v1.6 scores are not comparable.
- **Single-source or vendor claims:** PdfPig layout quality (no evidence either way), PDFium's multi-column gain (Kreuzberg, vendor), Tesseract vs PaddleOCR speed (tertiary), garbled-page thresholds (tertiary), and Docling's memory footprint (tertiary).
- **Carried over from the prior report:** the 19% silent-failure figure predates #562. It may now be lower.

## Gaps — what we did not find
- No independent per-category olmOCR-bench scores for Docling, PP-StructureV3 or the MinerU pipeline on CPU.
- No published comparison of PdfPig's layout-analysis pipeline against its raw `page.Text`.
- No clean head-to-head of Tesseract and PaddleOCR (ONNX) on document pages.
- No independent quality numbers for Tika on DOCX, MSG or HTML.
- No maintained .NET ODT or legacy .doc reader.
- No RAG system with explicit zip-bomb or PDF-bomb defences, a formal poison-document quarantine, or a parser-version-driven re-parse.
- Not investigated: Unstructured OSS, MarkItDown's quality, granite-docling as a standalone VLM, and how R2R and Kotaemon route parsers.

## Source quality assessment
- **Licences, versions, APIs and configuration:** almost entirely primary (GitHub repos, NuGet, official docs, Hugging Face model cards). Both NuGet ownership facts were re-checked directly against the NuGet API.
- **Accuracy:** weaker. The Allen AI olmOCR leaderboard is primary and neutral, but it omits Docling, PP-StructureV3 and the MinerU pipeline. Most other cross-parser numbers are vendor-run and are marked so.
- **Resilience patterns:** primary (source code of Open WebUI, Onyx, Dify, AnythingLLM, Tika and Docling).
- **Connapse baseline:** primary (Connapse's own eval run and source code).
- **Prior report:** document-shape-robustness-2026-09-27 is carried forward at its own source quality (mostly primary).

## Sources
**Primary**
- Docling releases https://github.com/docling-project/docling/releases · supported formats https://docling-project.github.io/docling/usage/supported_formats/ · GPU https://docling-project.github.io/docling/usage/gpu/ · chunking https://docling-project.github.io/docling/concepts/chunking/
- docling-serve https://github.com/docling-project/docling-serve · usage https://github.com/docling-project/docling-serve/blob/main/docs/usage.md · configuration https://github.com/docling-project/docling-serve/blob/main/docs/configuration.md · issues #366 #474 #489 #701
- Docling issues #3345 #3819 #4357 #2635 #3222 (github.com/docling-project/docling/issues)
- Docling technical report https://arxiv.org/abs/2501.17887 · heron layout https://arxiv.org/abs/2509.11720
- Model cards: https://huggingface.co/docling-project/docling-layout-heron · https://huggingface.co/docling-project/docling-models · https://huggingface.co/ibm-granite/granite-docling-258M
- Allen AI olmOCR leaderboard https://github.com/allenai/olmocr · olmOCR 2 https://arxiv.org/pdf/2510.19817
- MinerU licence https://github.com/opendatalab/MinerU/blob/master/LICENSE.md · MinerU2.5-Pro https://arxiv.org/html/2604.04771v1
- PaddleOCR https://github.com/PaddlePaddle/PaddleOCR · PaddleOCR-VL-1.5 https://arxiv.org/html/2601.21957v1
- Marker https://github.com/datalab-to/marker · Surya https://github.com/datalab-to/surya · Chandra https://github.com/datalab-to/chandra · MonkeyOCR https://github.com/yuliang-liu/monkeyocr
- VLM OCR failure modes https://arxiv.org/html/2606.29213
- PdfPig https://www.nuget.org/packages/PdfPig/ · fork https://www.nuget.org/packages/UglyToad.PdfPig/ · layout analysis https://github.com/UglyToad/PdfPig/wiki/Document-Layout-Analysis · issue #294
- Tabula https://www.nuget.org/packages/Tabula · https://github.com/BobLd/tabula-sharp
- PDFiumCore https://www.nuget.org/packages/PDFiumCore · Docnet.Core https://www.nuget.org/packages/Docnet.Core/
- RapidOcrNet https://www.nuget.org/packages/RapidOcrNet · Sdcb.PaddleOCR https://www.nuget.org/packages/Sdcb.PaddleOCR/ · TesseractOCR https://www.nuget.org/packages/TesseractOCR/
- Apache Tika https://tika.apache.org/ · tika-docker https://github.com/apache/tika-docker · timeouts https://tika.apache.org/docs/4.0.x/pipes/timeouts.html · tika-eval https://cwiki.apache.org/confluence/display/TIKA/TikaEvalMetrics
- Kreuzberg/Xberg https://www.nuget.org/packages/Kreuzberg · https://www.nuget.org/packages/XbergIo.Xberg · https://github.com/kreuzberg-dev/kreuzberg
- MarkItDown https://github.com/microsoft/markitdown
- SmartReader https://github.com/Strumenta/SmartReader · ReverseMarkdown https://www.nuget.org/packages/ReverseMarkdown · MimeKit https://www.nuget.org/packages/MimeKit · MsgReader https://www.nuget.org/packages/MsgReader · RtfPipe https://www.nuget.org/packages/RtfPipe · VersOne.Epub https://www.nuget.org/packages/VersOne.Epub · Codeuctivity.OpenXmlPowerTools https://www.nuget.org/packages/Codeuctivity.OpenXmlPowerTools · NPOI HWPF https://github.com/nissl-lab/npoi/issues/946 · unoserver https://pypi.org/project/unoserver/
- UTF.Unknown https://www.nuget.org/packages/UTF.Unknown/ · FileSignatures https://www.nuget.org/packages/FileSignatures · Mime-Detective https://www.nuget.org/packages/Mime-Detective
- Open WebUI loaders https://github.com/open-webui/open-webui/blob/main/backend/open_webui/retrieval/loaders/main.py
- Onyx extraction https://github.com/onyx-dot-app/onyx/blob/main/backend/onyx/file_processing/extract_file_text.py
- Dify extractors https://github.com/langgenius/dify/blob/main/api/core/rag/extractor/extract_processor.py
- AnythingLLM PDF https://github.com/Mintplex-Labs/anything-llm/blob/master/collector/processSingleFile/convert/asPDF/index.js
- RAGFlow https://github.com/infiniflow/ragflow/blob/main/docker/.env · API https://github.com/infiniflow/ragflow/blob/main/docs/references/http_api_reference.md
- Unstructured client https://github.com/Unstructured-IO/unstructured-python-client
- OHRBench https://arxiv.org/abs/2412.02592 · element chunking https://arxiv.org/abs/2402.05131 · table formats https://arxiv.org/abs/2305.13062
- Connapse source code and eval run 20260928-020641-967d761-extract-v1-connapse-extract

**Secondary**
- Datalab Marker 2 benchmark via MarkTechPost https://www.marktechpost.com/2026/07/24/datalab-marker-v2-vs-mineru-docling-and-liteparse-benchmark-breakdown/
- Nanonets olmOCR leaderboard https://benchmarking.nanonets.com/benchmarks/olmocr
- ParseBench https://arxiv.org/html/2604.08538v1
- Procycons PDF benchmark https://procycons.com/en/blogs/pdf-data-extraction-benchmark/
- Kreuzberg PDFium article https://dev.to/kreuzberg/document-structure-extraction-with-kreuzberg-44cj
- Bevendorff et al. 2023 https://dl.acm.org/doi/pdf/10.1145/3539618.3591920 · WCXB https://arxiv.org/html/2605.21097 · PP-OCRv5 https://arxiv.org/html/2603.24373v1
- Onyx changelog https://docs.onyx.app/changelog

**Tertiary**
- Independent PDF benchmark https://github.com/hraju115/pdf-extraction-benchmark
- Text-layer quality checks https://theneuralbase.com/pdf-processing/learn/advanced/verifying-extraction-quality/
- Tesseract vs PaddleOCR https://www.koncile.ai/en/ressources/paddleocr-analyse-avantages-alternatives-open-source
- codesota leaderboard https://codesota.com/ocr/benchmark/olmocr-bench · Docling memory write-ups (dosu, parsebridge)
