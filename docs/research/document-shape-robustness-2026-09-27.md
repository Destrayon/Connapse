# Which document shapes must Connapse ingest and retrieve well, and how should evals for them be built?
**Date:** 2026-09-27
**Status:** Reviewed
**Built on:** connapse-eval-project-design-2026-09-25.md (harness design, statistics, olmOCR-bench and ViDoRe shortlist), next-connectors-rag-shape-graphrag-2026-09-09.md (per-shape chunking evidence), github-docs-issues-chunking-2026-09-23.md, retrieval-eval-github-sources-2026-09-24.md (chunking tweaks moved MRR 1–3 points on already-clean markdown). Connapse MCP was not connected, so the pre-check used these local reports.

## Executive summary
The document shapes that matter most to RAG users are tables inside PDFs, scanned PDFs, spreadsheets, very long documents, multi-column layouts and slides. Connapse today cannot ingest several of them at all: it has no OCR, no XLSX, HTML or email parser. It also ingests others with known defects: PDF text comes out in raw content-stream order, DOCX tables are duplicated, and the default Semantic chunker flattens newlines. The eval harness cannot see any of this, because it writes every dataset document to a `.txt` file and so only ever exercises the plain-text parser. Failures are also partly invisible in the product: a document whose pipeline failed is marked Ready, and parser warnings are never stored. The recommended eval design scores three layers per shape: whether the evidence survived extraction, whether one chunk holds it, and whether search ranks it. It combines about six public raw-file benchmarks with a self-built corpus that renders the same content into every format. The largest uncertainty is effect size: most per-shape impact numbers come from single papers. And the "same content in every format" (format invariance) test has no published precedent.

## Research brief
**Question:** Which document formats and internal shapes will people who use a RAG bring to Connapse, how does each break ingestion and retrieval, and what evals would measure Connapse's robustness to them before the indexer is fixed?

**Sub-questions:**
1. Which formats and shapes do RAG users bring, and how common is each?
2. How does each shape fail at extraction, chunking, indexing and retrieval, and by how much?
3. Which public benchmarks provide raw files with retrieval labels?
4. How should ingestion be evaluated as its own layer, and how should self-built test sets be made?
5. What does Connapse's indexer and eval harness do today?

**Out of scope:** choosing parsers, OCR engines or .NET libraries (that is the fix, not the eval); answer generation; connector choice; embedding and reranker choice (settled 2026-09-24).

**Success criteria:** a ranked shape list, a per-shape failure map, a dataset portfolio, a metric per pipeline stage, and a gap list against Connapse's current code, ready to brainstorm an eval plan from.

## Findings by sub-question

### 1. Which document shapes RAG users bring to Connapse-like products
PDF is the dominant format, and the hardest document shapes users bring to a RAG system live inside PDFs. Spreadsheets are the second pain point.
- **Format mix.** Govdocs1 (about 1M US government web files, 2009-era) is 23.3% PDF, 19.1% HTML, 8.4% TXT, 8.1% DOC/DOCX, 6.7% XLS, 5.0% PPT and 1.8% CSV [primary, dated]. No current enterprise survey gives a per-format breakdown [gap].
- **Scans.** FinePDFs sent about 29% of 1.29B Common Crawl PDFs to its OCR path. A classifier made that choice, so 29% is an upper bound on true scans [primary].
- **Demand signal from issue trackers.** Keyword hits across the Open WebUI, AnythingLLM, RAGFlow and Dify issue trackers: ocr 637, "image pdf" 479, csv 400, excel 343, xlsx 255, pptx 166, "table pdf" 137, eml 28, handwritten 0 [primary, rough: a mention is not necessarily a complaint].
- **Product signals.** OpenAI file search dropped CSV/XLSX, Glean truncates spreadsheets at about 250k characters, and RAGFlow ships separate chunking templates for tables, slides, laws, manuals, papers, books, Q&A and résumés [primary].
- **Ranked by prevalence × difficulty:**
  1. Tables in PDFs, including merged cells and page-spanning tables.
  2. Scanned or image-only PDFs.
  3. Spreadsheets.
  4. Very long documents; a 10-K averages 123k words.
  5. Multi-column layouts.
  6. Slides.
  7. Near-duplicates and versions; about 33% of enterprise data, from one dated vendor survey.
  8. Charts whose information exists only as pixels.
  9. Page headers, footers and footnotes: 11.6% of DocLayNet layout elements.
  10. Email threads with attachments.
