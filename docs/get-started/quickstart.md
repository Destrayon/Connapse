---
title: Quickstart
description: Start Connapse with one Docker Compose command, create the Owner account, upload a document and search it.
type: quickstart
audience: user
---

# Quickstart

In about ten minutes you'll have Connapse running on your machine, with one document uploaded and a search that returns a cited answer from it.

## Before you begin

- Docker 24 or later, with the Compose plugin (`docker compose version` prints 2.34 or later). With an older Compose, follow [Install Connapse](../self-host/install.md) instead.
- About 8 GB of free memory and 20 GB of free disk. The web app, PostgreSQL, MinIO and Ollama together use about 8 GB at their limits.
- Port 5001 free on your machine.
- Internet access for the first start, to download the images and the embedding model.

## 1. Start Connapse

Run this from any folder:

```bash
docker compose -f oci://ghcr.io/destrayon/connapse:compose up -d -y
```

That's the whole setup: there's nothing to clone and no file to edit. The command downloads the prebuilt images and starts them. The first start makes random passwords for the database and file storage, keeps them in a Docker volume, and downloads the embedding model, which turns your documents into vectors.

You should see: `docker compose -p connapse ps` lists `postgres` and `minio` as `healthy`, and `web` and `ollama` as `running`.

Connapse now starts again on its own after your machine restarts, as long as Docker starts at boot. To keep it stopped, run `docker compose -p connapse stop`.

## 2. Wait for the model

```bash
curl -i http://localhost:5001/health/ready
```

You should see: `HTTP/1.1 200 OK`. While the model downloads, this answers `503` with how far it has got, and Connapse shows the same progress at the top of every page. You can carry on meanwhile: uploads wait for the model, and a search says it's still getting ready.

## 3. Create the owner account

Open <http://localhost:5001>. A new server has no accounts, so it shows the setup form. Enter your email, a display name and a password, then submit the form.

The first account becomes the **Owner**, the role with full control of the server. Nobody can register without an invite after this, so other people join through [invites](../guides/users-and-invites.md).

You should see: the **Containers** page, which is **Files** in the navigation, signed in as you.

## 4. Upload a document

1. On the **Containers** page, choose **New Container**.
2. Enter `my-notes` as the name, then choose **Create**.
3. Open `my-notes`, choose **Upload** and pick a Markdown, text, PDF or Word file. To bring in a whole folder, choose **Upload folder** instead.

You should see: the file's status change from **Queued** to **Processing** to **Ready**. **Ready** means it is indexed and searchable.

## 5. Search it

1. Open **Search** in the navigation.
2. Choose `my-notes` in the container list.
3. Type a question your document answers, then choose **Search**.

You should see: matching passages from your document, each labelled with the file name it came from.

## You're done when

`docker compose -p connapse ps` shows every service healthy or running, `/health/ready` returns 200, and a search in `my-notes` returns a passage labelled with your file's name.

## Next steps

- [Connect an AI agent](connect-an-agent.md) so Claude can search and build containers for you.
- [Add a source](../sources/index.md) to index S3, Azure Blob, GitHub, Confluence or Jira without uploading.
- [Install options](../self-host/install.md) cover the reranker, GPUs, other embedding providers and switching the local model off.
- [Run Connapse in production](../self-host/production.md) before you expose it to a network.
