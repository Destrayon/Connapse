# Azure setup

Connapse reads Azure Blob Storage (including Data Lake Gen2 accounts) as its own Entra app
identity, authenticated with a certificate it generates on its host, or as a managed identity.
No account keys, SAS tokens, client secrets, or connection strings are ever entered or stored.
It only reads: it never writes a blob, a role assignment, an ACL, or a tag.

## Setup

Admin → Providers → Azure. Two steps, each with an **Easy setup** guide and **Manual values**.

1. **Access.** Copy the script, run it in [Azure Cloud Shell](https://shell.azure.com) (Bash),
   paste back what it prints, Save. It uses the subscription Cloud Shell is signed in to, creates
   two custom roles and the access app, and uploads only the public certificate:
   - *Connapse Blob Data + Tags Reader* — read blobs and blob index tags, list containers.
   - *Connapse RBAC Authorization Reader* — read role and deny assignments and storage-account
     metadata at subscription scope.
   Whoever runs it needs Owner or User Access Administrator on the subscription and permission
   to register applications. The script is safe to re-run: it finds existing apps by name and
   updates the roles in place.
2. **Per-user permissions.** A second script registers the sign-in app people link their Entra
   identity through, grants the access app the two Microsoft Graph permissions
   (`User.Read.All`, `GroupMember.Read.All`) it looks people and groups up with, and attempts
   admin consent. Consent needs a Global Administrator or Privileged Role Administrator; if the
   person running the script is not one, the page shows a consent link to send them. When
   consent succeeds the script also pre-consents the sign-in app for everyone, so nobody sees a
   consent prompt when linking.

Then Admin → Connections → New → Azure Blob Storage: choose the storage account and containers
from the lists (or type them), Test connection, Save. People link their Entra identity under
Integrations → Microsoft Entra ID; their search results are then limited to what that identity
may read.

## Troubleshooting

**`AADSTS700027: The certificate ... is not registered on application`** — the certificate
Connapse signs with is not the one uploaded to that app. Open the step's guide on the Azure
provider page (it embeds the certificate Connapse actually holds), re-run the script, paste back,
Save. Use **New certificate** only if you want to replace the key.

**`AADSTS500113: No reply address is registered`** on the admin-consent link — the access app
had no reply URL. Re-run the Per-user permissions script; it registers the Providers page as
the reply URL and the link works.

**`AADSTS65001` / "admin consent pending"** — the Graph permissions have not been approved.
Send the consent link from the Per-user permissions card to a Global Administrator.

**"This identity may not list storage accounts / containers"** in the connection form — the
access identity lacks a listing role. Roles created before container/account listing was added
are upgraded by re-running the Access script; otherwise assign *Connapse Blob Data + Tags Reader*
on the account and *Connapse RBAC Authorization Reader* on the subscription. Typing the names
still works without the lists.

**Test connection fails with 403 / `AuthorizationPermissionMismatch`** — the access app has no
blob-read role on that account or container. Use the "Role assignments granting exactly this
access" snippet on the connection form, or assign *Connapse Blob Data + Tags Reader* at the
account scope. Role assignments can take a few minutes to propagate.

**Test connection fails with 404** — the container name is wrong, or the account is not the one
you think (check the resource group in the account picker). Azure accounts have no default
container; the test needs one that exists.

**Search returns nothing for a linked user** — per-user filtering is on and the identity lookup
failed closed. Check that admin consent was granted, that the subscription is set on the Access
step (the RBAC resolver needs it), and that the user still exists and is enabled in Entra. For
Data Lake Gen2 accounts, the user also needs Read on the file and Execute on every ancestor
directory in the POSIX ACLs.

**"Could not connect your Entra identity"** — the container log names the cause:
`docker logs connapse-web-1 | grep -A5 "Entra sign-in callback failed"`.
