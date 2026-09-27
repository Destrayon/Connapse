# generated-encodings

- **What it tests:** that text files in common encodings are read correctly. TextParser assumes UTF-8 whenever a file has no byte-order mark.
- **Source:** generated at load time by `GeneratedEncodingsAdapter`. Version `gen-1`.
- **Files:** the same sentence ("Encoding check: café, naïve, Größe and 日本語 must survive.") written as `.txt` and `.md` in each of five encodings:
  - UTF-8 with and without a BOM;
  - UTF-16 LE with and without a BOM;
  - Latin-1, which carries the Latin part only; for these characters Latin-1 and Windows-1252 bytes are identical.

  That makes 10 files.
- **Checks:** one `present` check per file for its sentence, at the parsed-text level and at the single-chunk level. Each encoding is its own category.
- **Expected today:** UTF-8 and the BOM-marked UTF-16 files pass. UTF-16 without a BOM and Latin-1 come out garbled.
