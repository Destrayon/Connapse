# How should tables inside documents be extracted, gated, and marked in their passages?
**Date:** 2026-11-11
**Status:** Reviewed
**Built on:** tables-container-2026-11-02.md

## Executive summary
For CON-312, tables found inside PDF, Word, PowerPoint, HTML, Markdown and Excel files should become queryable tables only when they pass a structural gate, and their passages should keep the table text for retrieval while showing the reader a short note that points to the table. No study compares "replace the table with a note" against "keep the table and add a note" directly. The closest evidence (T²-RAGBench) shows that replacing raw content with a summary halves numeric answer accuracy, while retrieving on the summary and answering from the full content does not hurt. Production parsers (Docling, Unstructured) drop single-cell and single-row "tables" as layout noise, which sets the gate. The main uncertainty is how much an agent benefits from the note versus simply reading the inline table; the CON-312 eval measures that.

## Research brief
**Question:** How do mature document pipelines find tables in mixed documents, decide which are real data tables, and represent them in the text passages a RAG system retrieves?

**Sub-questions:**
1. Evidence on table-in-passage representation: inline table, summary or note, or both — effect on retrieval and answer accuracy.
2. How production extractors (Docling, Unstructured, Marker, MinerU, Azure Document Intelligence, AWS Textract, Camelot, Tabula) detect tables per format, and what they reject.
3. How chunkers place a table relative to its surrounding prose, and what placeholder conventions exist.

**Out of scope:** OCR model choice, table-structure recognition models for scanned pages, text-to-SQL accuracy.

**Success criteria:** a recommendation for the CON-312 marker and quality gate, each claim cited.

## Findings by sub-question

### 1. Inline table, note, or both in a RAG passage
No published study compares a passage whose table is replaced by a pointer note against one that keeps the table (gap, high confidence that none exists as of 2026-11).
- T²-RAGBench (arXiv 2506.12071) tested hybrid text-and-table financial documents. Replacing documents with LLM summaries dropped number-match from 37.2 to 18.8; retrieving on summaries but answering from the full content held at 37.4. Hybrid BM25 plus dense was the best retriever. Primary, high confidence. Implication for CON-312: the stored and embedded chunk should keep the table cells.
- TableRAG (arXiv 2410.04739) and "Same Content, Different Representations" (arXiv 2509.22983) find large tables are answered better by SQL or schema-plus-cell retrieval than by putting the whole table in context. Primary, medium confidence (benchmarks are table-only). Implication: a pointer to a queryable table helps on aggregate and lookup questions over big tables.
- TAG (arXiv 2408.14717) argues retrieval plus a query engine beats either alone for questions needing computation. Primary, medium.
- Sui et al. (arXiv 2305.13062) found Markdown and HTML serializations are read comparably well by LLMs, and that structure markers matter more than format. Primary, medium. Connapse's Markdown pipe tables are a reasonable inline form.
- Ericsson's 3GPP study (arXiv 2408.17008) found keeping tables intact within a chunk, with their caption, beat splitting them. Primary, medium.
- TARGET (arXiv 2505.11545) and arXiv 2504.06560 benchmark table retrieval and find header and caption text carry most of the retrievable signal. Primary, medium. Implication: the note should name the columns.

### 2. How extractors find tables and what they reject
Production extractors all treat a table as a separate element, and the open-source ones drop degenerate tables. Sources are the projects' own code and docs (primary).
- **Docling** (docling-project/docling, backend sources): the Word backend skips 1x1 tables as layout boxes; the HTML backend takes headers from `th`/`thead`; the Excel backend finds each table region by flood fill over non-empty cells, so one sheet can hold several tables.
- **Unstructured** (`partition/auto.py`, `partition/xlsx.py`): `infer_table_structure` with the hi_res strategy for PDF; for XLSX, `find_subtable` splits a sheet into tables at empty rows and columns.
- **Marker** (datalab-to/marker) converts Office files to PDF and runs one PDF table pipeline over all of them. **MinerU** (opendatalab/MinerU) is PDF-first in the same way.
- **Azure Document Intelligence layout** (learn.microsoft.com, layout model docs): tables are returned for PDF and images; for XLSX the layout model does no table analysis.
- **AWS Textract** (docs.aws.amazon.com, "How tables work"): labels tables STRUCTURED_TABLE or SEMI_STRUCTURED_TABLE and gives a confidence per table and cell.
- **Camelot** (camelot-dev/camelot) reports per-table accuracy and whitespace percentage for filtering. **tabula-java** (`SpreadsheetExtractionAlgorithm`) accepts a ruled region as a table when its cells fill at least 0.65 of it. **tabula-sharp** (BobLd/tabula-sharp) depends on the official PdfPig package, which may clash with Connapse's PdfPig fork.

Implication for CON-312: Connapse's parsers already emit Markdown pipe tables, so the gate runs on those. Reject tables with one column or one data row, require a consistent column count, and treat the first row as the header. XLSX needs its own reader; split sheets into regions at fully empty rows, as Docling and Unstructured do.

