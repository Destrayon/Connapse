---
title: Containers, connections and sources
type: explanation
audience: admin
---

# Containers, connections and sources

Connapse keeps two kinds of content apart, because they have different owners. A **container** holds files that belong to Connapse. A **source** indexes content that lives somewhere else, such as an S3 bucket, a GitHub repository or a Confluence space. A **connection** sits between a source and that system: it says how Connapse signs in and what it may reach.

| | Container | Source |
|---|---|---|
| Whose data | Connapse's own | Another system's |
| Browsable as a file tree | Yes | No |
| Writable | Yes: upload, delete, create folders | Never |
| Searchable | Yes | Yes |
| Created by | Any editor | Administrators only |
| Backed by | Connapse's managed storage | A connection |

One connection can serve many sources. For example, a single connection to an AWS account can back one source per bucket.

## Containers

A container is a folder tree in Connapse's managed storage: MinIO by default, or another S3-compatible service set in the server's configuration. Nothing about storage is chosen per container. See [Use uploads and MinIO](uploads-and-minio.md).

A container must be empty before you can delete it, because deleting it deletes its stored files.

## Connections

A connection holds how to authenticate and what the credential may reach. It never names a bucket, repository or space; the source does that. Administrators create connections on the **Connections** page or with `POST /api/v1/connections`. Atlassian sites are the exception: they're added on the same page or with `POST /api/v1/atlassian/sites`, which runs the site's checks first.

### What each provider uses to sign in

Connapse doesn't accept pasted cloud keys. Each provider signs in with the identity set up on its provider page, under **Admin** → **Providers**:

| Provider | Signs in as | Rotating the credential |
|---|---|---|
| Amazon S3 | An IAM Roles Anywhere identity created from a certificate made on the Connapse host, or the AWS default credential chain. `roleArn` adds a role to assume. | Done in AWS |
| Azure Blob Storage | The managed identity Connapse runs as, or an app registration with a certificate | Done in Azure |
| GitHub | An installation of the GitHub App, with hour-long installation tokens | Done by GitHub |
| Atlassian | A service account's OAuth client credentials, stored encrypted | Replace the secret in Connapse |
| Custom source | A token from your custom source server, stored encrypted | Replace the token in Connapse |
| Filesystem | The account the Connapse server runs as | Nothing to rotate |
| SFTP | An SSH private key, stored encrypted | Replace the key in Connapse |

### Allowed locations and roots

Every source that shares a connection presents the same identity to the remote system, so the remote can't tell them apart. A connection's allowlist is where you say which parts any of its sources may index:

- `allowedLocations` on S3 and Azure connections lists buckets or containers, optionally with a prefix such as `shared-docs/public/`. A prefix entry allows only sources at or below it.
- `allowedRoot` on Filesystem and SFTP connections names the one directory sources may use.

An S3 or Azure connection with no `allowedLocations` allows anything the identity can reach and logs a warning at each sync. A list whose entries are all blank allows nothing.

For Filesystem connections, `Sources:Security:AllowedFilesystemRoots` limits which roots administrators may pick at all. See [Index a filesystem or SFTP server](filesystem-and-sftp.md).

## Sources

A source names what to index inside a connection: a bucket and prefix, a folder, a repository, a space or a project. Its `scope` is a typed object whose `type` names the provider:

| `type` | Required keys | Optional keys |
|---|---|---|
| `s3` | `bucketName` | `prefix` |
| `azureBlob` | `containerName` | `prefix` |
| `filesystem` | | `subPath`, `includePatterns`, `excludePatterns` |
| `sftp` | | `subPath`, `includePatterns`, `excludePatterns` |
| `github` | `owner`, `repo`, `kind` | `includePatterns`, `excludePatterns`, `includeComments`, `includeCommentAuthors`, `excludeCommentAuthors` |
| `confluence` | `spaceId` | `includeAttachments`, `maxAttachmentMb` |
| `jira` | `projectId` | `includeAttachments`, `maxAttachmentMb` |
| `custom` | `baseUrl`, `scopeId` | |

For `confluence` sources, `includeAttachments` defaults on when the site's last test confirmed the `read:attachment:confluence` scope, otherwise off; `jira` sources follow the same rule with `read:attachment:jira`. `maxAttachmentMb` defaults to 25.

`GET /api/v1/sources/scope-schema/{provider}` returns the full schema for one provider. Each setup page shows a complete example.

Reading sources needs the `knowledge:read` scope. Creating, changing, deleting and syncing them needs an Administrator with the `admin:sources` scope, because a source decides what outside data gets indexed.

A source's response doesn't include its scope, its sync cursor or, for non-administrators, its last sync error. Those name buckets, paths and failures that a read-only user shouldn't learn. Confluence and Jira sources are the exception: their space or project settings come back under `confluence` or `jira`, because administrators edit them on the **Sources** page.

Deleting a source removes its indexed documents. The content in the remote system is untouched.

### What each provider indexes

| Provider | Indexes | Per-user permissions |
|---|---|---|
| [Uploads](uploads-and-minio.md) | Files uploaded to a container | None: every user's role applies to every container |
| [Filesystem and SFTP](filesystem-and-sftp.md) | Files under a folder | None: everyone who can search the source sees every file |
| [Amazon S3](aws-setup.md) | Objects in a bucket, under an optional prefix | S3 Access Grants, through IAM Identity Center |
| [Azure Blob Storage](azure-setup.md) | Blobs in a container, under an optional prefix | Azure RBAC and Data Lake ACLs, through Microsoft Entra sign-in |
| [GitHub](github-setup.md) | A repository's Markdown files, or its issues and pull requests | The reader's access to the repository, through a linked GitHub account |
| [Confluence and Jira](atlassian-setup.md) | One Confluence space or Jira project, with optional attachments | The reader's access in Atlassian, through a linked Atlassian account |
| [Custom source](custom-source-setup.md) | Whatever your custom source server returns for a scope | Decided by your server, through a linked account |

## Sync

Connapse checks each enabled source every 5 minutes, or at the source's `syncIntervalSeconds`, and reconciles what it has indexed with what the remote lists:

- A file is reindexed when its last-modified time or size changes.
- A source that is still syncing is skipped, not queued, so a slow remote can't pile up syncs.
- `POST /api/sources/{id}/sync` starts a sync now.

### Deletions are guarded

A sync treats anything missing from the remote's listing as deleted. A listing can come back empty and still succeed, for example when a bucket policy narrows or a mount drops. So when a sync would delete more than both 10 documents and 10% of the source, it applies its additions and holds back the deletions. The **Sources** page shows administrators how many were held back, with an **Apply deletions** button.

- Approving runs the sync again. If the remote has recovered, nothing is deleted.
- Approving covers up to the number you saw. If more files have gone missing since, the larger set is held back again.
- The threshold is fixed. Small sources are never blocked.
- The guard checks one sync at a time, so a listing that loses a little less than the threshold on every sync isn't caught.

## Related

- [Automate setup with the API](../guides/automate-with-the-api.md)
- [How permissions work](../concepts/permissions.md)
- [REST API reference](../reference/rest-api.md)
