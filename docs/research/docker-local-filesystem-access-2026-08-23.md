# How should Connapse in Docker reach files on the operator's own machine?

**Date:** 2026-08-23
**Status:** Complete
**Supersedes nothing.** Narrows a question [the earlier report](filesystem-ingestion-2026-08-23.md) answered under an assumption that no longer holds.

## Why this is a second report

The earlier research asked *"which pull protocol should Connapse speak?"* and recommended SFTP for the local case. Where the two disagree, that one still governs the phasing — S3 first, SFTP second, the Filesystem connector kept for single-host installs. This report only revisits how a container reaches the host's disk, and does not reorder anything. That framing had already ruled out mounting the host's disk into the container — brainstorming rejected bind mounts as "a security concern and poor UX" before any research began, so every option surveyed was a network protocol by construction.

The new constraint — **everything must be configurable in the UI** — makes that exclusion worth re-testing rather than inheriting. A protocol needs a server and a credential set up per connection, forever. A mount is configured once and then never again. Against a "no terminal" requirement those trade in opposite directions, so the answer can change.

## The hard constraint

**A container's filesystem namespace is fixed when the container is created.** Adding a bind mount to a running container means stopping it, removing it, and creating a new one with the full mount set ([docker/cli#5328](https://github.com/docker/cli/issues/5328), [moby#11105](https://github.com/moby/moby/issues/11105)). This is a property of how containers work, not a Docker limitation awaiting a feature.

**Docker Desktop does not expose host drives automatically.** Verified directly: `docker run --rm alpine ls /` on this machine shows no `/host_mnt` and no host paths. Drive sharing in Docker Desktop settings makes a path *mountable*, not mounted.

So no application running inside a container can grant itself access to a host path it was not given. Every solution is one of four families.

## The four families

### 1. Mount once at deployment, choose paths in the UI

Mount a broad host path read-only when the container is created; afterwards every source is configured in the UI within it.