### 3. Where a table sits relative to its prose, and placeholder conventions
No surveyed project writes a short named pointer into the passage text where a table was; they keep the table inline, isolate it as its own chunk, or move it out of band behind an ID. Text placeholders exist only for images.
- **Docling** (docling-core `hierarchical_chunker.py`, `hybrid_chunker.py`, `serializer/markdown.py`): a table is serialized as one chunk, by default with `TripletTableSerializer`; `meta.doc_items` keeps a `#/tables/N` reference outside the embedded text. HybridChunker splits oversized tables by rows with the header repeated, and its merge step can join a small table with nearby text (inferred from code). Markdown export writes pipe tables; images become `<!-- image -->`; there is no table placeholder. Primary, high.
- **Unstructured** (docs, chunking): "A Table element is always isolated and never combined with another element"; oversized tables become `TableChunk`s; the originals stay in `metadata.orig_elements`. Primary, high.
- **LlamaIndex** (`node_parser/relational/base_element.py`): each table yields an `IndexNode` (LLM summary plus column schema) in the table's place and a `TextNode` with the table; `RecursiveRetriever` follows `index_id` to the table. The closest prior art for a pointer, but it is a separate node, not a marker inside the passage. Primary, high.
- **LangChain** semi-structured RAG cookbook: embeds a table summary and returns the raw table from a doc store by `doc_id`; text chunks carry no marker. Secondary (vendor blog), medium.
- **Haystack** `AzureOCRDocumentConverter`: each table becomes its own CSV Document with `preceding_context`, `following_context` and `page`; it is removed from the page text with no placeholder. Primary (docs), high.
- **Azure Document Intelligence** Markdown keeps tables inline as HTML with captions; JSON `tables[]` point into `content` by character spans. Primary, high for the Markdown; span detail from memory, medium. **AWS Bedrock Knowledge Bases** keeps table text in chunks and can attach the full table as chunk metadata that replaces the match at retrieval. Primary, medium. **RAGFlow** keeps a table as one atomic HTML chunk. **Marker, MinerU and LlamaParse** keep tables inline and only link images. Primary, medium.

Implication for CON-312: every system that takes the table out of the retrieved text keeps a full copy reachable by ID, and Azure's character-span pointers are the nearest match to recording the table's span inside a chunk. Connapse's design keeps the table text in the stored chunk (as Docling, Azure, Bedrock and RAGFlow do) and records the span, then renders a note over the span on read. A plain-text pointer inside the passage has no precedent, so the eval has to show it helps.

## Conflicts and uncertainties
- LlamaIndex, LangChain and Haystack take the table out of the retrieved text (behind a summary node, a summary embedding, or nothing), while T²-RAGBench shows summaries alone lose numeric accuracy. They are reconciled by keeping the table text in the stored chunk and rendering the note only on read, so retrieval sees the cells and the reader sees the pointer.
- The user leaned toward replacing the table with a note. The evidence supports doing that in what the reader sees, not in what is indexed.
- The 0.65 fill ratio (tabula) and the 1x1 drop (Docling) are single-source thresholds from code, not studied values.

## Gaps — what we did not find
- No study of an agent following a pointer note from a passage to a queryable table. The CON-312 linked-set eval is the first measurement.
- No published threshold for type consistency in a column; the ≥95% figure in CON-312 is a project choice.
- No per-format accuracy for table detection in DOCX or PPTX; benchmarks are PDF.

## Source quality assessment
Most findings rest on primary sources: arXiv papers and the projects' own code and docs. The placement findings for Docling and Unstructured rest on documentation rather than code and are medium confidence. Built on tables-container-2026-11-02.md, which covered the container table design but not passage linking.

## Sources
**Primary — papers:** arXiv 2506.12071 (T²-RAGBench); 2410.04739 (TableRAG); 2509.22983 (Same Content, Different Representations); 2408.14717 (TAG); 2305.13062 (Sui et al., table serialization); 2408.17008 (Ericsson 3GPP RAG); 2505.11545 (TARGET); 2504.06560.

**Primary — code and docs:** github.com/docling-project/docling (msword, html, msexcel backends; HybridChunker); github.com/Unstructured-IO/unstructured (partition/auto.py, partition/xlsx.py, chunking docs); github.com/docling-project/docling-core (chunker and serializer); docs.haystack.deepset.ai AzureOCRDocumentConverter; blog.langchain.com/semi-structured-multi-modal-rag; docs.aws.amazon.com Bedrock kb-chunking-parsing and kb-advanced-parsing; learn.microsoft.com Document Intelligence markdown-elements and AI Search Document Layout skill; github.com/infiniflow/ragflow (chunker/table.go); opendatalab.github.io/MinerU output files; github.com/datalab-to/marker; github.com/opendatalab/MinerU; learn.microsoft.com Azure AI Document Intelligence layout model; docs.aws.amazon.com Textract "How tables work"; github.com/camelot-dev/camelot; github.com/tabulapdf/tabula-java (SpreadsheetExtractionAlgorithm); github.com/BobLd/tabula-sharp; github.com/run-llama/llama_index (MarkdownElementNodeParser).
