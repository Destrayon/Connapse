---
title: How Connapse works
type: explanation
audience: user
---

# How Connapse works

Connapse is a self-hosted search service for your organisation's documents, built for AI agents as much as people. It copies text out of your files and the systems you connect, splits it into passages, and indexes each passage two ways: as words, for exact matches, and as a vector (a list of numbers that captures meaning), for matches in other words. A search runs both and merges the answers. Agents reach it over MCP, scripts over REST, and people through the web app.

## Where the content comes from

Content lives in one of two places. A **container** holds files that belong to Connapse: you upload them, browse them as a folder tree, and delete them. A **source** indexes content that stays in another system, such as an S3 bucket, a GitHub repository or a Confluence space, through a **connection** that says how Connapse signs in. Sources are read-only and sync on a schedule. [Containers, connections and sources](../sources/index.md) explains the split.

## What happens to a file

When a file arrives, by upload or by sync, Connapse keeps the original and queues the file for ingestion in the background:

1. A parser turns the file into text, keeping headings, lists and tables. PDFs are read by layout, and scanned pages are recognised with OCR.
2. A chunker splits the text into passages of a few hundred tokens, along headings where the document has them.
3. The embedding model turns each passage into a vector.
4. The passages, their vectors and their word index are stored in PostgreSQL.

The document's status moves from `Pending` through `Processing` to `Ready`, or to `Failed` with a reason. [About ingestion](ingestion.md) covers each step.

## What happens on a search

A search embeds the query, finds the closest passages by vector and the best matches by keyword, and merges the two lists into one ranking. A reranker, if you turn one on, then reads the top passages next to the query and reorders them. Results from a source are checked against what the person searching may see in that source. [About search](search.md) explains the ranking, and [About permissions](permissions.md) the checks.

## Three ways in, one set of rules

The web app, the REST API and the MCP server all call the same operations. Each operation states the role and token scope it needs, so a permission is checked the same way wherever a request comes from. Over MCP, an agent sees a tool only when its key's scopes allow it, and deletes can wait for a person's approval. See [Roles and scopes](../reference/roles-and-scopes.md).

## Where things are stored

| What | Where |
|------|-------|
| Original uploaded files | Managed storage: MinIO, or another S3-compatible service |
| Documents, passages, vectors, word index, settings | PostgreSQL with the pgvector extension |
| Users, tokens, agents, audit log | PostgreSQL, in their own tables |
| Files indexed from a source | Left where they are; Connapse keeps only the extracted text |

Settings changed in the web app are stored in PostgreSQL and apply without a restart. A container can override the search, chunking, embedding, upload and summary settings for itself. The [configuration reference](../reference/configuration.md) lists every setting.

## Related

- [Quickstart](../get-started/quickstart.md)
- [About search](search.md)
- [About ingestion](ingestion.md)
- [About permissions](permissions.md)
