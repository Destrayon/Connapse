# GitHub Setup

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse reads GitHub as a **GitHub App** that you create from Connapse in one step. Each
organisation or account you install the App on becomes a **connection**, and each repository you add
becomes up to two **sources**: its markdown docs, and its issues and pull requests. Reads use
hour-long installation tokens; no personal access token is pasted in or stored.

Public repositories are searchable by every Connapse user. A private repository's content appears
only for people who link their GitHub account under **Profile → Integrations** and whom GitHub lets
read that repository.

The App is set up on **Admin → Providers → GitHub** (`/admin/providers/github`).

## Prerequisites

| Need | Why |
|------|-----|
| A GitHub account, and, for an organisation-owned App, the organisation **owner** or **App manager** role | GitHub only lets those people create an App for an organisation |
| Connapse opened at the address your users use, served over **HTTPS** (or reached on `localhost`) | GitHub sends people back to the address the App was created from. Account linking over plain HTTP would send sign-ins unencrypted |
| Outbound HTTPS from the Connapse host to `github.com` and `api.github.com` | Every read and check goes there |

## Step 1 — The GitHub App

**What it is:** the identity Connapse reads GitHub as, and the one users sign in through to link their accounts.

### Easy setup (recommended)

1. Choose **Create the App on GitHub**. By default the App is owned by your own account; choose **Have an organisation own the App instead** and **Use this organisation** to put it on an organisation, so it outlives any one person's account.
2. GitHub shows the App's name and permissions. Confirm, and GitHub sends you back to Connapse, which stores the App.

The App is always public. That decides who may *install* it, so one App can serve several organisations; it says nothing about which repositories it reads. It asks for read-only access:

| Permission | Used for |
|------------|----------|
| Contents | Reading markdown docs over git |
| Issues | Indexing issues and their comments |
| Pull requests | Indexing pull requests and their comments |
| Metadata | Required by GitHub for every App |

### Manual values

