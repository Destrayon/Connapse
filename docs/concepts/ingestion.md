---
title: About ingestion
type: explanation
audience: admin
---

# About ingestion

Ingestion is the background work that turns a file into searchable passages: parse it to text, split the text into chunks, embed each chunk, and store the result. It runs the same way for an uploaded file and for one a source sync found. The formats and limits are listed in [File types](../reference/file-types.md).

## Parsing

A parser extracts a file's text and keeps its structure as Markdown, because headings and tables help both chunking and search:

- **PDF** pages are read by layout. A layout model labels each page's regions (text, titles, tables, figures, running headers and footers) and their reading order; headers, footers and page numbers are dropped, and tables become Markdown tables. A page with no text layer, or one too garbled to read, is recognised with OCR on the CPU, up to 50 pages per document by default.
- **Word and PowerPoint** files keep their paragraphs, tables and slides.
- **HTML** pages keep their main content and drop navigation.
- **Email** keeps its subject, sender, recipients and date, and its attachments are parsed as files of their own.
- **EPUB** books keep their chapters in reading order.
- **Plain-text** formats, such as Markdown, logs, CSV, JSON and YAML, are read as they are.

Parsers run in separate worker processes, not in the web server, so a malformed file can't take the server down. Each worker has a memory cap and a deadline (300 seconds by default), is replaced after 50 files, and on Linux is sandboxed with no network access and no reach into Connapse's data. A file that runs past the deadline, runs out of memory or crashes its worker fails with `parse_timeout`, `parse_out_of_memory` or `parse_crashed`.

## Chunking

The chunker splits the text into passages small enough to embed and to show as a search result: up to 512 tokens by default, with 50 tokens of overlap between neighbours. By default passages are prefixed with the document's title, and Markdown passages with the path of headings above them, so a passage found on its own still says what it belongs to.

The strategy depends on the file:

| Strategy | Used for |
|----------|----------|
| **Document-aware** | Markdown always, and PDF, Office, HTML, email and EPUB files unless the upload picked another strategy. Splits along headings. |
| **Semantic** | Everything else, by default. Splits where the topic changes, judged by how similar neighbouring sentences are. |
| **Recursive** | Splits on paragraphs, then lines, then sentences, then words. |
| **Fixed size** | Splits every 512 tokens. |
| **Sentence window** | Indexes single sentences and returns the sentences around each one. |
| **Record** | GitHub issues and pull requests, chosen by the source rather than configured. |

## Embedding and storage

The embedding model turns each passage into a vector, and Connapse stores the passages, vectors and word index in PostgreSQL. Each vector records the model that made it, so a model change leaves the old vectors searchable by keyword until they're embedded again; see [Change the embedding model](../guides/change-embedding-model.md).

Four documents are ingested at a time by default. A document's status moves from `Pending` through `Processing` to `Ready`, or to `Failed` with a reason you can see on its row.

## Reindexing

A reindex runs ingestion again on documents already stored. By default it skips any document whose content and settings haven't changed, and reprocesses the ones whose embedding or chunking settings have. A forced reindex reprocesses everything.

## Related

- [File types](../reference/file-types.md)
- [How Connapse works](how-connapse-works.md)
- [Configuration reference](../reference/configuration.md)
