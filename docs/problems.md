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

Settings: reading, changing and resetting them, and the settings actions (re-indexing, listing embedding
models, testing a connection, clearing all summaries), need Administrator, and a token needs `admin:settings`.

Connections: listing, reading, renaming, pausing, resuming, deleting them and replacing their secret
(over `PUT /api/v1/connections/{id}/secret`, `PUT /api/v1/atlassian/sites/{id}/credentials` or a change that
carries one) need Administrator, and a token needs `admin:sources`.

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

## settings-category-not-found

Connapse has no settings category by that name. The categories are `embedding`, `chunking`, `search`,
`llm`, `upload` and `summary`, in any case, and `samlsignin`, `identitycenter`, `azure` and `azuread` can be read.

## provider-owned-settings

These settings change who can sign in or switch per-user permission enforcement on, so only provider
setup changes them. They can still be read here. The detail names the provider route that changes them.

## settings-changed

The settings were changed after you read them, so your save was refused rather than overwrite that
change. Read them again (`GET /api/settings/{category}`), make your change again, and save with the new
`ETag` in `If-Match`. Only a save that sends `If-Match` is checked.

## invalid-settings

The settings were refused: the body isn't a JSON object, isn't valid JSON, or names a property the
category doesn't have. A connection test is refused the same way when it has no category or settings,
its settings don't fit the category, or `timeoutSeconds` is outside 1 to 300; a re-index is refused
when `containerId` isn't a container's id. `field` names the input at fault.

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

## folder-not-found

No folder has that path in this container: none is recorded there, and no document's path lies under it.

## invalid-folder

The folder path can't be used: it is required, can't contain `..`, can't be more than 50 levels deep, and can't
be the root folder. The `field` member names the input at fault.

## folder-exists

A folder already has that path in this container.

## batch-not-found

No ingestion job has that id, or the caller may not read the document it ingests. The id is the job id an upload
returned.

## user-not-found

No user has that id.

## invalid-roles

The roles asked for can't be given: none at all (a user keeps at least one), one that isn't a role
(the roles are `Viewer`, `Editor` and `Admin`), or `Owner` or `Agent`, which are never assigned.
`field` is `roles`.

## user-changed

The user's roles changed between the check of what you may do and the change itself, most often
because someone else changed them, so nothing was done. Load the user again and retry.

## user-not-changed

The user's account store refused the change, so nothing was changed. The detail says why when it
can.

## invalid-invite

The invite can't be made as asked: the email is missing, longer than 256 characters, or not an
email address (`field` is `email`), or the role isn't one an invite gives. The roles are `Viewer`,
`Editor` and `Admin`; `Owner` and `Agent` are never given (`field` is `role`).

## user-exists

Someone already has an account with that email, so there's no one to invite. `field` is `email`.

## invite-pending

That email already has a pending invite. Revoke it first to send a new link.
`field` is `email`.

## invite-not-found

No pending invite has that id. It may have been revoked, or already accepted.

## agent-not-found

No live agent has that id. It may have been deleted.

## agent-key-not-found

That agent has no active key with that id. It may have been revoked already.

## invalid-agent

The agent can't be made as asked: the name isn't 2 to 64 letters, digits, hyphens and underscores
(`field` is `name`), or the description is longer than 500 characters (`field` is `description`).

## invalid-agent-key

The key can't be made as asked: the name isn't 2 to 64 characters (`field` is `name`), or a scope
isn't one an agent key can hold. The scopes are `knowledge:read`, `knowledge:write` and
`agent:ingest` (`field` is `scopes`).

## agent-exists

A live agent already has that name. `field` is `name`.

## pat-not-found

You have no active personal access token with that id. It may have been revoked already, or it is
someone else's: each person sees and revokes only their own.

## invalid-pat

The token can't be made as asked: the name is blank or longer than 256 characters (`field` is
`name`), a scope isn't one a personal access token can hold (`field` is `scopes`), or the expiry
isn't in the future (`field` is `expiresAt`). The scopes are `knowledge:read`, `knowledge:write`,
`agent:ingest`, `admin:users`, `admin:audit`, `admin:oauth-clients`, `admin:settings` and `admin:sources`.
An expiry that names no time zone is read as UTC.

## connection-not-found

No connection has that id. On the `/api/v1/atlassian/sites` routes, a connection to another
provider reads as missing too.

## invalid-connection

The connection change was refused: a name that is blank or longer than 128 characters (`field` is
`name`), a delete whose `confirmName` isn't the connection's name (`field` is `confirmName`), or a
resume whose `sourceIds` is empty or names a source on another connection (`field` is `sourceIds`).

A new connection or a settings change is refused for the same reasons the Connections page refuses
it. `field` says which part:

