# Example custom source middle man

A small server that implements every part of the [Connapse custom source protocol](../../docs/protocols/custom-source-v1.md),
backed by a folder of files. It exists to test Connapse against and to read as a reference — not to deploy.

## What it pretends to be

- Each folder under `sample-data/scopes/` is a **scope**.
- Each top-level folder inside a scope gives its documents one **access key**: `folder:{scope}/{folder}`, or
  `folder:{scope}/` for files at the scope root.
- `example.json` lists the system's **users**, the keys each one holds (`grants`), and documents a user may not
  read even with the key (`denies`).
- Account links are kept in `links.json` next to `example.json` — the middle man's own storage.

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

## Settings in `example.json`

| Member | Meaning |
|---|---|
| `token` | The bearer token Connapse must send. |
| `permissions_enabled` | `false` makes it a content-only middle man. |
| `link_secret`, `connapse_issuer`, `provider_id` | Must match the custom provider in Connapse for linking to work. Remove `link_secret` to turn linking off. |
| `page_size`, `max_check_batch` | Page size and the largest permission-check batch accepted. |
| `preset_links` | Connapse user ID → example user, to skip the linking flow while testing. |
