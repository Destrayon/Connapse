# Setting up a custom source

A custom source reads documents from a **middle man**: a server you run in front of any system, speaking the
[Connapse custom source protocol](protocols/custom-source-v1.md). This guide covers the Connapse side: what to
enter, where each value comes from, and what to do when a check fails.

Like every other kind of source, a custom source sits on a connection: an administrator adds the connection
once, with the middle man's address and token, and anyone who may add sources then picks a scope on it. There
are two kinds of custom connection:

- **Content only.** The connection holds the address and token. Everyone who can read its sources sees all of
  their documents.
- **With permission checks.** Add a custom provider first; the connection names it, and the provider holds the
  address and token. Every search result is checked with your middle man before it is shown.

## Before you start

- **A running middle man.** It must answer `GET /.well-known/connapse-source` with protocol version `1`. The
  example server in `samples/Connapse.CustomSource.Example` is one; `connapse-source-check` (see the protocol
  document's last section) tells you whether yours meets the protocol.
- **A token.** Your middle man decides its bearer token; Connapse sends it on every request. A middle man must
  refuse requests without it, or with a wrong one, with `401`. Connapse only reads, so give the token read-only
  access on your middle man.
- **HTTPS.** Connapse only calls `https://` addresses. For a middle man on the same machine or Docker network,
  an administrator can allow plain HTTP for its host by listing it in the `CustomSources:AllowedHttpHosts`
  setting (for example the environment variable `CustomSources__AllowedHttpHosts__0=bridge`).
- **For permission checks:** your middle man must declare `permissions.check` in its discovery document and
  answer `POST /access/v1/evaluations`.
- **You need the Admin role** to add providers, connections and sources.

## 1. Custom provider

**Providers → Custom providers → Add custom provider.** Only needed for permission checks; for a content-only
connection, skip to the next step.

| Field | Where it comes from |
|---|---|
| **Base URL** | Your middle man's address, such as `https://bridge.acme.internal`. |
| **Token** | The bearer token configured on your middle man (for the example server, `token` in `example.json`). |
| **Name** | Filled in from the name your middle man reports; change it if you like. |

Choose **Check middle man**. Connapse checks, in order, that the address is allowed, that the middle man speaks
protocol 1, that it accepts the token, that it refuses a wrong one, and that it answers a real permission check.
**Save provider** is available once every step passes.

**Account linking (optional).** If your middle man needs each user's own account in its system, it declares a
linking page. Choose **Generate link secret** and copy the three values shown into your middle man: the
**issuer** (Connapse's address), the **audience** (the provider's ID) and the **link secret**, which is shown
once. Users then link from **Profile → Integrations**.

## 2. Custom connection

**Connections → Add connection**, kind **Custom middle man**. Under **Custom provider**, pick your provider for
permission checks, or **No provider (content only)**. Without a provider, enter:

| Field | Where it comes from |
|---|---|
| **Base URL** | Your middle man's address, such as `https://bridge.acme.internal`. It cannot change once sources use it. |
| **Token** | The bearer token configured on your middle man. |

Choose **Test connection** to check the middle man without saving, or save: Connapse checks it with that token
before it saves, and a token it refuses is never stored. Later, **Edit** then **Test connection** re-checks with the
stored token. The stored token is only ever sent to the stored address; to change the address, enter its token too. With a provider, the connection holds nothing itself: the provider's address and token are used.

## 3. Custom source

**Sources → New source**, then pick your custom connection. The dialog shows the middle man it reads and loads
the scopes it offers; **Reload scopes** asks again.

| Field | Where it comes from |
|---|---|
| **Scopes** | Loaded from your middle man when you pick the connection. Tick any number: each becomes its own source, named after the connection and the scope (rename it afterwards on the source's page). Scopes the connection already has a source for are shown as already added. |
| **Sync interval in seconds** | Optional, and applies to every source added. At least 60, and your middle man may ask for a longer one. |

Over the REST API, `POST /api/sources` takes the connection's `connectionId` and a `scopeJson` of
`{"scopeId": "eng"}`; a source takes no `secret`.

Custom sources added before they needed a connection were moved onto generated content-only connections, one
per address and token, named after the middle man's host.

## Replacing a token

A source holds no token, so a new token goes where the old one is kept. For a content-only connection, choose
**Edit** on the Connections page and enter the new **Token**; for a provider, open its page and choose
**Change address or token**. Either way Connapse checks the new token with the middle man first, and if it is
refused the old one stays in use. Nothing is re-read.

When a middle man refuses a token during a sync, the connection shows **Token refused** on the Connections
page, the provider's page names the connections it was refused for, and the source's page says it is not
reading. It clears on the next successful sync, or when a new token passes the check.

## Account linking

Users link from **Profile → Integrations → Continue to … sign-in**. Your middle man's sign-in page opens; after
signing in there, the user returns to Connapse, which confirms the link for the person signed in. Connapse
stores nothing about the link. Deleting a Connapse user removes their link at every custom provider.

## Troubleshooting

| What you see | Likely cause | What to do |
|---|---|---|
| "Use HTTPS. Plain HTTP is allowed only for hosts listed in CustomSources:AllowedHttpHosts." | The address is plain HTTP. | Use HTTPS, or ask an administrator to allow the host in `CustomSources:AllowedHttpHosts`. |
| "Leave credentials, query strings and fragments out of the address" | The address carries a user name, query or fragment. | Enter just the address; the token goes in its own field. |
| "The middle man refused the token (401)", or **Token refused** on a connection | The token is wrong, or the middle man's token changed. | Enter the token configured on your middle man: on the connection, or on its custom provider (see [Replacing a token](#replacing-a-token)). |
| "This connection's custom provider no longer exists." | The provider the connection named was deleted. | Add a new connection that names an existing provider, or one without a provider. |
| "The middle man answered a request carrying a wrong token" | The middle man answers any request. | Make it refuse a missing or wrong token with `401`. |
| "The middle man does not speak custom source protocol version 1." | The discovery document lacks `"1"` in `protocol_versions`. | Fix the middle man's discovery document. |
| "answered … with a 3xx redirect" | The address points at a redirect, often `http` to `https`. | Enter the final address. |
| "Could not reach the middle man … its host name does not resolve" | The host name is wrong, or not resolvable from where Connapse runs. | Check the address. In Docker, use the middle man's container or service name on Connapse's network. |
| "Could not reach the middle man … nothing answered" or "… did not answer in time" | It is down, overloaded, or a firewall is in the way. | Check it is running and reachable from the Connapse server or container. |
| "Could not reach the middle man … the HTTPS connection failed" | Its certificate is expired, self-signed, or for another name. | Give it a certificate Connapse's host trusts. |
| "This connection holds no token.", or "… its connection holds no token" | A content-only connection was saved without one. | Edit the connection and enter the token. |
| "Enter the token for this address. The saved token is only sent to …" | The address changed and the token field was left blank. | Enter the token the new address expects, or put the old address back. |
| "Enter the middle man's full address, for example https://bridge.example.com." | The address is not a full URL. | Enter it with `https://` and the host. |
| "Use an HTTPS address." | The address uses a scheme other than HTTPS or allowed HTTP. | Use `https://`. |
| "answered … with an empty body" or "… JSON that does not match the protocol" | The middle man answered outside the protocol. | Compare its answer with the protocol document, or run `connapse-source-check` against it. |
| "asked Connapse to wait" | The middle man answered `429` or `503`. | Nothing to do; Connapse retries later. If it persists, check the middle man's load. |
| "answered N decision(s) for M document(s); none can be trusted" | A permission check answer had the wrong number of results. | Fix the middle man to answer one decision per document, in order. |
| "This connection names no middle man." | A custom connection's settings are incomplete. | Edit the connection and choose a provider or enter a middle man. |
| "A custom provider's address cannot change while connections use it" | Connections read the provider's current address. | Add a new provider for the other address, and new connections on it. |
| "No longer offered by the middle man" | A ticked scope was removed after the scopes loaded. | Choose **Reload scopes** and tick another. |
| "Tick at least one scope." | **Create source** was pressed with nothing ticked. | Tick the scopes to add. |
| "Could not load the scopes." or "Could not check the middle man." | Something unexpected failed in Connapse. | Try again; the server log has the cause. |
| "… cannot sync: it reads …, but its connection's middle man is …" | The source's address no longer matches its connection's. | Add a new source on the connection; this one cannot be repointed. |
| "Sources read this address, so it cannot change." | The connection's sources read the current address. | Add a new connection for the other middle man, and new sources on it. |
| "Enter a whole number of seconds, at least 60" | The sync interval is not a number, or below 60. | Enter 60 or more, or leave it empty for the default. |
| "The middle man answered … with 500" | The middle man failed. | Check the middle man's own logs. |
| "The middle man offers no scopes." | The scopes listing is empty. | Configure at least one scope on the middle man. |
| "The middle man does not answer permission checks" | `permissions.check` is missing from discovery, or the check call failed. | Declare it and serve `POST /access/v1/evaluations`. |
| Search shows nothing from a source with permission checks | The user is not linked, holds no keys, or the check failed or timed out. | Link the account under **Profile → Integrations**; check the middle man's logs. |
| "Status unknown" on a provider card | The middle man did not answer the link status call. | Check the middle man is running, then choose **Check again**. |
