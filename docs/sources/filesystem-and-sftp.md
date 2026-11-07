---
title: Index a filesystem or SFTP server
description: Index a folder on the Connapse host or a remote machine over SFTP.
type: how-to
audience: admin
---

# Index a filesystem or SFTP server

Use this when the files you want to search sit in a folder: on the disk the Connapse server runs on (Filesystem), or on another machine that Connapse can reach over SSH (SFTP). Neither provider has per-user permissions, so everyone who can search a source sees every file in it.

## Prerequisites

- An Administrator account in Connapse.
- For Filesystem: the folder is visible to the Connapse server process. Under Docker, that means mounted into the `web` container: see [Reach a folder on this machine from Docker](#reach-a-folder-on-this-machine-from-docker).
- For SFTP: an SSH server on the machine that holds the files, and a network path from Connapse to it on port 22 or another port you choose.

## Index a folder on the server's own disk

1. Open **Connections** → **Add connection** and choose **Filesystem**.
2. Enter the **Allowed root**, the directory every source on this connection must stay inside, such as `/srv/knowledge`. If your deployment sets `Sources:Security:AllowedFilesystemRoots`, the field becomes a list of those roots.
3. Save the connection.
4. Open **Sources** → **New source**, pick the connection and enter a **Sub-path** inside the root. Leave it blank to index the whole root.
5. Optional: add include and exclude patterns, such as `*.md` or `drafts/**`.
6. Save. The first sync starts within a few minutes, or choose **Sync** on the source.

Connapse picks up changes on each scheduled sync, not as they happen. Choose **Sync** on the source to pick them up sooner.

### Reach a folder on this machine from Docker

A Connapse running in Docker can't see your disks until you mount a folder into the `web` container. Use this when the files and Docker are on the same machine; for another machine, use [SFTP](#index-files-on-another-machine-over-sftp). `docker-compose.yml` ships the lines you need, commented out, so the default setup stays as it is until you turn them on.

1. In `docker-compose.yml`, under the `web` service's `volumes:`, uncomment this line and change the left side to your folder. Keep `/mnt/host` and `:ro`. Windows paths use forward slashes; on Linux or macOS the line looks like `- /home/you:/mnt/host:ro`.

   ```yaml
         - C:/Users/you:/mnt/host:ro
   ```

2. Under the `web` service's `environment:`, uncomment the line that limits filesystem connections to the same path:

   ```yaml
         Sources__Security__AllowedFilesystemRoots__0: /mnt/host
   ```

3. Run `docker compose up -d`, which recreates the `web` container with the mount.
4. Follow the steps above with `/mnt/host` as the **Allowed root**, and pick a folder inside it as the **Sub-path**.

A few things to know:

- **Keep `:ro`.** It makes the folder read-only inside the container. Connapse never writes to a source, so you give nothing up, and neither a mistake nor a compromised server can change your files.
- **Keep the two paths the same.** With the allowed root set, saving a source on a connection whose root is anywhere else is refused, so each source is held inside the mount twice. Without it, any root is allowed and Connapse logs a warning.
- **Mount the folder you'll choose from.** Changing the mounted folder means editing the file and recreating the container; changing the sub-path doesn't. Mount wide enough that the sub-path is your day-to-day choice, but no wider: every source on a Filesystem connection can reach all of it.
- **On a Linux host, check permissions.** Connapse runs as user ID 1654 in the container, and that ID needs to be able to read the folder. A home directory that only its owner can open won't work.

The mount can't be added from the Connapse UI. A container's mounts are fixed when it's created, so Connapse could only change them by controlling Docker itself, which is equivalent to root on your machine. The mount is what stops a compromised Connapse reading your other files, so it stays a choice only you can make.

Running Connapse directly on the machine, rather than in a container, removes all of this: the Filesystem provider reads real paths with no mount.

## Index files on another machine over SFTP

The Filesystem provider can only see the server's own disk. In Docker, that disk isn't yours unless you [mount a folder into it](#reach-a-folder-on-this-machine-from-docker), and a hosted deployment has no shared disk at all. For any other folder on your own computer, a NAS or another server, use SFTP.

### Connect this computer

1. On **Connections**, choose **Connect this computer**.
2. Connapse generates a key pair and shows one command. Run it in an administrator terminal on the computer that holds the files. It installs and starts the SSH server if needed, opens TCP 22 to local subnets, and installs the key.
3. Paste what the command printed back into Connapse, then enter the folder you want to index.
4. Create a source on the new connection, as in steps 4 to 6 above.

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

Create a Filesystem connection, then a source on it. Both calls need an Administrator token with the `admin:sources` scope.

```bash
curl -X POST http://localhost:5001/api/v1/connections \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Knowledge share","provider":"Filesystem","config":{"allowedRoot":"/srv/knowledge"}}'

curl -X POST http://localhost:5001/api/sources \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Handbook","connectionId":"CONNECTION_ID","scope":{"type":"filesystem","subPath":"handbook","includePatterns":["*.md"],"excludePatterns":[]}}'
```

The connection call returns the new connection under `connection`; put its `connection.id` in place of `CONNECTION_ID`.

An SFTP connection carries the private key in the request body. It's stored encrypted and never returned:

```json
{
  "name": "File server",
  "provider": "Sftp",
  "config": {
    "host": "files.example.com",
    "port": 22,
    "username": "connapse",
    "allowedRoot": "/srv/knowledge"
  },
  "privateKey": "-----BEGIN OPENSSH PRIVATE KEY-----\n...\n-----END OPENSSH PRIVATE KEY-----",
  "passphrase": null
}
```

The source's scope is `{"type":"sftp","subPath":"handbook"}`. Related calls:

| Task | Call |
|---|---|
| Test the connection | `POST /api/v1/connections/{id}/test` |
| Forget the pinned host key | `DELETE /api/v1/connections/{id}/host-key` |
| Replace the private key | `PUT /api/v1/connections/{id}/secret` with `privateKey` and `passphrase` |

## How Connapse keeps sources inside the root

- A source's sub-path is resolved under the connection's root, and Connapse checks that the result is still inside it, resolving links on every part of the path.
- For SFTP, the path is resolved on the remote server, because the root names a directory on that machine, not on the Connapse host.
- SFTP syncs step over symbolic links rather than following them, so a link to a folder outside the root can't pull that folder in.
- A folder that can't be read fails the whole SFTP sync, rather than looking as if every file in it was deleted.
- `Sources:Security:AllowedFilesystemRoots` applies to Filesystem roots only, not SFTP roots.

Two cases no path check can catch: bind mounts and hard links, which really are inside the root, and a path swapped out between Connapse's check and its read. Run the server under a low-privilege account, and keep its configuration and key ring outside every root you allow.

## Related

- [Containers, connections and sources](index.md)
- [Use uploads and MinIO](uploads-and-minio.md)