This is what comparable self-hosted products do, and [Immich's external library flow](https://oneuptime.com/blog/post/2026-03-20-photo-gallery-portainer/view) is the closest match to Connapse's model: uncomment an optional bind mount, then add the path in the admin panel and scan. [Paperless-ngx](https://docs.paperless-ngx.com/setup/) is the same shape — the consumption directory is a compose volume, and everything else is application configuration.

It maps onto Connapse's existing split without inventing anything:

| Layer | Who sets it | Connapse concept |
|---|---|---|
| What the container can see at all | Deployment, once | the bind mount |
| Which roots an admin may name | `Sources:Security:AllowedFilesystemRoots` | connection `allowedRoot` |
| Which subtree gets indexed | Admin, in the UI, any number of times | source `subPath` |

**Cost:** one line in `docker-compose.yml` and a `docker compose up -d`. No new code, no credential of any kind, no server on the host.

### 2. Speak a network protocol to a server on the host

SFTP, SMB, or WebDAV. Nothing in the container's configuration changes; the host runs a server and Connapse authenticates to it.

Covered in depth by the earlier report. The relevant update is what is **already true on this machine** (all verified directly):

| | Status |
|---|---|
| `sshd` service | **Running**, automatic start |
| `Subsystem sftp` | **Configured** (`sftp-server.exe`) |
| `PubkeyAuthentication` | **yes** |
| `authorized_keys` | **Present**, both user and administrators paths |
| Port 22 from a container | **Open** via `host.docker.internal` |
| `LanmanServer` (SMB) | **Running**, shares `C$`, `D$`, `ADMIN$` |
| Port 445 from a container | **Open** |

SFTP is therefore about one step from working here: generate a key pair and add the public half to `authorized_keys`. [#392](https://github.com/Destrayon/Connapse/issues/392) removes the terminal from the first half of that.

**SMB is worse here despite being equally reachable.** The shares that exist are `C$` and `D$`, which are administrative shares — reaching them means storing a Windows *administrator* password. SMBLibrary is also NTLM-only and LGPL-3.0. Trading an SSH key scoped to one account for an administrator password is a clear downgrade.

### 3. Push from the host to Connapse

An agent, a sync tool, or the existing upload UI. The earlier report rejected a purpose-built agent on distribution and signing cost, and that stands.

Worth noting the honest zero-infrastructure member of this family: **Connapse already accepts uploads into managed containers through the UI.** That is fully UI-driven with nothing to install. It is copy-in rather than sync — files do not track changes on disk — but for a fixed corpus it is the shortest path that exists today.

### 4. Do not run Connapse in a container

If Connapse runs natively on the machine holding the files, the Filesystem connector works with real paths. No mount, no server, no credential, no key.

This deserves stating plainly because the entire problem is an artefact of the deployment choice. For a single-user local install it is the simplest answer available, and it is the one configuration where "point it at a folder" needs nothing else at all.

## The tempting answer, and why it is wrong

The obvious way to make mounts UI-configurable is to give Connapse the Docker socket so it can recreate its own container with new mounts.

**Do not do this under any circumstances.** Access to `/var/run/docker.sock` is [root-equivalent on the host](https://www.netdata.cloud/guides/docker/docker-socket-security/): anything that can talk to the daemon can create a container that mounts `/` and execute as root. Mounting the socket read-only does not help, because the restriction is on the filesystem handle and not on the API it exposes.

The mount boundary exists to stop a compromised Connapse reading arbitrary host files. Handing Connapse the ability to move that boundary hands it root on the machine — strictly worse than the exposure it was meant to bound. **This is the principled reason the mount stays a deployment concern: it is the one decision the application must not be able to make about itself.**

## Recommendation

**For the local case, mount once and read-only. For the remote case, SFTP.** They are not competitors; they answer different questions.

The "everything in the UI" requirement is satisfied in the sense that matters: **all recurring work is in the UI.** Adding a source, changing a subpath, adding a second folder inside the mount, excluding a pattern — every one of those is a UI action. What remains outside is a single line set once at install, and it is outside precisely because it is the security boundary.

Concretely, mount the profile rather than a whole drive:

```yaml
    volumes:
      - appdata:/app/appdata
      - ${CONNAPSE_HOST_ROOT}:/mnt/host:ro
```

`CONNAPSE_HOST_ROOT` is whatever the operator wants exposed — `C:/Users/alice`, `/home/alice`, `D:/archive`. It is deliberately not a default: the right answer is specific to the machine, and a default here would either be wrong or expose more than the operator meant.

`:ro` costs nothing, since Connapse never writes to a source, and removes an entire class of accident. Then set `Sources:Security:AllowedFilesystemRoots` to `/mnt/host` so the allowlist and the mount agree, and every connection is bounded twice.

**SFTP is still worth finishing**, and not as a consolation. It is the only option in the list that works when the files are on a machine Connapse does not share hardware with — a NAS, a work laptop, a hosted deployment — and on this machine it is one paste away from working.

## What Connapse should change

1. **Ship the bind mount commented out in `docker-compose.yml`**, with the target path and `:ro` already written, exactly as Immich does. This turns the one-time step from "know that this is possible and write it correctly" into "uncomment a line". Filed as [#393](https://github.com/Destrayon/Connapse/issues/393).
2. **Say this in `docs/connectors.md`.** The document currently explains that the Filesystem connector needs a shared disk without telling anyone how to arrange one.
3. **Finish [#392](https://github.com/Destrayon/Connapse/issues/392)** so the SFTP path needs no terminal either.

## Addendum — optimising for operator setup cost

The first pass asked what *works*. This pass asks what costs the operator least, which turns out to have a different answer, because the browser is a route none of the four families covers.

### The category has not solved this, and says so out loud

[AnythingLLM](https://docs.anythingllm.com/installation-docker/overview) is the closest comparable product, and it ships **two builds**: a desktop app — one-click, single-user, direct filesystem access, no configuration — and a Docker build for teams that adds roles and access control. The split is not accidental. Their Docker users have been asking for exactly this since at least [#1873](https://github.com/Mintplex-Labs/anything-llm/issues/1873) and [#4877](https://github.com/Mintplex-Labs/anything-llm/issues/4877) (watch folders), and the community answer is a [third-party Python sync script](https://github.com/dastra/anythingllm-document-sync).

The instructive one is [#3640](https://github.com/Mintplex-Labs/anything-llm/issues/3640): a user who **already mounted their NAS into the container** and still could not use it, because the UI offered no way to point at an existing directory. Their native sync feature watches individual files and [cannot watch a directory at all](https://docs.anythingllm.com/beta-preview/active-features/live-document-sync).

**Connapse is ahead of this, not behind it.** The connection/source split already expresses "here is a root, index this subtree of it" — the exact thing #3640 wanted and could not find. What is missing is only the mount, and the documentation for it.

### The route none of the four families covers: the browser

The container cannot see the host's disk, but **the operator's browser can**, and it is already talking to Connapse.

**`<input type="file" webkitdirectory>`** lets a user pick a whole folder from a native dialog; the browser then hands over every file beneath it, with the relative path on each as `webkitRelativePath`. It reached [Baseline in 2025](https://caniuse.com/input-file-directory) and works in Chrome, Edge, Firefox, and Safari 11.1+ — everything except iOS Safari. There is no permission API, no flag, and nothing to install.

**Setup cost: none.** Not "one line in a compose file" — actually none. The operator opens Connapse, clicks a button, picks a folder.

Connapse is most of the way there already and does not know it. `wwwroot/js/fileDrop.js` uploads via `fetch()` and `FormData`, which is the right shape — but it reads `e.dataTransfer.files`, so **dropping a folder does not work today**. Directory drops need `webkitGetAsEntry()` and a recursive walk; folder *picking* needs the attribute above. Both are small, and both feed the upload path that already exists.

**What it does not do is stay in sync.** This is an import, into a managed container, and refreshing means importing again. That is a real limitation and it is also the honest shape of the thing: once the bytes are uploaded, Connapse owns them, which is exactly what a container is. It suits "index my documents" and does not suit "mirror a folder that changes".

Practical ceiling: this is a browser upload, so a few thousand files is comfortable and a hundred thousand is not.

### The File System Access API, and why not

[`showDirectoryPicker()`](https://developer.chrome.com/docs/capabilities/web-apis/file-system-access) is the more powerful version — a directory handle that can be stored in IndexedDB and re-used, with [persistent permissions since Chrome 122](https://developer.chrome.com/blog/persistent-permissions-for-the-file-system-access-api) that survive a browser restart. It would allow genuine re-sync with no setup.

**It is the wrong fit anyway, for a reason that has nothing to do with browser support.** Connapse syncs from a background service on a timer. A directory handle only lives in a page — so a source backed by one can only reconcile while somebody has the tab open, and Chrome auto-revokes on tab backgrounding besides. A "source" that silently stops syncing when you close the tab is worse than an import that never claimed to.

It is also Chromium-only (no Firefox, no Safari), where `webkitdirectory` is Baseline. More work, narrower support, and a sync model that does not match the product.

### Revised recommendation

Three answers for three shapes of need, and the cheapest one covers most people:

| Need | Answer | Operator setup |
|---|---|---|
| "Get my documents in" | **Folder upload in the browser** | **None** |
| "Mirror a folder that keeps changing" | Bind mount, read-only | One line, once |
| "Files are on another machine" | SFTP | SSH server + key |
| Single machine, no Docker wanted | Native install | None; connector works as-is |

**Build the folder upload.** It is the smallest change of the four, it needs nothing from the user, it works in every desktop browser, and it reuses an upload path that already exists. Filed as [#394](https://github.com/Destrayon/Connapse/issues/394).

The mount stays the answer for continuous sync and should be [shipped commented-out](https://github.com/Destrayon/Connapse/issues/393) so it is an uncomment rather than a thing to know. SFTP stays the answer for another machine. And AnythingLLM's split is worth taking seriously as positioning: **Docker is the deployment for teams and servers; a single user indexing their own laptop has a simpler option, and Connapse should say so rather than making them fight the container.**

## What this does not solve

- **Files on a machine that is off, asleep, or behind NAT.** Unchanged from the earlier report, and still deliberately the operator's problem.
- **A mount is as broad as you make it.** Mounting `C:/` gives every source in Connapse a potential view of the whole drive, bounded only by `AllowedFilesystemRoots`. The narrower the mount, the less the allowlist has to carry.
- **Changing which folder is mounted still requires a container recreate.** Choosing a *sub*path does not. Mount broadly enough that the sub-path choice is the one you actually make.

## Verification log

Everything asserted about this machine was checked rather than assumed:

- `docker run --rm alpine ls /` — no host paths, no `/host_mnt`.
- `nc -z host.docker.internal 22 / 445 / 139` from a container — 22 open, 445 open, 139 closed.
- `Get-Service sshd, LanmanServer` — both Running, both Automatic.
- `Get-SmbShare` — `ADMIN$`, `C$`, `D$`, `IPC$`.
- `sshd_config` — `PubkeyAuthentication yes`, `Subsystem sftp sftp-server.exe`, `PasswordAuthentication yes`, administrators override present.
- `authorized_keys` present at both the user and administrators locations.

## Sources

- [docker/cli#5328](https://github.com/docker/cli/issues/5328), [moby#11105](https://github.com/moby/moby/issues/11105) — mounts cannot be added to a running container
- [Docker bind mounts documentation](https://docs.docker.com/engine/storage/bind-mounts.md)
- [Docker socket security](https://www.netdata.cloud/guides/docker/docker-socket-security/), [The Dangers of Docker.sock](https://raesene.github.io/blog/2016/03/06/The-Dangers-Of-Docker.sock/)
- [Paperless-ngx setup](https://docs.paperless-ngx.com/setup/), [Immich external libraries via Portainer](https://oneuptime.com/blog/post/2026-03-20-photo-gallery-portainer/view)
- [Earlier report: filesystem ingestion](filesystem-ingestion-2026-08-23.md) — protocol comparison, SMBLibrary licence and NTLM limits
