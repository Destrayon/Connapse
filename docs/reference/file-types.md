---
title: File types
type: reference
audience: admin
---

# File types

Connapse indexes the file types below, whether they are uploaded or found by a source sync. A file of any other type is refused with `Unsupported file type`. How each format is read is in [About ingestion](../concepts/ingestion.md).

## Formats

| Format | Extensions | What's kept |
|--------|-----------|-------------|
| Plain text | `.txt` `.md` `.markdown` `.csv` `.log` `.json` `.xml` `.yaml` `.yml` | The text as it is. CSV and JSON are read as text, not as tables or records. |
| PDF | `.pdf` | Text in reading order, headings and tables, without running headers, footers and page numbers. Pages with no readable text are recognised with OCR. |
| Word and PowerPoint | `.docx` `.pptx` | Paragraphs, headings, tables and slides |
| HTML | `.html` `.htm` | The main content, without navigation |
| Email | `.eml` `.msg` | Subject, sender, recipients, date and body. Attachments of a supported type are indexed as files of their own. |
| EPUB | `.epub` | Chapters in reading order |

Spreadsheets (`.xlsx`) and older Office formats (`.doc`, `.ppt`) aren't supported.

## Limits

Each limit is an **Upload** setting, changed on the **Settings** page or as `Knowledge__Upload__<Setting>` in [Configuration](configuration.md). A file over a limit fails with the code shown, which appears in its row's error.

| Limit | Setting | Default | Fails with |
|-------|---------|---------|-----------|
| File size | `MaxFileBytes` | 256 MiB | `file_too_large` |
| PDF pages | `MaxPdfPages` | 5,000 | `too_many_pages` |
| Size a DOCX or PPTX expands to | `MaxDecompressedBytes` | 512 MiB | `decompressed_too_large` |
| Compression ratio of a DOCX or PPTX past 10 MiB expanded | `MaxCompressionRatio` | 100 | `decompressed_too_large` |
| Entries in a DOCX or PPTX package | `MaxZipEntries` | 10,000 | `too_many_entries` |
| Characters extracted from one file | `MaxExtractedCharacters` | 20 million | `output_too_large` |
| Parse time per file | `ParseTimeoutSeconds` | 300 | `parse_timeout` |
| Parser memory | `ParserMemoryLimitMb` | 2,048 MB | `parse_out_of_memory` |
| PDF pages recognised with OCR | `MaxOcrPagesPerDocument` | 50 | Not a failure: later pages are skipped with a warning |

## Other failures

| Code | Meaning |
|------|---------|
| `no_text` | The file has no text to index |
| `no_text_layer` | A PDF has no text layer and OCR is turned off (`PdfOcr`) |
| `garbled_text` | A PDF, DOCX or PPTX gave text too unreadable to index |
| `parse_crashed` | The parser's worker process crashed on the file |
| `sandbox_unavailable` | The parser sandbox was required but couldn't start |

## Related

- [About ingestion](../concepts/ingestion.md)
- [Configuration](configuration.md)
- [Upload files](../sources/uploads-and-minio.md)
