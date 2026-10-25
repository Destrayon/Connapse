# Problem types

Every REST API failure is an RFC 9457 problem: `application/problem+json` with `type`, `title`,
`status`, an optional `detail`, and an `error` member kept for scripts written before the format
changed. When `type` points at this page, the heading it names below explains the problem. Any
other `type` is the RFC 9110 section for the HTTP status.

A `type` never changes once it ships. Each heading below is the slug an operation's failure
declares with `[Failure(status, slug, title)]`, and the architecture tests check that every
declared slug has an entry here.

## forbidden

The caller is signed in but their role doesn't allow this. Sources: reading needs the Viewer role,
changing needs Administrator. Containers: reading needs the Viewer role (over MCP, an agent key
with `knowledge:read` may read too), and changing them needs Editor (over MCP, an agent key granted
`knowledge:write` may change them too); a container's files follow the same rules. The audit log: reading needs Administrator, and a token needs
`admin:audit`. OAuth clients: listing and revoking need Administrator, and a token needs
`admin:oauth-clients`. Account links: reading needs a person or their own token with `knowledge:read`;
unlinking needs the person signed in themselves, never a personal access token or agent key.
Search needs `knowledge:read`: the Viewer role or above, or an agent key or token with that scope.

## invalid-link-provider

Unlinking named a provider Connapse doesn't link accounts with. Built-in providers are `AWS`, `Azure`,
`GitHub` and `Atlassian`; a custom provider is unlinked at `/api/v1/auth/custom/{providerId}`.

## link-not-found

The caller has no link to that provider, so there was nothing to unlink.

## link-changed

The AWS link was replaced by a new sign-in while it was being removed, so the new one was left in
place. Look at what is linked now and try again.

## link-provider-unavailable

The custom provider's source server didn't remove the link, so the caller is still linked there. Nothing
changed in Connapse; try again later.

## source-not-found

No source has that id, or it is one the caller may not know exists (a private GitHub repository the
caller can't read, or an Atlassian or permission-checked custom source, listed only to
administrators).

## invalid-source

A source's settings were refused: a scope that isn't valid JSON, a change its provider fixes at
creation (a GitHub source's repository or private flag, a custom source's server or scope, an
Atlassian source's space or project), or a secret, which a source never holds. `field` names the
input at fault when there is one.

## source-conflict

A source with that name already exists, or the connection already has a source for that scope (a
Confluence space, Jira project or custom scope is added once per connection).

## provider-unavailable

The provider couldn't be asked while checking the source's scope: Atlassian or the custom source
server didn't answer. Nothing was saved; try again.

## sync-in-progress

A sync for this source is already running, most often the scheduled one. Nothing went wrong and
nothing was started; the running cycle picks up the changes.

## provider-not-found

The provider named in the path isn't one Connapse has. Providers are named as the API writes them, such as
`S3`, `AzureBlob`, `Filesystem`, `Sftp`, `GitHub`, `Atlassian` and `Custom`, in any case.

## invalid-audit-filter

An audit log filter can't match anything: `since` must be before `until`. Both are UTC; a time given
without a zone is read as UTC.

## oauth-client-not-found

No OAuth client with that id has access: it isn't registered and holds no refresh tokens or pending
authorization codes. It may already have been revoked.

## search-scope-not-found

No container or source has that id or name, or it is a source the caller may not know exists. A
source listed only to administrators (a Confluence space, a Jira project, a permission-checked
custom source) can still be searched by its id, and reads as not found when the caller can read
nothing in it, so naming one doesn't confirm it exists.

## invalid-search

The search was refused: the query is missing or over 10,000 characters, `topK` is outside 1 to 100,
or `minScore` is outside 0.0 to 1.0. `field` names the input at fault.

## invalid-page

The page asked for is out of range: `skip` must be 0 or more, and `take` between 1 and 200. For the
audit log, `before` must be a `nextBefore` value the endpoint returned.

## approval-not-signed-in

Reviewing MCP approval requests needs a signed-in person. Sign in to Connapse and open the
Approvals page.

## approval-ui-only

An MCP approval request can only be listed or decided in the Connapse UI, on the Approvals page.
A token must never approve the call it is waiting on, so REST and MCP callers are refused.

## approval-not-found

No waiting approval request has this id, or it belongs to someone else. Only the person a token
acts for, or an administrator, can see and decide its requests.

## approval-not-pending

The approval request was already approved or denied, or it expired. A request waits a day; an
approval can be used once, within 15 minutes.

## container-not-found

No container has that id or name.

## invalid-container

The container's name or the change asked for isn't valid. A name is 2 to 128 characters of lowercase
letters, digits and hyphens, and can't start or end with a hyphen. A rename names a new name, a new
description, or both. The `field` member names the input at fault.

## container-conflict

Another container already has that name. Names are unique across Connapse.

## container-not-empty

The container still has files. Deleting a container deletes what it stores, so it must be emptied
first; the detail says how many documents remain.

## invalid-reindex

A container reindex asked for something it can't do: `maxDocuments` must be a positive number, and
`continueAfter` must be the document id a previous paged reindex returned. The `field` member names
the input at fault.

## file-not-found

No document has that id or path in this container, or the caller may not read it. A document the caller may
not read is answered exactly as a missing one, so a request can't learn that it exists.

## invalid-file-request

A file request asked for something it can't do: a bulk delete names between 1 and 100 file ids, and a bulk
upload between 1 and 100 files. An uploaded file needs a name of at most 255 characters with no folder in it, a
folder path with no `..` and at most 50 levels, and some content. The `field` member names the input at fault.

## file-not-ready

The document is still being ingested, so it has no text to read yet; the detail gives its status. Try again
once ingestion finishes.

## file-ingestion-failed

The document's ingestion failed, so it has no text to read; the detail says why. Retry the file, or upload it
again once the cause is fixed.

## no-parser

No parser reads this file type, so its text can't be extracted.

## file-not-readable

The document is recorded, but its stored file can't be read from storage.

## unsupported-file-type

No parser reads files of this type, so it can't be uploaded. The detail lists the types that can be. In a bulk
upload, each such file is reported in its place and the rest are still uploaded.

## container-not-writable

The container's storage doesn't take uploads.
