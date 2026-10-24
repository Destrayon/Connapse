# Problem types

Every REST API failure is an RFC 9457 problem: `application/problem+json` with `type`, `title`,
`status`, an optional `detail`, and an `error` member kept for scripts written before the format
changed. When `type` points at this page, the heading it names below explains the problem. Any
other `type` is the RFC 9110 section for the HTTP status.

A `type` never changes once it ships. Each heading below is the slug an operation's failure
declares with `[Failure(status, slug, title)]`, and the architecture tests check that every
declared slug has an entry here.

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
