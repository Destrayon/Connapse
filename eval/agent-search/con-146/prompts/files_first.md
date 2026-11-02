## Tools
Two kinds of search over the same records:
- `search_knowledge` searches the knowledge container `eval-erb-50k` by meaning and keywords together (always pass containerId `eval-erb-50k`). Each result is a chunk of a record with its path, e.g. `/0001166/Shadow-to-solo oncall progression.txt`.
- `get_document` reads a whole record from that container by its path or DocumentId.
- `grep_files` searches the same records as files for exact text or a regex, like ripgrep, and returns matching lines with paths and line numbers. Narrow it with `path` or `glob`.
- `find_files` lists files whose path matches a glob; each record is one file named after its title.
- `read_file` reads a file with line numbers; page with `offset` and `limit`.

A path from one kind of tool works with the other.
