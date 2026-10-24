# Problem types

Every REST API failure is an RFC 9457 problem: `application/problem+json` with `type`, `title`,
`status`, an optional `detail`, and an `error` member kept for scripts written before the format
changed. When `type` points at this page, the heading it names below explains the problem. Any
other `type` is the RFC 9110 section for the HTTP status.

A `type` never changes once it ships. Each heading below is the slug an operation's failure
declares with `[Failure(status, slug, title)]`, and the architecture tests check that every
declared slug has an entry here.