- `provider`: no provider was given, or it is Atlassian, whose sites are added with
  `POST /api/v1/atlassian/sites`.
- `config`: the settings aren't a JSON object, a required setting is missing, the custom provider
  doesn't exist, or the connection is an Atlassian site, whose credentials change with
  `PUT /api/v1/atlassian/sites/{id}/credentials`.
- `installationId`: the GitHub App isn't set up, or isn't installed where the connection says.
- `region`: an S3 connection has no region and Connapse couldn't look up the bucket's.
- `privateKey`, `token`: a secret replacement left out the key or token it replaces.
- `secret`, `baseUrl`: a custom source server refused the token or address. A new address needs its
  token sent with it, because the stored token is only ever sent to the stored address.

## connection-check-unavailable

Connapse couldn't finish checking the connection's settings: GitHub or the custom source server
didn't answer. Nothing was saved. `field` says which check; try again.

## connection-conflict

Another connection already has that name. `field` is `name`. Without a `field`, the connection changed while new
settings or a new secret were being checked, or while it was being tested, so its stored secret was not used and no
health was recorded; reload it and try again.

## connection-in-use

Sources still use the connection, so it can't be deleted. `detail` names them, and `sources` lists
each one with a link; delete those sources first.

## connection-has-no-sources

The connection has no sources, so there is no syncing to pause or resume.

## custom-provider-not-found

No custom provider has that id. It may have been deleted.

## invalid-custom-provider

The custom provider can't be saved as asked. Its source server didn't pass the check: the address
breaks a rule or doesn't answer (`field` is `baseUrl`), or the token was refused, a wrong token was
accepted, or the saved token can't go to a new address (`field` is `token`). Or the name isn't 1 to
128 characters (`field` is `name`). Nothing was saved; run the check to see each step.

## custom-provider-conflict

Another custom provider already has that name (`field` is `name`), or connections use this provider,
so its address can't change (`field` is `baseUrl`).

## custom-provider-in-use

Connections still check permissions with this custom provider, so it can't be deleted: their sources
would lose their permission checks. The detail names them; delete them or move them to another
provider first.

## invalid-aws-setup

The AWS setup can't be saved as asked: a value is missing, the region isn't an AWS region such as
`us-east-1`, an address isn't a full http or https address, or a blank `privateKeyPem` was sent with no
stored key to keep (or one that can't be read). `field` names the input at fault. Nothing was saved.

## aws-credential-refused

AWS didn't give a session for this IAM Roles Anywhere credential, so nothing was saved and the current
credential still works. The detail is AWS's reason; check the certificate, ARNs and region match.

## aws-access-changed

The stored AWS credential changed since it was read, so nothing was changed. This happens when it no longer
matches `expected`, or when someone else saved or removed a credential while this save or removal was running.
Read the providers again, review the values and try again.

## invalid-azure-setup

The Azure setup can't be saved as asked: the tenant id is missing, the tenant isn't the one this host's
managed identity is in, or the redirect address isn't a full http or https address. `field` names the input
at fault. Nothing was saved.

## azure-credential-refused

Entra didn't give a token for this credential, so nothing was saved and the current one still works. The
detail is the reason; check the tenant, client id and certificate path, and that Connapse can read the file.
Right after the setup script runs, Entra may still be registering the certificate: wait a minute and save again.

## enforcement-not-on

Per-user permission enforcement for the provider (AWS or Azure) couldn't be switched on in this process, so
the sign-in application wasn't saved: saving it with enforcement off would show every user every document
from that provider. Restart Connapse and save again.

## invalid-github-app

The GitHub App values can't be used as sent: the App ID is missing or not a positive number, no private
key was sent and none is stored for that App, the key isn't a readable `.pem` file, or an organisation
named to own a new App isn't one on GitHub. `field` names the input at fault. Nothing was changed.

## github-app-replace-unconfirmed

A different GitHub App is stored. Replacing it disconnects every GitHub connection, because each is an
installation of the stored App, so send `replaceExisting: true` to confirm, or use the stored App's ID.
It is also the answer when the stored App changed while GitHub was checking the values sent; read it
again and resend. Nothing was changed.

## github-app-refused

GitHub didn't accept the App ID and private key together, or the client secret isn't the App's. `field`
names which. Nothing was stored, and the current App still works; check the App ID, and that the key or
secret was generated for that App.

## github-unavailable

GitHub couldn't be reached, or answered in a way Connapse couldn't use, so nothing was changed. Check that
this server can reach api.github.com, then try again.

## github-manifest-expired

GitHub sent back a GitHub App creation that this person didn't start, or started more than an hour ago,
or that was already finished. Nothing was stored; start the creation again.

## github-manifest-no-code

GitHub sent the person back without the code that hands over the new App, so nothing was stored. Start
the creation again.
