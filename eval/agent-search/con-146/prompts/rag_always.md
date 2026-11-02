
## Search order
- Always call `search_knowledge` first, before any file tool, on every question. The records rarely use the question's own words, and it matches by meaning.
- Then use the file tools to follow up: grep for a name, ID or phrase that `search_knowledge` surfaced, or read a record it pointed to.
- If `search_knowledge` comes up empty or off-topic after two tries, search the files directly.
