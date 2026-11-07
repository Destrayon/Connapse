---
title: Use uploads and MinIO
description: Upload files to Connapse's built-in MinIO storage, the default for a new container.
type: how-to
audience: admin
---

# Use uploads and MinIO

Use this when you want Connapse to hold the files itself: you upload them to a container, and Connapse stores them in its own object storage. Nothing outside Connapse needs to be set up. By default that storage is the MinIO service in `docker-compose.yml`, with every container's files in one bucket, `connapse-files`.

## Prerequisites

- An Editor or Administrator account. Viewers can search containers but not create or change them.
- A running Connapse server. See [Install Connapse](../self-host/install.md).

## Steps

1. On the **Containers** page, choose **New Container**.
2. Enter a name of 2 to 128 lowercase letters, digits and hyphens, such as `team-handbook`, then choose **Create**.
3. Open the container and choose **Upload**. Pick one or more files, or drag them onto the page. Each file can be up to 256 MiB.
4. Optional: create folders to organise the files. Folders are part of each file's path and can be used to narrow a search.

You should see: each file's status change from **Queued** to **Processing** to **Ready**. See [Supported file types](../reference/file-types.md) for what Connapse can read.

## Upload a folder

Open a container, go to the folder you want the upload in, then choose **Upload folder** and pick a folder, or drag a folder onto the page. Everything inside it uploads, subfolders included, and each file keeps its path: `project/docs/guide.md` uploaded at the top of a container becomes `/project/docs/guide.md`.

A panel above the file list counts the files as they go. You can keep working in Connapse while it runs, including in other containers, but closing or reloading the tab stops the upload. **Cancel** stops it too, and the files already uploaded stay.

- **A bad file doesn't stop the rest.** Each file that fails is listed with the reason when the upload ends, and **Download the list** gives you all of them.
- **Everything in the folder is attempted.** Files Connapse can't read are listed as failed, not skipped silently, so a folder with `.git` or `node_modules` in it lists a lot of them. Upload the folders you want rather than the whole project.
- **Some folder names can't be kept.** A folder whose name has a backslash in it, which Mac and Linux allow, is listed as failed. Connapse would read the backslash as a folder break, and the file could replace another one.
- **It's a one-time copy.** Connapse doesn't watch the folder, so later changes on your computer don't reach Connapse. Upload the folder again to replace the files with the same paths. To keep a repository or a bucket in step, [add a source](index.md).
- **Very large folders ask first.** Over 5,000 files, Connapse tells you the count and waits for you to confirm.
- **The usual limits apply to each file.** A folder has no size limit of its own, and one upload runs at a time.

**Upload folder** doesn't appear on iPhone or iPad, where browsers have no working folder picker.

If files land in the MinIO bucket some other way, for example copied in with the MinIO client, Connapse doesn't see them until the container is synced. A container's sync reconciles its stored files with its index; it doesn't contact any outside system.

## Point Connapse at other S3-compatible storage

Connapse reads its storage settings from `Knowledge:Storage:MinIO`. Any S3-compatible service works. Set these under `services.web.environment` in `docker-compose.override.yml`:

```yaml
services:
  web:
    environment:
      Knowledge__Storage__MinIO__Endpoint: "storage.example.com:9000"
      Knowledge__Storage__MinIO__AccessKey: "connapse"
      Knowledge__Storage__MinIO__SecretKey: "change-me"
      Knowledge__Storage__MinIO__UseSSL: "true"
      Knowledge__Storage__MinIO__BucketName: "connapse-files"
```

Connapse creates the bucket on first start if it doesn't exist. Changing the endpoint or bucket doesn't move files that are already stored: copy them across before you switch, or reupload them.

## Do it with the API

Creating containers and uploading needs a token with the `knowledge:write` scope.

```bash
curl -X POST http://localhost:5001/api/containers \
  -H "X-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"team-handbook","description":"Policies and onboarding"}'

curl -X POST http://localhost:5001/api/containers/CONTAINER_ID/files \
  -H "X-Api-Key: $TOKEN" \
  -F "files=@onboarding.md" -F "path=/onboarding"
```

To upload to several folders in one request, send `paths` with one folder per file, in the same order as `files`. A request takes at most 100 files, and each one is reported on its own, so one bad file doesn't fail the others:

```bash
curl -X POST http://localhost:5001/api/containers/CONTAINER_ID/files \
  -H "X-Api-Key: $TOKEN" \
  -F "files=@guide.md" -F "paths=/project/docs/" \
  -F "files=@notes.txt" -F "paths=/project/"
```

| Task | Call |
|---|---|
| Create a folder | `POST /api/containers/{containerId}/folders` with `{"path":"/onboarding"}` |
| List files | `GET /api/containers/{containerId}/files` |
| Delete a file | `DELETE /api/containers/{containerId}/files/{fileId}` |
| Sync stored files with the index | `POST /api/containers/{containerId}/sync` |
| Delete an empty container | `DELETE /api/containers/{containerId}` |

## Related

- [Containers, connections and sources](index.md)
- [Back up and restore Connapse](../self-host/backup-and-restore.md)
