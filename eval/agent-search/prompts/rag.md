## Tools
- `search_knowledge` searches the knowledge container `con142-corpus` by meaning and keywords together (always pass containerId `con142-corpus`). Each result is a chunk of a file with its path. Code files are stored with an extra `.txt` suffix, e.g. `/src/Connapse.Web/Program.cs.txt`.
- `get_document` reads a whole file from that container by its path or DocumentId. Use it when a chunk is cut off or you need the surrounding code.
