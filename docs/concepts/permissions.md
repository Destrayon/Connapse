---
title: About permissions
type: explanation
audience: admin
---

# About permissions

Two layers decide what someone sees in Connapse. Their **role** decides what they may do in Connapse itself, and for content indexed from another system, that **system's own permissions** decide which documents appear in their results. Connapse never shows someone a document the source system wouldn't let them open.

## Roles and scopes

Every account has one role: Owner, Admin, Editor or Viewer. The role sets what the person can do: Viewers search and browse, Editors also upload and delete, and Admins also manage users, connections, sources and settings. See [Manage users and invites](../guides/users-and-invites.md).

A token narrows that further. Each personal access token or agent key carries scopes, such as `knowledge:read`, and a request passes only when both the role and a scope allow it. A token can never do more than the account that made it. The full table is in [Roles and scopes](../reference/roles-and-scopes.md).

Containers have no per-container permissions: a role applies to every container.

## Permissions from the source system

A source that has its own permission model is filtered per document, at search time, by asking that system. A grant or a revocation there takes effect in Connapse without a sync, within the few minutes the answer is cached.

To ask on someone's behalf, Connapse has to know who they are in that system. Each person links their account once on **Profile** → **Integrations**:

| Source | Who decides | What's linked |
|--------|-------------|---------------|
| Amazon S3 | S3 Access Grants, for the user and their groups | An IAM Identity Center user |
| Azure Blob Storage | Azure RBAC, deny assignments and Data Lake ACLs | A Microsoft Entra account |
| GitHub | GitHub's access to each private repository; public repositories are open to everyone | A GitHub account |
| Confluence and Jira | Confluence's and Jira's own permissions | An Atlassian account |
| Custom source | Your custom source server | An account on your server |
| Filesystem, SFTP and uploads | Nobody: everyone who may search sees every file | Nothing |

Each source's setup page says exactly what is checked and how long answers are cached.

## Failing closed

When a check can't be answered, Connapse hides the result rather than showing it. A person with no linked account sees nothing from a source that needs one; a lookup that fails or times out hides the results it would have decided; a document whose permissions can't be read is left out. A missing result is a nuisance someone reports. A leaked one can't be taken back.

The same rule decides how fine the check is. Connapse checks each document the way the source system does, never by the folder or bucket around it, so a document locked down inside an open folder stays locked down.

## Connapse only reads permissions

Connapse never writes grants, roles or ACLs in the source system. Administrators manage access where they always have, in the S3 console, Azure, GitHub or Atlassian, and Connapse follows.

## Related

- [Roles and scopes](../reference/roles-and-scopes.md)
- [Containers, connections and sources](../sources/index.md)
- [Manage users and invites](../guides/users-and-invites.md)
