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
changing needs Administrator.

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

## invalid-page

The page asked for is out of range: `skip` must be 0 or more, and `take` between 1 and 200.
