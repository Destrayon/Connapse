# CLI Reference

The Connapse CLI (`connapse`) provides command-line access to the platform. It authenticates via Personal Access Tokens stored in `~/.connapse/credentials.json`.

## Installation

```bash
# Option A: .NET Global Tool (requires .NET 10)
dotnet tool install -g Connapse.CLI

# Option B: Native binary from GitHub Releases (no .NET required)
# https://github.com/Destrayon/Connapse/releases
```

## General Commands

### `version`

Show the installed CLI version.

```bash
connapse version
connapse --version
```

### `update`

Update the CLI to the latest release.

```bash
connapse update            # Download and install the latest version
connapse update --check    # Check for updates without installing
```

## Authentication Commands

### `auth login`

Authenticate with a Connapse server. Opens a browser for PKCE-based login by default.

```bash
connapse auth login [--url <server-url>] [--no-browser]
```

| Flag | Description |
|------|-------------|
| `--url <server-url>` | Server URL (default: `https://localhost:5001` or stored URL) |
| `--no-browser` | Use email/password prompt instead of browser login |

Credentials are stored in `~/.connapse/credentials.json`.

### `auth logout`

Clear stored credentials and revoke the CLI token on the server.

```bash
connapse auth logout
```

### `auth whoami`

Show current identity, server, and connection status.

```bash
connapse auth whoami
```

### `auth pat create`

Create a new Personal Access Token.

```bash
connapse auth pat create <name> [--expires <yyyy-MM-dd>]
```

| Argument/Flag | Description |
|---------------|-------------|
| `<name>` | Name for the token |
| `--expires <yyyy-MM-dd>` | Optional expiration date |

The token value is shown only once -- copy it immediately.

### `auth pat list`

List all your Personal Access Tokens with status, prefix, and dates.

```bash
connapse auth pat list
```

### `auth pat revoke`

Revoke a Personal Access Token by ID.

```bash
connapse auth pat revoke <id>
```

| Argument | Description |
|----------|-------------|
| `<id>` | PAT ID (GUID format) |

## Container Commands

### `container create`

Create a new container.

```bash
connapse container create <name> [--description "..."]
```

| Argument/Flag | Description |
|---------------|-------------|
| `<name>` | Container name (lowercase alphanumeric and hyphens) |
| `--description "..."` | Optional description |

### `container list`

List all containers with document counts.

```bash
connapse container list
```

### `container delete`

Delete an empty container.

```bash
connapse container delete <name>
```

| Argument | Description |
|----------|-------------|
| `<name>` | Container name |

## Upload Command

Upload file(s) to a container. Supports single files or entire directories (recursive).

```bash
connapse upload <path> --container <name> [--strategy <name>] [--destination <path>]
```

| Argument/Flag | Description |
|---------------|-------------|
| `<path>` | File or directory path to upload |
| `--container <name>` | **Required.** Target container name |
| `--strategy <name>` | Chunking strategy: `Semantic` (default), `FixedSize`, or `Recursive` |
| `--destination <path>` | Destination folder path in the container (default: `/`) |

The legacy alias `ingest` also works in place of `upload`.

## Search Command

Search within a container.

```bash
connapse search "<query>" --container <name> [--mode <mode>] [--top <n>] [--path <folder>] [--min-score <0.0-1.0>]
```

| Argument/Flag | Description |
|---------------|-------------|
| `"<query>"` | Search query text |
| `--container <name>` | **Required.** Container to search within |
| `--mode <mode>` | Search mode: `Semantic`, `Keyword`, or `Hybrid` (default: `Hybrid`) |
| `--top <n>` | Number of results to return (default: `10`) |
| `--path <folder>` | Filter results to a folder subtree |
| `--min-score <0.0-1.0>` | Minimum similarity score threshold |

## Reindex Command

Re-process and re-embed documents in a container.

```bash
connapse reindex --container <name> [--force] [--no-detect-changes]
```

| Flag | Description |
|------|-------------|
| `--container <name>` | **Required.** Container to reindex |
| `--force` | Ignore content hashes and reindex all documents |
| `--no-detect-changes` | Disable automatic settings change detection |

## Configuration

The CLI reads configuration from:

1. `appsettings.json` (optional, in the CLI directory)
2. Environment variables
3. Stored credentials at `~/.connapse/credentials.json` (set by `auth login`)

The `ApiBaseUrl` can be set via config, environment variable, or the `--url` flag on `auth login`.
