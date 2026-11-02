## Tools
Two kinds of search over the same files:
- `search_knowledge` searches the knowledge container `con142-corpus` by meaning and keywords together (always pass containerId `con142-corpus`). Each result is a chunk of a file with its path. Code files carry an extra `.txt` suffix there, e.g. `/src/Connapse.Web/Program.cs.txt`; drop the suffix to use the path with the file tools.
- `grep_files` searches the original files for exact text or a regex, like ripgrep, with paths and line numbers. `find_files` lists files by glob. `read_file` reads a file with line numbers.
- `get_document` reads a whole file from the container; `read_file` does the same for the original files.

## Which search to use
- Use `search_knowledge` first when the question describes a behaviour, reason, design or concept in its own words and you do not know the exact names to look for ("why", "how does", "what happens when").
- Use `grep_files` first when the question contains an exact name: a class, method, setting key, error code, endpoint path, file name, or a literal string or number.
- Once one search gives you a name or a file, switch freely: grep for a name that semantic search surfaced, or read a file that grep found.
- If one kind of search comes up empty or off-topic after two tries, try the other.
