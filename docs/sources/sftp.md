---
title: Index a folder over SFTP
description: Index a folder on your computer, a NAS or another server over SFTP.
type: how-to
audience: admin
---

# Index a folder over SFTP

Use this when the files you want to search sit in a folder on a machine that Connapse can reach over SSH: your own computer, a NAS or another server. The folder stays in sync as it changes. SFTP has no per-user permissions, so everyone who can search a source sees every file in it.

For a folder you only need once, [upload it from the browser](uploads-and-minio.md#upload-a-folder) instead.

## Prerequisites

- An Administrator account in Connapse.
- An SSH server on the machine that holds the files, and a network path from Connapse to it on port 22 or another port you choose.

## Connect a machine

### Connect this computer

1. On **Connections**, choose **Connect this computer**.
2. Connapse generates a key pair and shows one command. Run it in an administrator terminal on the computer that holds the files. It installs and starts the SSH server if needed, opens TCP 22 to local subnets, and installs the key.
3. Paste what the command printed back into Connapse, then enter the folder you want to index.
4. Open **Sources** → **New source**, pick the connection and enter a **Sub-path** inside the folder. Leave it blank to index the whole folder.
5. Optional: add include and exclude patterns, such as `*.md` or `drafts/**`.
6. Save. The first sync starts within a few minutes, or choose **Sync** on the source.

The address defaults to `host.docker.internal`, which reaches the host from a container on Docker Desktop. On a Linux Docker host, add `extra_hosts: ["host.docker.internal:host-gateway"]` to the `web` service.

### Connect a server

1. Open **Connections** → **Add connection**, choose **SFTP**, then choose **Set this up for me** in the form. You can also choose **Connect this computer** and switch to **Another server**.
2. Enter the address Connapse should use to reach the server.
3. Run the command Connapse shows over an SSH session on that server. It installs the key and leaves the SSH server and firewall as they are.
4. Paste what it printed back into Connapse, enter the folder, and save.

Both guided paths install the key with `restrict` and a forced `internal-sftp` command, so it can't open a shell or forward ports. The host key fingerprint comes back in the pasted text, so the first connection is verified rather than trusted blindly. Both also report whether the machine still accepts password logins, without changing that setting. Connapse never asks for a password to install a key itself.

### Set up SFTP by hand

1. Turn on an SSH server: OpenSSH Server on Windows, Remote Login on macOS, `sshd` on Linux.
2. Add a public key to the account's `authorized_keys`, prefixed with `restrict,command="internal-sftp"`.
3. On **Connections** → **Add connection** → **SFTP**, enter the host, port, user name, the matching private key and its passphrase if it has one, and the **Allowed root**.
4. Create a source on the connection with a **Sub-path** inside the root.

On Windows, OpenSSH writes paths with a slash before the drive letter: write `/C:/Users/you/Documents`, not `C:\Users\you\Documents`.

SFTP has no change notifications, so every sync walks the whole folder. Point a source at a folder, not a whole drive: at a hundred thousand files, a sync takes minutes.

## Check or replace the host key

Connapse trusts the first host key an SFTP server presents and pins it. Every later connection that sees a different key is refused, and the error names both keys. The pinned fingerprint is shown on the connection in the format `ssh-keygen` prints, so you can compare it with the server:

```bash
ssh-keyscan files.example.com | ssh-keygen -lf -
```

If you rekey the server yourself, syncing stops with a mismatch error. Choose **Forget** beside the fingerprint on the connection and save. The next connection pins the new key.

## Do it with the API

Create an SFTP connection, then a source on it. Both calls need an Administrator token with the `admin:sources` scope. The private key travels in the request body, and it's stored encrypted and never returned.

```bash
curl -X POST http://localhost:5001/api/v1/connections \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"File server","provider":"Sftp","config":{"host":"files.example.com","port":22,"username":"connapse","allowedRoot":"/srv/knowledge"},"privateKey":"-----BEGIN OPENSSH PRIVATE KEY-----\n...\n-----END OPENSSH PRIVATE KEY-----","passphrase":null}'

curl -X POST http://localhost:5001/api/sources \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Handbook","connectionId":"CONNECTION_ID","scope":{"type":"sftp","subPath":"handbook","includePatterns":["*.md"],"excludePatterns":[]}}'
```

The connection call returns the new connection under `connection`; put its `connection.id` in place of `CONNECTION_ID`. Related calls:

| Task | Call |
|---|---|
| Test the connection | `POST /api/v1/connections/{id}/test` |
| Forget the pinned host key | `DELETE /api/v1/connections/{id}/host-key` |
| Replace the private key | `PUT /api/v1/connections/{id}/secret` with `privateKey` and `passphrase` |

## How Connapse keeps sources inside the root

- A source's sub-path is resolved under the connection's root on the remote server, because the root names a directory on that machine, not on the Connapse host. Connapse checks that the result is still inside the root.
- Syncs step over symbolic links rather than following them, so a link to a folder outside the root can't pull that folder in.
- A folder that can't be read fails the whole sync, rather than looking as if every file in it was deleted.

Two cases no path check can catch: bind mounts and hard links, which really are inside the root, and a path swapped out between Connapse's check and its read. Sign in as a low-privilege account that can read only what you mean to index.

## Related

- [Containers, connections and sources](index.md)
- [Use uploads and MinIO](uploads-and-minio.md)