- **By vertical:** finance means long table-heavy filings; legal means numbered clauses and cross-references; engineering means deep manual hierarchies; research means multi-column pages with formulas; personal knowledge means small, heavily linked markdown.

### 2. How each document shape breaks RAG, with measured impact
Four failures carry strong measurements, and they matter more than any chunk-boundary tweak: OCR noise, numeric tables, chunks losing their document identity, and version conflation.

| Shape | Stage | Mechanism | Measured impact |
|---|---|---|---|
| Scanned PDF | Extraction | OCR character and structure errors | Best OCR still loses at least 14% end-to-end. Heavy noise costs about 50% of retrieval (OHRBench) [primary] |
| Scanned PDF | Extraction | Low OCR error does not guarantee good RAG; struck-out text is read as current | Ultra-long pages: 42.6% RAG accuracy (InduOCRBench) [primary] |
| Native PDF | Extraction | Reading order and structure lost | Losing structure hurts more than erasing the same area of content (ProSA) [primary] |
| Native PDF | Chunking | Splitting that ignores the heading hierarchy | 33-point gap on table-dependent questions. Hierarchy and metadata mattered more than which PDF converter was used [primary, single source] |
| Numeric tables | Retrieval | Numbers carry little meaning for embeddings; many near-identical reports | Hybrid R@3 49.4%. Number-match 40.9% vs 72.7% with the right context (T²-RAGBench) [primary] |
| Page-spanning tables | Chunking | Header row separated from data rows | 0.89 vs 0.78 for vision-guided vs fixed chunking [primary, single source] |
| Forms | Extraction | Keys separated from their values | Misalignment is the dominant error (FUNSD) [primary; number unverified] |
| Slides | Extraction | Sparse text whose meaning depends on layout | Text retrievers nDCG@10 47–59 vs 75.3 for screenshot embeddings (SlideVQA) [primary] |
| Charts | Extraction | Information exists only as pixels | Text pipelines 65–67 vs 81.3 nDCG@5 for ColPali (ViDoRe) [primary] |
| Spreadsheets | Serialization | Cells lose their headers; sheets flattened; formulas stored instead of values | Table detection 25.6% worse with naive encoding (SpreadsheetLLM) [primary]. Markdown vs CSV 60.7% vs 44.3% [secondary, blog] |
| DOCX | Extraction | Text boxes, headers and footers dropped; tracked deletions leak in | Mechanism only; fixed in Haystack, RAGFlow and Langflow PRs [primary] |
| HTML | Extraction | Boilerplate floods the index; structure lost | Cleaning removes about 94% of tokens; structure beats plain text on 6 datasets (HtmlRAG) [primary] |
| Email | Retrieval | Quoted replies duplicate content; names and IDs need keyword matching | BM25 R@5 87.5% vs dense 59.3% (EnronQA) [primary] |
| Long documents | Chunking | A chunk loses which company and period it describes | FinanceBench: shared store 19% correct vs per-document store 50% [primary]. Contextual chunk headers cut failures 35%, and 67% with reranking (Anthropic) [primary, vendor] |
| Versions | Retrieval | Old and new versions conflated | 58–64% vs 90% for version-aware retrieval (VersionRAG, n=100) [primary, small] |
| Legal | Chunking/rerank | Clauses split; "see Section 4.2" cross-references unresolved | Precision@1 2–14%; a general reranker hurt (LegalBench-RAG) [primary] |
| CJK | Keyword index | Whitespace tokenization makes a sentence one token | Keyword search returns nothing [mechanism; the >10-point figure is snippet-only] |
| Encoding, hyphenation, ligatures, tiny docs | Extraction/index | Garbled characters; split words miss keyword matches | No RAG measurement found [gap] |

