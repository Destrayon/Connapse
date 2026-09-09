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
   (`User.Read.All`, `GroupMember.Read.All` — read-only lookups of who a person is and which
   groups they belong to) it looks people and groups up with, and attempts admin consent. Consent
   needs a Global Administrator or Privileged Role Administrator; if the person running the
   script is not one, the page shows a consent link to send them. When consent succeeds the
   script also pre-consents the sign-in app for everyone, so nobody sees a consent prompt when
   linking.

Then Admin → Connections → New → Azure Blob Storage: choose the storage account and containers
from the lists (or type them), Test connection, Save. People link their Entra identity under
Integrations → Microsoft Entra ID; their search results are then limited to what that identity
may read.

### What Recheck verifies

**Recheck** on the Access card asks Entra for a token as Connapse's identity. *Ready* means Entra
accepted the certificate (or issued a token to the managed identity); it does not prove any role
is assigned — **Test connection** on a connection does that. *Failed* means Entra refused the
credential and quotes the `AADSTS` reason; *Unconfirmed* means Azure could not be reached.

### Certificates

The certificates Connapse generates are valid for one year. Each card shows when its certificate
expires and warns 30 days ahead. To renew, open the card's guide, choose **New certificate**, run
the script it produces (which registers the new public certificate alongside the old one), paste
back, Save. The old certificate keeps working until you save, and the private keys are stored
only under `appdata/azure` on the Connapse host, readable by Connapse's own user.

Save is a check first: Connapse asks Entra for a token with the new certificate before it
replaces the one in use, and refuses to save anything Entra rejects — the working setup stays as
it was. The script also prints the thumbprint of the certificate it registered; if it differs
from the one the page holds (another tab made a new certificate in between), Save refuses and
asks you to run the command shown now. The same check runs when Manual values are saved.

## Manual values

Use these when the identity already exists. Every value is read from the Azure portal or the
Microsoft Entra admin center; nothing here is a secret except an optional PFX password.

### Access

| Field | Where to find it |
|---|---|
| Directory (tenant) ID | Entra admin center → Overview (the tenant overview). |
| How Connapse signs in | *App registration with a certificate* — an app you registered and uploaded a certificate to. *User-assigned managed identity* — an identity attached to the host Connapse runs on. |
| Application (client) ID | Entra admin center → App registrations → the app → Overview. Certificate app only. |
| Client certificate path | A path on the Connapse host to a PEM (certificate plus private key) or PFX. Its public certificate must be uploaded under the app's Certificates & secrets. Certificate app only. |
| Certificate password | Optional; only for a password-protected PFX. Never shown again once stored; leave blank to keep it. |
| Managed identity client ID | Azure portal → Managed Identities → the identity → Overview → Client ID. Managed identity only. A system-assigned identity cannot be selected here. |
| Subscription ID | Optional. Azure portal → Subscriptions. Needed for per-user permissions (role assignments are read from it) and for the storage-account list on the connection form. |

The identity needs a role that can read blobs on each storage account — Storage Blob Data
Reader, or the *Connapse Blob Data + Tags Reader* role the script creates. For per-user
permissions it also needs *Connapse RBAC Authorization Reader* (or Reader) on the subscription
and the two Graph permissions above, with admin consent.

### Per-user permissions (sign-in app)

| Field | Where to find it |
|---|---|
| Directory (tenant) ID | Filled in from the Access step. Change it only if the sign-in app lives in another tenant. |
| Application (client) ID | Entra admin center → App registrations → the sign-in app → Overview. |
| Redirect URI | Filled in from the address you reached Connapse on, plus `/api/v1/auth/cloud/azure/callback`. Register the same value under the app's Authentication page as a Web redirect URI. |
| Client certificate path | A path on the Connapse host to the sign-in app's PEM or PFX; its public certificate goes under that app's Certificates & secrets. |
| Certificate password | Optional; PFX only. |

### Connection

| Field | Notes |
|---|---|
| Name | Filled in from the first container you choose if left blank. |
| Storage account name | Pick from the accounts Connapse can see, or type it. Listing needs the Subscription ID on the Access step and *Connapse RBAC Authorization Reader* (or Reader) on the subscription. |
| Blob endpoint | Optional. Only to point at Azurite or another emulator; otherwise the account's public endpoint is used. |
| Allowed locations | The containers (optionally `container/prefix`) a source on this connection may name. Pick from the list or type them. |
| Container to test against | Not saved; the first allowed location when blank. |

## Troubleshooting

**Access card shows Failed** — Entra refused Connapse's credential; the card quotes the
`AADSTS` code. `AADSTS700027` (certificate not registered on the application) or an expired
certificate: open the guide, choose **New certificate**, run the script, paste back, Save.
`AADSTS700016` (application not found): the app was deleted — Reset access and set it up again.

**Access card shows Unconfirmed** — Azure could not be reached from this server, or, for a
managed identity, no identity is available on this host. Check outbound access to
`login.microsoftonline.com`, then Recheck. Test a connection to be sure.

**Per-user permissions card shows Failed** — Entra refused the sign-in application's
certificate; the card quotes the reason. Open the card's guide, choose **New certificate**, run
the script, paste back, Save.

**Save says "Entra refused the … credential, so nothing was changed"** — the certificate or
identity you are about to store does not work, and the current one was left in place. Right
after running a script, Entra can take up to a minute to register the certificate: wait, then
Save again. If it persists, the certificate on the app is not the one Connapse holds — re-run the
command shown on the page.

**Test connection: "Could not reach Entra to sign Connapse in"** — the request never got an
answer from Entra; the credential was not judged. Check outbound access, then try again.

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

**Test connection: "not allowed to list container"** (403, `AuthorizationPermissionMismatch`) —
the access app has no blob-read role on that account or container. Use the "Role assignments
granting exactly this access" snippet on the connection form, or assign *Connapse Blob Data +
Tags Reader* at the account scope. Role assignments can take a few minutes to propagate.

**Test connection: "no container ... exists"** (404) — the container name is wrong, or the
account is not the one you think (check the resource group in the account picker). Azure
accounts have no default container; the test needs one that exists.

**Test connection: "Entra rejected Connapse's identity"** — the same fault as a Failed Access
card; fix it there, not on the connection.

**Test connection: "Could not reach ..."** — the account name does not resolve. Check the
spelling, and the blob endpoint if one is set.

**Search returns nothing for a linked user** — per-user filtering is on and the identity lookup
failed closed. Check that admin consent was granted, that the subscription is set on the Access
step, and that the user still exists and is enabled in Entra. For Data Lake Gen2 accounts, the
user also needs Read on the file and Execute on every ancestor directory in the POSIX ACLs.

**"Could not connect your Entra identity"** — the container log names the cause:
`docker logs connapse-web-1 | grep -A5 "Entra sign-in callback failed"`. A redirect mismatch
means the Redirect URI on the Per-user permissions card differs from the one registered on the
sign-in app.
