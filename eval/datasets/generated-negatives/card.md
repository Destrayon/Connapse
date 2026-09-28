# generated-negatives

- **What it tests:** that files Connapse cannot read fail loudly. Each file must be rejected at upload, or end with Status Failed, a non-empty ErrorMessage, **and** IngestionState Failed (the state the UI badge reads). None may show as Ready with no text, and none may take down the host.
- **Source:** generated at load time by `GeneratedNegativesAdapter` (`tests/Connapse.Eval/Datasets/Generated`), with no downloads and nothing committed. Version `gen-1`; a change to a generator must bump it.
- **Files, each with its category:**
  - `truncated.pdf` (truncated): the first half of the bytes of a two-page PDF the harness generates.
  - `image-only.pdf`, `image-only.docx` (image-only): one striped PNG and no text layer, like a scan.
  - `png-named.pdf`, `text-named.docx`, `pdf-named.docx` (wrong-extension).
  - `zero-byte.pdf` (empty): the upload validator rejects it today.
  - `zip-bomb.docx` (zip-bomb): `word/document.xml` inflates to 200 MB of repeated text from a file under 2 MB. It runs last, because the host is in-process and an out-of-memory crash ends the run.
- **Determinism:** the adapter test checks that two builds of each file are byte-identical. PdfPig writes a random file ID into each PDF's trailer, so the builder replaces it with a hash of the file's content.
- **Why not SafeDocs or other malformed-PDF corpora:** per-file copyright is unclear, and the malicious subsets are unsafe to open on a developer machine. The generated files cover the same failure classes.