Cross-cutting failure modes, in order of evidence strength:
1. Chunks lose their document identity.
2. Silent empty or partial extraction.
3. Keyword tokenization breaks on numbers, codes and CJK.
4. Flattening destroys structure.
5. Near-duplicates collapse the result set as the corpus grows.
6. Extraction scores do not predict retrieval quality (InduOCRBench), so per-shape retrieval must be the headline metric.

### 3. Public benchmarks with raw files and retrieval labels
Six public datasets together cover native PDF, tables, charts, long documents, multilingual queries, HTML, email and a DOCX/PPTX smoke test. None covers raw XLSX, real DOCX, native PPTX with speaker notes, or EML with attachments.

| Dataset | Shapes | Raw files | Labels | License | Fit |
|---|---|---|---|---|---|
| ViDoRe v3 (2–3 public subsets) | Enterprise PDFs, tables, charts, 6 query languages | PDFs | Graded page labels + bounding boxes | CC BY 4.0 labels; documents keep per-publisher licenses | High |
| OHRBench | 7 domains; evidence tagged text/table/formula/chart/reading order | pdfs.zip | Evidence page | CC BY 4.0 | High |
| UDA (finance + Wikipedia) | PDF and HTML, tables | PDF, HTML | Evidence (granularity unverified) | CC BY-SA 4.0 | High |
| T²-RAGBench (sampled) | Tables in financial PDFs | PDFs | One gold document; questions rewritten to stand alone | CC BY 4.0 | High for tables |
| MMLongBench-Doc or FinanceBench | Long documents | PDFs | Evidence pages (FinanceBench also evidence text) | Contradictory: "research only" / CC BY-NC vs MIT | Med-High, legal review first |
| EnronQA (1–2 inboxes) | Email | Full text with headers, writable as .eml | One email per question | Unverified | High for email |
| RAG-Multi-Corpus | PDF, DOCX, PPTX, HTML, MD | Yes | Citations (granularity unverified) | MIT | Smoke test only: synthetic, not peer-reviewed |

- **Parse-only checks (no retrieval labels):** olmOCR-bench (scans, tables, math; ODC-BY) and ParseBench (Apache-2.0).
- **Rejected on license or format:** DocVQA, SlideVQA, RD-TableBench, MMDocIR, CRAG. Their licenses are research-only or no-derivatives, or they ship page images only.
- **Converting QA to retrieval labels:** evidence pages become page-level relevance labels; single gold documents become document labels; LegalBench-RAG's character spans allow chunk-level scoring.

### 4. How to evaluate ingestion separately from retrieval
Ingestion should be scored in three layers per query, so a retrieval miss can be attributed to extraction, chunking or ranking.
- **Extraction: binary "fact" tests, used as the CI gate.** These come from olmOCR-bench and ParseBench [primary]. Each fact checks one of:
  - a snippet that must be present;
  - a snippet that must be absent (headers, footers, page numbers);
  - order: A precedes B;
  - a table cell together with its neighbouring cells.

  Matching is fuzzy after Unicode normalization. These tests are deterministic, cheap and immune to formatting drift.
- **Extraction: continuous diagnostics.**
  - Token recall and spurious-token rate (Unstructured SCORE-Bench) [primary, vendor].
  - tika-eval's out-of-vocabulary rate, which needs no ground truth and catches garbled encoding [primary].
  - TEDS and GriTS for tables correlate only r≈0.7 with human judgment [single study]. Cell-neighbour facts are the better CI check.