For an App you registered yourself at [New GitHub App](https://github.com/settings/apps/new), or to rotate the stored App's private key or add its client secret. Register the App with the read-only permissions above and the two addresses the page shows:

| Setting on GitHub | Value |
|-------------------|-------|
| Callback URL | `https://<your Connapse>/api/v1/auth/cloud/github/callback` |
| Setup URL | `https://<your Connapse>/api/v1/providers/github/installed` (tick **Redirect on update**) |
| Webhook | Untick **Active**. Connapse polls, so it needs no webhook |

| Field | Required | Where to get it |
|-------|----------|-----------------|
| App ID | Yes | The number at the top of the App's page: GitHub **Settings → Developer settings → GitHub Apps → your App** |
| Private key (.pem file) | First save only | On the same page, under **Private keys**, choose **Generate a private key** and paste the whole downloaded file. Leave blank to keep the stored key |
| Client secret | For account linking | On the same page, under **Client secrets**, choose **Generate a new client secret**. Without it, people cannot link GitHub accounts, so private repositories stay hidden from everyone |

**Check and save** asks GitHub to confirm the App ID and key, and the client secret, before anything is stored. Entering a *different* App ID replaces the App, and every existing GitHub connection stops working, because each is an installation of the old App. The page asks you to confirm that first.

### Health

The card shows the App, its owner, whether account linking is available, when it was stored, and when GitHub last accepted its key. A key GitHub no longer accepts is reported as **Failed**; generate a new key and paste it under Manual values.

## Connection

On **Connections → Add connection**, provider **GitHub**, or choose **Add a GitHub connection** on the provider page.

| Field | Required | Notes |
|-------|----------|-------|
| Installation | Yes | Where the App is installed. **Install it on an organisation or account** goes to GitHub and brings you back with the new installation chosen |
| Name | Yes | Filled in as `github-<account>` |

**Test connection** checks each layer: the App's key is accepted, the installation still exists, and how many repositories it covers. **Configure on GitHub**, on an existing connection's edit form, opens the installation's settings, where you choose which repositories it covers.

## Source

On **Sources → New source**, pick the GitHub connection and enter the **Repository** as `owner/repo` or its github.com address; the field suggests the repositories the installation covers. Choose what to index:

- **Docs:** markdown files on the default branch. **File patterns** (optional) are file names to index, one per line, with `*` as a wildcard, such as `*.md` or `CHANGELOG*`. They match the file name, not its folder.
- **Issues and pull requests**, optionally with their comments. Comments from bots are left out by default; the source's edit page lets you include specific bots or leave out specific people.

A private repository is added as private automatically.

## Account linking

Each person links their own GitHub account under **Profile → Integrations → GitHub**. GitHub's sign-in page says the App can "act on your behalf"; Connapse only reads which account signed in, then revokes that access immediately. Only the account's id and login are stored. When someone searches, Connapse asks GitHub, as the installation, whether their account can read each private repository, and remembers the answer for five minutes.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| After **Create the App on GitHub**: *You don't have permission to create apps for this organisation* | You are not an owner or App manager of the organisation | Ask an owner to make you an App manager, or create the App on your own account |
| *That GitHub App setup was started too long ago or by someone else* | The setup was left open for over an hour, or finished in another browser | Start it again |
| *GitHub created the App, but Connapse could not collect its key* | The one-time code expired before Connapse used it | Delete that App on GitHub and start again |
| Check and save: *GitHub did not accept that App ID and private key together* | The key was generated for a different App, or the App ID is mistyped | Copy the App ID from the App's page and generate a new key there |
| Check and save: *That private key is not a readable .pem file* | Part of the file was pasted | Paste the whole file, including the `BEGIN` and `END` lines |
| Check and save: *GitHub did not accept that client secret* | The secret belongs to another App, or was revoked | Generate a new client secret on the App's page |
| Provider card: *GitHub no longer accepts the App's private key* | The key was deleted on GitHub | Generate a new key and paste it under Manual values |
| Provider card warns that people cannot link GitHub accounts | The App was entered by hand without its client secret | Add the client secret under Manual values |
| Connection test or list: *The GitHub App is no longer installed on …* | The installation was removed or suspended on GitHub | Install the App there again, or delete the connection |
| Connection test warning: *covers no repositories yet* | The installation was set to selected repositories with none chosen | Choose **Configure on GitHub** in the message and select repositories |
| New source: *This installation can't see owner/repo* | The repository is not among the installation's selected repositories, or the name is wrong | Choose **Configure on GitHub** from the message and add the repository |
| *GitHub could not be reached* | Outbound access to `api.github.com` is blocked | Check the server's network egress |
| Sources: status *Waiting*, with *GitHub's hourly request limit is used up until HH:mm UTC* | The installation spent its request budget | Nothing to do; syncing continues after that time |
| Sync error: *GitHub repository … can no longer be read* | The repository was renamed away, deleted, removed from the installation, or (for a public source) made private | Its documents are hidden. Restore access, or delete the source and add the repository again |
| Profile: *GitHub sign-in is not available* | No App yet, or an App without its client secret | An administrator finishes Step 1 |
| Profile: *That GitHub sign-in took too long or was started in another tab* | The sign-in expired | Start it again from the same tab |
| Profile: *started by a different Connapse user* | Someone else's browser finished the sign-in | Sign in to Connapse as yourself and start again |
| A linked user sees no content from a private repository | Their GitHub account cannot read it, or access changed in the last five minutes | Check their access on GitHub; wait five minutes after a change |

## Removing GitHub

1. Delete the GitHub sources, then the GitHub connections.
2. **Remove the App from Connapse** on the provider card (type the App's name to confirm). This deletes only Connapse's copy of the key.
3. Delete the App on GitHub under **Settings → Developer settings → GitHub Apps → your App → Advanced** if nothing else uses it.
