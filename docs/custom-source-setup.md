# Setting up a custom source

A custom source reads documents from a **middle man**: a server you run in front of any system, speaking the
[Connapse custom source protocol](protocols/custom-source-v1.md). This guide covers the Connapse side: what to
enter, where each value comes from, and what to do when a check fails.

There are two ways to use a middle man:

- **Content-only source.** Add the source directly. Everyone who can read the source sees all of its documents.
- **Source with permission checks.** Add a custom provider, then a custom connection that uses it, then the
  source on that connection. Every search result is checked with your middle man before it is shown.

## Before you start

- **A running middle man.** It must answer `GET /.well-known/connapse-source` with protocol version `1`. The
  example server in `samples/Connapse.CustomSource.Example` is one; `connapse-source-check` (see the protocol
  document's last section) tells you whether yours meets the protocol.
- **A token.** Your middle man decides its bearer token; Connapse sends it on every request. A middle man must
  refuse requests without it, or with a wrong one, with `401`.
- **HTTPS.** Connapse only calls `https://` addresses. For a middle man on the same machine or Docker network,
  an administrator can allow plain HTTP for its host by listing it in the `CustomSources:AllowedHttpHosts`
  setting (for example the environment variable `CustomSources__AllowedHttpHosts__0=bridge`).
- **For permission checks:** your middle man must declare `permissions.check` in its discovery document and
  answer `POST /access/v1/evaluations`.
- **You need the Admin role** to add providers and connections.

## 1. Custom provider

**Providers → Custom providers → Add custom provider.** Only needed for permission checks.

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

**Connections → Add connection**, kind **Custom provider (permission checks)**. Pick the provider. A connection
holds no credentials of its own.

## 3. Custom source

**Sources → New source.** Pick **Custom source** for a content-only source, or your custom connection.

| Field | Where it comes from |
|---|---|
| **Base URL** | Your middle man's address. On a custom connection it is fixed to the provider's. |
| **Token** | The bearer token configured on your middle man. |
| **Scope** | Choose **Load scopes**, then pick one. Each scope your middle man offers becomes its own source. |
| **Name** | Filled in from the scope's name. |
| **Description**, **Sync interval in seconds** | Optional. The interval is at least 60, and your middle man may ask for a longer one. |

## Replacing a token

When your middle man's token changes, open the source (**Sources → the source's name**), enter the new one in
**New token** under **Token**, and choose **Replace token**. Connapse checks it with the middle man first; if it is refused, the old token stays in use.
Nothing is re-read. A provider's token is replaced on its page under **Change address or token**.

## Account linking

Users link from **Profile → Integrations → Continue to … sign-in**. Your middle man's sign-in page opens; after
signing in there, the user returns to Connapse, which confirms the link for the person signed in. Connapse
stores nothing about the link. Deleting a Connapse user removes their link at every custom provider.

## Troubleshooting

| What you see | Likely cause | What to do |
|---|---|---|
| "Use HTTPS. Plain HTTP is allowed only for hosts listed in CustomSources:AllowedHttpHosts." | The address is plain HTTP. | Use HTTPS, or ask an administrator to allow the host in `CustomSources:AllowedHttpHosts`. |
| "Leave credentials, query strings and fragments out of the address" | The address carries a user name, query or fragment. | Enter just the address; the token goes in its own field. |
| "The middle man refused the token (401)" | The token is wrong, or the middle man's token changed. | Enter the token configured on your middle man. For an existing source, replace it under **Token**. |
| "The middle man answered a request carrying a wrong token" | The middle man answers any request. | Make it refuse a missing or wrong token with `401`. |
| "The middle man does not speak custom source protocol version 1." | The discovery document lacks `"1"` in `protocol_versions`. | Fix the middle man's discovery document. |
| "answered … with a 3xx redirect" | The address points at a redirect, often `http` to `https`. | Enter the final address. |
| "Could not reach the middle man" | It is down, or the host name does not resolve from Connapse. | Check it is running and reachable from the Connapse server or container. |
| "The middle man answered … with 500" | The middle man failed. | Check the middle man's own logs. |
| "The middle man offers no scopes." | The scopes listing is empty. | Configure at least one scope on the middle man. |
| "The middle man does not answer permission checks" | `permissions.check` is missing from discovery, or the check call failed. | Declare it and serve `POST /access/v1/evaluations`. |
| Search shows nothing from a source with permission checks | The user is not linked, holds no keys, or the check failed or timed out. | Link the account under **Profile → Integrations**; check the middle man's logs. |
| "Status unknown" on a provider card | The middle man did not answer the link status call. | Check the middle man is running, then choose **Check again**. |