- **Chunking.** Chroma's token-level recall, precision and IoU against evidence spans [primary]. Plus an "answer-in-single-chunk" rate: the share of evidence spans that one chunk holds whole. That second metric is our own and unpublished.
- **Linking to retrieval.** Label each query with its evidence text, document and page. A retrieved chunk counts as a hit if it contains the evidence, matched by longest common subsequence as OHRBench does. Existing MRR and nDCG then split into three causes: evidence lost at extraction, evidence split at chunking, or evidence ranked too low.
- **Perturbation curves.** Inject errors at known rates into clean renders, the OHRBench design (formatting noise at r = 0.1, 0.3, 0.6; scan degradation at three levels), and plot recall against rate.
- **Format invariance.** Render one canonical source to md, html, docx, pdf, pptx, xlsx, eml and a scanned pdf, and ingest each into its own container. Per-query recall should be equal across formats. Report the gap with the harness's paired permutation test and Holm correction. No published study does this [gap; the design is ours].
- **Self-built corpus recipe.**
  1. Author canonical sources in Markdown or HTML: headings, merged-cell tables, footnotes, multi-column sections and code.
  2. Render them with Pandoc, LibreOffice headless, Playwright print-to-PDF, LaTeX and Python's `email` module.
  3. Degrade scans with Augraphy (MIT).
  4. Generate facts and evidence spans from the source automatically.
  5. Confirm a clean round-trip before perturbing, so renderer bugs are not scored as parser bugs.

  Synthetic renders are "too clean", so keep a small real subset, e.g. from govdocs1, with hand-written facts, and report the two subsets separately.
- **Negative and robustness corpora:**

  | Corpus | License |
  |---|---|
  | govdocs1 | "believed freely redistributable" |
  | SafeDocs / UNSAFE-DOCS (malformed PDFs) | Per-file copyright unclear |
  | Apache Tika regression corpus | Unclear |

  Also generate our own negatives: encrypted, image-only, truncated, wrong extension, mixed encoding, zip bombs, huge files. The pass rule is an explicit error status, never a Ready document with empty or garbled text. The headline robustness number is the **silent-failure rate**: documents reported as ingested whose fact recall is below a threshold.

### 5. What Connapse's indexer and eval harness do today
Connapse's indexer handles only three parser families. Its eval harness never exercises any of them except plain text. Verified against the code on 2026-09-27; paths are relative to the repository root.
- **File-type detection is by extension only.** `IngestionPipeline.cs:686` selects the parser, and `FileTypeValidator.cs:12` holds the allow-list.
- **Plain text.** TextParser covers txt, md, csv, log, json, xml and yaml. It assumes UTF-8 when there is no BOM, so Latin-1 and UTF-16 text is garbled silently. JSON and XML are indexed as raw markup.
- **PDF.** PdfParser uses PdfPig `page.Text`: raw content-stream order, no multi-column handling, no OCR. Encrypted or corrupt PDFs return empty text plus a warning.
- **Office.** OfficeParser handles DOCX and PPTX only.
  - DOCX reads the body only, dropping headers, footers, footnotes, comments and text boxes.
  - DOCX tables are emitted twice: once inline and once as pipe-joined rows appended at the end of the document (`OfficeParser.cs:93-121`, confirmed).
  - PPTX puts each text run on its own line and drops speaker notes.
- **Not parsed at all:** xlsx, doc, html, eml, msg, rtf, zip, images and source code. Uploads of these are rejected, but the UI file picker offers `.html`. Connector sources have no extension filter, so these files become Failed documents.
- **Chunking.**
  - Uploads default to Semantic, and the configured strategy is not consulted on the API upload path.
  - Semantic re-joins sentences with spaces, which flattens tables, CSV and code (`SemanticChunker.cs:159`).
  - DocumentAware, with a heading breadcrumb, applies only to markdown.
  - No chunker adds the document title.
  - Token counting always uses cl100k_base, whatever the embedding model.
- **Metadata.** No per-chunk page or slide number is stored; page markers exist only as inline text. Search hits do not return offsets or heading paths.
- **Failure visibility.**
  - Only zero-chunk documents are marked Failed.
  - Parser warnings are never persisted.
  - `IngestionJobs.IngestAsync` ignores the pipeline result and sets the ingestion state to indexed regardless, so a Failed document shows as Ready (`IngestionJobs.cs:59,84-88`, confirmed).
  - The harness's failed-document count relies on that same state, so it would miss these failures.
- **Eval harness.** Suite v1 is 16 pre-extracted text datasets. Each document is written as `NNNNNNN.txt` (`ConnapseSearchSystem.cs:89-95`), so only TextParser + Semantic is exercised. The harness design doc defers parser evals to sub-project 7.
- **Tests.** No binary fixtures, and no PdfParser tests. OfficeParser tests build tiny documents in code, with no tables, headers or speaker notes.

