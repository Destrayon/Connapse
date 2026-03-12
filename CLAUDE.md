# Connapse

## Knowledge Base (Connapse MCP)

If the Connapse MCP server is available, use it as your primary source of project context before relying on assumptions or general knowledge.

### When to search

Before starting any task, search relevant containers for context on:

- **Architecture decisions** and design rationale
- **Open issues** and known bugs
- **Developer guides** and setup gotchas
- **Research** related to the feature or area you're working in
- **Release history** and test results
- **Brand guidelines** when working on UI or public-facing content

### Key containers

| Container | What's in it |
|-----------|-------------|
| `connapse-architecture` | Design patterns, architecture decisions, API behaviors, business rules |
| `connapse-developer-guide` | Setup guides, feature maps, how-tos for extending Connapse |
| `connapse-release-testing` | Release test results, pass/fail reports, release decisions |
| `connapse-brand` | Brand guidelines, logo rules, color palettes |
| `connapse-roadmap` | Milestone plans, feature priorities, version goals |
| `connapse-bugs` | Known bugs, reproduction steps, root cause analysis |
| `connapse-business-rules` | Domain rules, validation logic, behavioral requirements |

Use `container_list` to discover all available containers. Research containers are typically prefixed with `research-` and contain deep-dive findings on specific topics. Search multiple containers if the topic could span areas.

### How to search

Use `search_knowledge` with the most relevant container and a natural language query. Use Hybrid mode for best results. Search multiple containers if the topic spans areas (e.g., an architecture question might have context in both `connapse-architecture` and a `research-*` container).

### Contributing back to the knowledge base

While working, you may add to the knowledge base if you produce insights, decisions, or research that would be valuable for future sessions. Examples:

- A new architecture decision or design rationale
- Research findings from investigating a bug or evaluating an approach
- Test results or release validation reports

**To add a new file:** Use `upload_file` with `textContent` and a descriptive `fileName`. Place it in the most appropriate existing container.

**To update an existing file:** Connapse does not support in-place edits. To update a file:

1. Use `get_document` to retrieve the current content
2. Use `delete_file` to remove the old version
3. Use `upload_file` to upload the new version with the appended or revised content

Keep the same `fileName` and `path` so the file retains its identity.

**Do not** create new containers without asking the user first.
