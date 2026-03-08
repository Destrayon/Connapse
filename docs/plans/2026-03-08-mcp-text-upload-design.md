# MCP upload_file: Raw Text Content Parameter

**Date:** 2026-03-08
**Status:** Approved

## Problem

The MCP `upload_file` tool requires Base64-encoded content for all files. For text-based files (Markdown, JSON, CSV, etc.), this adds unnecessary friction in agent workflows — agents must encode plain text to Base64 before uploading.

## Solution

Add an optional `textContent` parameter to `upload_file`, mutually exclusive with the existing `content` (Base64) parameter.

## Parameter Changes

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `containerId` | string | yes | Container ID or name (unchanged) |
| `content` | string | no* | Base64-encoded file content. For binary files (PDF, DOCX, images). Mutually exclusive with textContent. |
| `textContent` | string | no* | Raw text content for text-based files (Markdown, TXT, CSV, JSON, etc.). Mutually exclusive with content. |
| `fileName` | string | yes | Original file name with extension (unchanged) |
| `path` | string | no | Destination folder path (unchanged) |
| `strategy` | string | no | Chunking strategy (unchanged) |

*Exactly one of `content` or `textContent` must be provided.

## Validation

- Both provided: error `"Provide either 'content' or 'textContent', not both."`
- Neither provided: error `"Provide either 'content' (base64) or 'textContent' (raw text)."`
- `textContent` provided: encode to UTF-8 bytes, wrap in MemoryStream
- `content` provided: Base64 decode (existing behavior)

## Scope

Only [McpTools.cs](../../src/Connapse.Web/Mcp/McpTools.cs) `UploadFile` method changes. No restrictions on file extension when using `textContent`. All downstream processing (storage, ingestion, chunking, embedding) is unchanged.
