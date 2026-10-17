# Example custom source server

A small server that implements every part of the [Connapse custom source protocol](../../docs/protocols/custom-source-v1.md),
backed by a folder of files. It exists to test Connapse against and to read as a reference — not to deploy.

## What it pretends to be

- Each folder under `sample-data/scopes/` is a **scope**.
- Each top-level folder inside a scope gives its documents one **access key**: `folder:{scope}/{folder}`, or
  `folder:{scope}/` for files at the scope root.
- `example.json` lists the system's **users**, the keys each one holds (`grants`), and documents a user may not
  read even with the key (`denies`).
- Account links are kept in `links.json` next to `example.json` — the source server's own storage.

## Run it

```bash
docker build -t connapse-custom-source-example samples/Connapse.CustomSource.Example
```

```bash
docker run --rm -p 8080:8080 -v "$PWD/samples/Connapse.CustomSource.Example/sample-data:/app/sample-data" connapse-custom-source-example
```

```bash
curl -H "Authorization: Bearer change-me-example-token" http://localhost:8080/.well-known/connapse-source
```

Or without Docker:

```bash
dotnet run --project samples/Connapse.CustomSource.Example --urls http://localhost:8080
```

## Check it with the conformance tool

`conformance_hooks.py` holds the commands the conformance tool needs to change documents and finish a
sign-in here. With the server running on `http://localhost:8080` over `sample-data` (set `link_secret`,
`connapse_issuer`, `provider_id` and `public_base_url` in `example.json` first):

```bash
dotnet run --project tools/Connapse.CustomSource.Conformance -- --url http://localhost:8080 --token change-me-example-token --scope eng --user connapse-alice --link-secret "<link_secret>" --provider-id "<provider_id>" --issuer "<connapse_issuer>" --create-command "python samples/Connapse.CustomSource.Example/conformance_hooks.py create samples/Connapse.CustomSource.Example/sample-data" --delete-command "python samples/Connapse.CustomSource.Example/conformance_hooks.py delete samples/Connapse.CustomSource.Example/sample-data" --sign-in-command "python samples/Connapse.CustomSource.Example/conformance_hooks.py sign-in samples/Connapse.CustomSource.Example/sample-data"
```

`connapse-alice` must be in `preset_links`. The same suite runs in CI against the server in-process
(`tests/Connapse.CustomSource.Example.Tests/ConformanceTests.cs`).

## Settings in `example.json`

| Member | Meaning |
|---|---|
| `token` | The bearer token Connapse must send. |
| `permissions_enabled` | `false` makes it a content-only source server. |
| `link_secret`, `connapse_issuer`, `provider_id` | Must match the custom provider in Connapse for linking to work. Remove `link_secret` to turn linking off. |
| `page_size`, `max_check_batch` | Page size and the largest permission-check batch accepted. |
| `preset_links` | Connapse user ID → example user, to skip the linking flow while testing. |
