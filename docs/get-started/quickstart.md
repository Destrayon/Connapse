---
title: Quickstart
type: quickstart
audience: user
---

# Quickstart

In about fifteen minutes you'll have Connapse running on your machine, with one document uploaded and a search that returns a cited answer from it.

## Before you begin

- Docker 24 or later, with the Compose plugin (`docker compose version` prints 2.20 or later).
- Git.
- About 8 GB of free memory and 20 GB of free disk. The web app, PostgreSQL, MinIO and Ollama together use about 8 GB at their limits.
- Port 5001 free on your machine.
- Internet access for the first start, to download the images and the embedding model.

## 1. Get the code

```bash
git clone https://github.com/Destrayon/Connapse.git
cd Connapse
```

You should see: a `Connapse` folder containing `docker-compose.yml` and `.env.example`.

## 2. Create your environment file

```bash
cp .env.example .env
```

The defaults in `.env` work on your own machine. Change every password in it before anyone else can reach the server; [Run Connapse in production](../self-host/production.md) explains what to set.

You should see: a new `.env` file next to `.env.example`.

## 3. Start Connapse with Ollama

Ollama runs the embedding model that turns your documents into vectors, so this quickstart starts it too.

```bash
docker compose --profile with-ollama up -d
```

The first start builds the web image from source and takes several minutes. Database migrations run automatically when the web app starts.

You should see: `docker compose ps` lists `postgres` and `minio` as `healthy`, and `web` and `ollama` as `running`.

## 4. Download the embedding model

```bash
docker compose exec ollama ollama pull nomic-embed-text
```

`nomic-embed-text` is the default embedding model. Connapse doesn't download it for you.

You should see: `success` as the last line of output.

## 5. Check that Connapse is up

```bash
curl -i http://localhost:5001/health
```

You should see: `HTTP/1.1 200 OK`.

## 6. Create the owner account

Open <http://localhost:5001>. A new server has no accounts, so it shows the setup form. Enter your email, a display name and a password, then submit the form.

The first account becomes the **Owner**, the role with full control of the server. Nobody can register without an invite after this, so other people join through [invites](../guides/users-and-invites.md).

You should see: the **Containers** page, which is **Files** in the navigation, signed in as you.

## 7. Upload a document

1. On the **Containers** page, choose **New Container**.
2. Enter `my-notes` as the name, then choose **Create**.
3. Open `my-notes`, choose **Upload** and pick a Markdown, text, PDF or Word file.

You should see: the file's status change from **Queued** to **Processing** to **Ready**. **Ready** means it is indexed and searchable.

## 8. Search it

1. Open **Search** in the navigation.
2. Choose `my-notes` in the container list.
3. Type a question your document answers, then choose **Search**.

You should see: matching passages from your document, each labelled with the file name it came from.

## You're done when

`docker compose ps` shows every service healthy or running, `/health` returns 200, and a search in `my-notes` returns a passage labelled with your file's name.

## Next steps

- [Connect an AI agent](connect-an-agent.md) so Claude can search and build containers for you.
- [Add a source](../sources/index.md) to index S3, Azure Blob, GitHub, Confluence or Jira without uploading.
- [Install options](../self-host/install.md) cover the reranker, GPUs and other embedding providers.
- [Run Connapse in production](../self-host/production.md) before you expose it to a network.
