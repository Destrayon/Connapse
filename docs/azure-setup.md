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
   to register applications. The script is safe to re-run: it reuses the app Connapse recorded
   (never one found by display name) and updates the roles in place.
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

### Connapse running on Azure

If the Connapse host is an Azure VM, App Service, or container with a managed identity, the
Access guide notices and switches to it: no app registration and no certificate. Its script
creates the same two custom roles and assigns them to that identity by object id, and the
paste-back records the identity (tenant, subscription, principal). Whoever runs it needs only
Owner or User Access Administrator on the subscription. A link under the guide switches back to
a certificate app if you prefer one; the manual form also offers "This host's managed identity"
as a choice, needing only the tenant. Detection asks for the host's system-assigned identity; a
host that has only user-assigned identities may report none — enter such an identity by hand
under Manual values, by its client ID.

The Per-user permissions guide then grants the identity its two Graph permissions as app roles
(a REST call per permission) instead of consenting an app. If the person running it is not a
Global Administrator or Privileged Role Administrator, the script reports that and the card
shows the two commands to hand to one; press "The permissions have been granted" once they have.

### Graph permissions for a managed identity

A managed identity has no app registration to consent to, so its two Microsoft Graph
permissions are app-role assignments on its service principal. The guided Per-user permissions
script makes them; when it cannot (the runner is not a Global Administrator or Privileged Role
Administrator), or when the access identity was entered by hand, a directory administrator runs
these in Cloud Shell, with the identity's **Object (principal) ID** from its Overview page in
the Azure portal (for a host's own identity, the host's **Identity** page):

```bash
GRAPH_API='00000003-0000-0000-c000-000000000000'
MI='<managed-identity-object-id>'
GRAPH_SP_ID=$(az ad sp show --id "$GRAPH_API" --query id -o tsv)
for ROLE in User.Read.All GroupMember.Read.All; do
  ROLE_ID=$(az ad sp show --id "$GRAPH_API" --query "appRoles[?value=='$ROLE'].id | [0]" -o tsv)
  az rest -m POST -u "https://graph.microsoft.com/v1.0/servicePrincipals/$MI/appRoleAssignments" \
    -b "{\"principalId\":\"$MI\",\"resourceId\":\"$GRAPH_SP_ID\",\"appRoleId\":\"$ROLE_ID\"}"
done
```

"Permission being assigned already exists" means that grant was already in place. Once both are
made, press **The permissions have been granted** on the Per-user permissions card (or, for a
hand-entered identity, set the sign-in application up under Manual values).

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

Save is a check first: Connapse writes the new certificate to a file of its own (named by its
thumbprint), asks Entra for a token with it, and only then commits settings naming that file.
The key in use is never moved or overwritten; it is removed only after the new settings are
stored, and anything Entra rejects is deleted again with the working setup untouched. The script
also prints the thumbprint of the certificate it registered; if it differs from the one the page
holds (another tab made a new certificate in between), Save refuses and asks you to run the
command shown now. The same Entra check runs when Manual values are saved.

Only Entra errors that mean the credential itself is wrong — certificate not registered
(`AADSTS700027`), keys expired (`AADSTS7000222`), app disabled (`AADSTS7000112`), app or tenant
not found (`AADSTS700016`, `AADSTS90002`), missing service principal (`AADSTS7000229`) — count as
a refusal. Throttling, transient faults, and outages read as *Unconfirmed* with a retry, never as
a demand to set up again.

Each save attempt writes its own file (`connapse-azure-access-<thumbprint>-<attempt>.pem`), so
attempts never share one. Old certificate files are removed only once nothing stored names them
and they are more than an hour old, so two Connapse processes sharing one volume cannot delete
each other's key; if the settings store cannot be read, nothing is deleted at all. Saving the
sign-in application switches per-user enforcement on *before* storing the application and
requires the switch to be live in this process; if it cannot be written or applied, the
application is not saved and the message says to restart and save again. The AWS sign-in save
carries the Azure switch through unchanged. If enforcement is somehow off while sign-in is
configured, the Per-user permissions card shows Failed and says how to switch it on. A save
whose outcome cannot be confirmed (the store failed after committing and could not be read back)
is reported as unconfirmed rather than as failed; check the card after a restart before retrying.

## Manual values

Use these when the identity already exists. Every value is read from the Azure portal or the
Microsoft Entra admin center; nothing here is a secret except an optional PFX password.

### Access

| Field | Where to find it |
|---|---|
| Directory (tenant) ID | Entra admin center → Overview (the tenant overview). |
| How Connapse signs in | *App registration with a certificate* — an app you registered and uploaded a certificate to. *This host's managed identity* — the system-assigned identity of the Azure host Connapse runs on; nothing else to enter. *A user-assigned managed identity* — an identity attached to the host, named by its client ID. |
| Application (client) ID | Entra admin center → App registrations → the app → Overview. Certificate app only. |
| Client certificate path | A path on the Connapse host to a PEM (certificate plus private key) or PFX. Its public certificate must be uploaded under the app's Certificates & secrets. Certificate app only. |
| Certificate password | Optional; only for a password-protected PFX. Never shown again once stored; leave blank to keep it. |
| Managed identity client ID | Azure portal → Managed Identities → the identity → Overview → Client ID. User-assigned identity only; the host's own identity needs no ID. |
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

**Access card shows Unconfirmed** — Azure could not be reached from this server. Check outbound
access to `login.microsoftonline.com`, then Recheck. Test a connection to be sure.

**Access card shows Failed: "cannot be used from this host"** — the certificate file is missing
or unreadable, the settings are only partly filled in, or Access is set to a managed identity and
this host has none (the identity was removed, or the settings came from another host). Fix the
file or the identity, or set access up again.

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
