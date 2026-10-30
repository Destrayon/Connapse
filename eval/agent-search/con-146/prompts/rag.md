## Tools
- `search_knowledge` searches the knowledge container `eval-erb-50k` by meaning and keywords together (always pass containerId `eval-erb-50k`). Each result is a chunk of a record with its path, e.g. `/0001166/Shadow-to-solo oncall progression.txt`.
- `get_document` reads a whole record from that container by its path or DocumentId. Use it when a chunk is cut off or you need the rest of the record.
