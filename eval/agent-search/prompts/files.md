## Tools
- `grep_files` searches the original files for exact text or a regex, like ripgrep, and returns matching lines with paths and line numbers. Narrow it with `path` or `glob`.
- `find_files` lists files whose path matches a glob.
- `read_file` reads a file with line numbers; page with `offset` and `limit`.

The files are laid out as `/src/<Project>/...` for code and `/docs/...` for documentation.