## Conflicts and uncertainties
- **Extraction checks vs retrieval as the gate.** The methodology research recommends binary extraction facts as the CI gate. The failure-mode research (InduOCRBench) finds extraction quality does not predict RAG quality. Reconciled here: facts are the cheap CI gate and the diagnostic, and per-shape retrieval is the headline. Neither alone is sufficient.
- **How much structure matters.** The 2026-09-24 eval measured only 1–3 MRR points from content handling on GitHub markdown. This run finds 33-point hierarchy gaps and roughly 30-point table gaps. Both hold: gains are small when the baseline preserves structure (clean markdown) and large when it destroys structure. Connapse's Semantic chunker and PdfPig ordering destroy structure, so large gains are plausible here. This is an inference, not a measurement on Connapse.
- **Licenses.**
  - FinanceBench: CC BY-NC on Hugging Face vs MIT-style on GitHub.
  - MMLongBench-Doc: Apache-2.0 alongside "research use only".
  - EnronQA and the Tika corpus: license unverified.
  - ViDoRe v3: per-document publisher licenses.
- **Single-source numbers:** hierarchy-aware chunking (33 points), vision-guided table chunking, VersionRAG (n=100), FUNSD form figures, TEDS/GriTS vs human correlation, and the OOXML hidden-content fork counts (21 constructs, all 13 tools affected).
- **Unverified figures.** The CJK >10-point figure and the CSV-vs-Markdown numbers rest on a snippet and a blog.
- **Scan share.** The 29% scan share is an upper bound set by a classifier.

## Gaps — what we did not find
- No current per-format prevalence survey of enterprise or self-hosted RAG corpora; Reddit threads could not be retrieved.
- No public retrieval benchmark with raw XLSX, real DOCX (tracked changes, comments, text boxes), native PPTX with speaker notes, or EML/MSG with attachments.
- No real-scan or handwriting benchmark with retrieval labels beyond OHRBench's partial coverage.
- No published format-invariance study (same content, different formats).
- No RAG measurement for encoding errors, hyphenation and ligatures, header/footer pollution, very short documents or legal cross-references.
- No healthcare or HR prevalence evidence.

## Source quality assessment
- **Failure-mode and benchmark claims:** mostly primary. Papers with numbers (OHRBench, T²-RAGBench, FinanceBench, EnronQA, HtmlRAG, SpreadsheetLLM, ViDoRe/ColPali, LegalBench-RAG) and official dataset cards.
- **Prevalence claims:** weaker. The best source (Govdocs1) is dated government web data; issue-mention counts are rough signals; the redundancy figure is a 2016 vendor survey.
- **Vendor-primary sources** (Anthropic, Unstructured, Reducto) are flagged where used.
- **The Connapse audit** is primary (source code); two load-bearing claims were confirmed by reading the lines directly.
- **Prior corpus reports** are carried forward at their own source quality: mostly primary, with the 2026-09-24 eval limited by model-written questions.

## Sources
**Primary**
- OHRBench https://arxiv.org/html/2412.02592 · dataset https://huggingface.co/datasets/opendatalab/OHR-Bench
- InduOCRBench https://arxiv.org/abs/2605.00911
- ProSA https://arxiv.org/abs/2605.19309
- PDF parser comparison https://arxiv.org/html/2410.09871
- Hierarchy-aware splitting https://arxiv.org/abs/2604.04948
- T²-RAGBench https://arxiv.org/html/2506.12071 · https://huggingface.co/datasets/G4KMU/t2-ragbench
- Vision-guided chunking https://arxiv.org/html/2506.16035v2
- FUNSD serialization https://arxiv.org/abs/2609.17538
- DSE / SlideVQA https://arxiv.org/html/2406.11251
- ColPali / ViDoRe https://arxiv.org/html/2407.01449
- SpreadsheetLLM https://arxiv.org/abs/2407.09025
- HtmlRAG https://arxiv.org/abs/2411.02959
- EnronQA https://arxiv.org/html/2505.00263 · https://huggingface.co/datasets/MichaelR207/enron_qa_0922
- cAST https://arxiv.org/abs/2506.15655
- FinanceBench https://arxiv.org/html/2311.11944 · https://huggingface.co/datasets/PatronusAI/financebench
- Anthropic Contextual Retrieval https://www.anthropic.com/news/contextual-retrieval
- Chunk-size study https://arxiv.org/abs/2505.21700
- VersionRAG https://arxiv.org/abs/2510.08109
- LegalBench-RAG https://arxiv.org/html/2408.10343 · https://github.com/zeroentropy-ai/legalbenchrag
- Haystack DOCX fix https://github.com/deepset-ai/haystack/pull/12769
- RAGFlow DOCX fix https://github.com/infiniflow/ragflow/pull/20221
- ViDoRe v3 https://huggingface.co/datasets/vidore/vidore_v3_hr
- UDA https://huggingface.co/datasets/qinchuanhui/UDA-QA
- MMLongBench-Doc https://github.com/mayubo2333/MMLongBench-Doc
- RAG-Multi-Corpus https://github.com/udayallu/RAG-Multi-Corpus
- EnterpriseRAG-Bench https://github.com/onyx-dot-app/EnterpriseRAG-Bench
- olmOCR-bench https://github.com/allenai/olmocr/blob/main/olmocr/bench/README.md
- ParseBench https://arxiv.org/html/2604.08538v1
- OmniDocBench https://github.com/opendatalab/OmniDocBench
- Chroma chunking evaluation https://www.trychroma.com/research/evaluating-chunking
- tika-eval https://cwiki.apache.org/confluence/display/tika/TikaEvalMetrics
- TEDS/GriTS vs LLM judge https://arxiv.org/abs/2603.18652
- OOXML hidden-content forks https://arxiv.org/abs/2608.25880
- Augraphy https://github.com/sparkfish/augraphy
- govdocs1 https://digitalcorpora.org/corpora/file-corpora/files/
- SafeDocs https://digitalcorpora.org/corpora/file-corpora/cc-main-2021-31-pdf-untruncated/
- Govdocs1 type counts https://www.researchgate.net/figure/Govdocs1-File-Types-and-File-Counts_tbl1_357990825
- FinePDFs https://huggingfacefw-finepdfsblog.hf.space/
- DocLayNet https://arxiv.org/html/2206.01062
- OpenAI file search https://developers.openai.com/api/docs/guides/tools-file-search
- Glean limits https://docs.glean.com/connectors/crawler-and-indexing-limits
- Bedrock KB https://docs.aws.amazon.com/bedrock/latest/userguide/knowledge-base-ds.html
- Vertex https://docs.cloud.google.com/generative-ai-app-builder/docs/prepare-data
- Azure blob indexing https://learn.microsoft.com/en-us/azure/search/search-howto-indexing-azure-blob-storage
- Docling formats https://docling-project.github.io/docling/usage/supported_formats/
- Unstructured types https://docs.unstructured.io/open-source/introduction/supported-file-types
- Open WebUI loaders https://raw.githubusercontent.com/open-webui/open-webui/main/backend/open_webui/retrieval/loaders/main.py
- Marker https://github.com/datalab-to/marker
- 10-K length https://arxiv.org/html/2401.06915
- Connapse source code (this worktree)

**Secondary**
- Unstructured SCORE-Bench https://unstructured.io/blog/introducing-score-bench-an-open-benchmark-for-document-parsing
- RAGFlow templates https://ragflow.io/docs/dataset_configuration
- OpenAI community thread https://community.openai.com/t/what-file-types-are-actually-supported/929529
- Tika regression corpus https://openpreservation.org/blogs/apache-tikas-regression-corpus-tika-1302/
- "Too clean" synthetic docs https://arxiv.org/html/2603.23885v2
- Hybrid search https://denser.ai/blog/hybrid-search-for-rag/
- Veritas Databerg 2016 (vendor)

**Tertiary**
- CSV vs Markdown blog https://www.anup.io/data-in-csv-format-isnt-always-the-best-for-llms/
- qmd CJK issue https://github.com/tobi/qmd/issues/617
- Reducto marketing https://reducto.ai/
