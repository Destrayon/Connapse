---
title: Azure setup
type: how-to
audience: admin
---

# Azure setup

Connapse reads Azure Blob Storage, including Data Lake Gen2 accounts, as its own identity in Microsoft Entra ID (Azure's directory service). That identity is an app registration with a certificate Connapse generates on its host, or the managed identity of the Azure host Connapse runs on. You never enter or store account keys, SAS tokens, client secrets or connection strings. Connapse only reads: it never writes a blob, a role assignment, an access control list or a tag.

Everything below is set up under **Admin** → **Providers** → **Azure**. Then a connection names the storage account and the containers it may use, and a source picks one to index.

## What gets indexed

Each source indexes the blobs in one container, optionally under a prefix. Connapse reads each blob's content as a file, using the same parsers as uploads. Blob index tags are read at search time for per-user filtering, not indexed as content.

## How permissions work

When per-user permissions are set up, each person links their Entra identity on **Profile** → **Integrations**, under **Microsoft Entra ID**. Their search results are then limited to the blobs that identity may read in Azure:

- Azure role assignments (RBAC) and deny assignments on the account, container and subscription.
- Role conditions on blob index tags (attribute-based access control, or ABAC), checked against each result's tags.
- On Data Lake Gen2 accounts, the POSIX access control lists: Read on the file and Execute on every folder above it.

Filtering happens at search time, against Azure, with short caches. Who a person is and which groups they're in, and their role and deny assignments, are cached for 5 minutes. On Gen2 accounts, folder lookups are cached for 60 seconds. Blob tags and Gen2 file ACLs are read for every result. So a grant or a revocation in Azure takes effect within 5 minutes.

A person who hasn't linked an identity gets no Azure results. A lookup that fails hides the results rather than showing them.

## Before you begin

| Need | Why |
|---|---|
| An Azure subscription, and someone who can open [Azure Cloud Shell](https://shell.azure.com) (Bash) in it | Each guided step is a script you run there |
| Owner or User Access Administrator on the subscription, and permission to register applications | The Access script creates two custom roles and an app registration |
| A Global Administrator or Privileged Role Administrator, for per-user permissions | Someone in one of those roles must grant the Microsoft Graph permissions |
| Connapse reached at the address people will sign in on | The sign-in app's redirect URI is built from it |

## 1. Access

The identity Connapse reads storage with.

### Easy setup

1. Copy the script from the **Access** card and run it in Azure Cloud Shell (Bash). It uses the subscription Cloud Shell is signed in to, creates the access app and two custom roles, and uploads only the public certificate:
   - *Connapse Blob Data + Tags Reader*: read blobs and blob index tags, and list containers.
   - *Connapse RBAC Authorization Reader*: read role assignments, deny assignments and storage account details across the subscription.
2. Paste back what the script prints and choose **Save**. Connapse asks Entra for a token with the new certificate before it stores anything.

The script is safe to run again. It reuses the app Connapse recorded, never one it finds by name, and updates the roles in place.

### Connapse running on Azure

If the Connapse host is an Azure VM, App Service or container with a managed identity, the **Access** guide detects it and uses it instead: no app registration and no certificate. The script creates the same two roles and assigns them to that identity, and the pasted result records the tenant, subscription and identity. Whoever runs it needs only Owner or User Access Administrator on the subscription.

Detection looks for the host's system-assigned identity. If the host has only a user-assigned identity, enter it under **Manual values** by its client ID. A link under the guide switches back to a certificate app if you prefer one.

### Manual values

Use these when the identity already exists. Nothing here is a secret except an optional PFX password.

| Field | Where to find it |
|---|---|
| Directory (tenant) ID | Entra admin center → **Overview** |
| How Connapse signs in | *App registration with a certificate*, *This host's managed identity* (nothing else to enter), or *A user-assigned managed identity* (named by its client ID) |
| Application (client) ID | Entra admin center → **App registrations** → the app → **Overview**. Certificate app only |
| Client certificate path | A path on the Connapse host to a PEM (certificate and private key) or a PFX. Its public certificate must be uploaded under the app's **Certificates & secrets**. Certificate app only |
| Certificate password | Optional, for a password-protected PFX. Never shown again; leave blank to keep the stored one |
| Managed identity client ID | Azure portal → **Managed Identities** → the identity → **Overview** → **Client ID**. User-assigned identity only |
| Subscription ID | Optional. Azure portal → **Subscriptions**. Needed for per-user permissions and for the storage account list on the connection form |

The identity needs a role that can read blobs on each storage account: Storage Blob Data Reader, or *Connapse Blob Data + Tags Reader*. Per-user permissions also need *Connapse RBAC Authorization Reader* (or Reader) on the subscription, and the Graph permissions in step 2.

### Check and renew access

**Recheck** asks Entra for a token as Connapse's identity. *Ready* means Entra accepted the credential; it doesn't prove any role is assigned, which **Test connection** on a connection does. *Failed* quotes Entra's `AADSTS` reason. *Unconfirmed* means Azure couldn't be reached.

Certificates Connapse generates are valid for one year, and the card warns 30 days before one expires. To renew, open the guide, choose **New certificate**, run the script it shows, paste back and save. The script registers the new certificate beside the old one, and the old one keeps working until you save. Private keys are stored only under `appdata/azure` on the Connapse host, readable only by Connapse's own user. A save that Entra refuses leaves the working setup untouched.

### Do it with the API

`PUT /api/v1/providers/azure/access` needs an Administrator token with the `admin:settings` scope. Entra is asked for a token first, and nothing is saved unless it gives one.

```json
{
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "subscriptionId": "66666666-7777-8888-9999-000000000000",
  "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
  "clientCertificatePath": "/app/appdata/azure/connapse-access.pem",
  "useHostManagedIdentity": false
}
```

For the host's managed identity, send `tenantId`, `subscriptionId` and `"useHostManagedIdentity": true`. For a user-assigned identity, send `userAssignedManagedIdentityClientId` instead of `clientId`. `GET /api/v1/providers` shows what each provider has set up.

## 2. Per-user permissions

The Entra app people sign in through, so Connapse knows which Entra identity each person is.

### Easy setup

1. Run the script from the **Per-user permissions** card in Cloud Shell. It registers the sign-in app and grants the access identity two read-only Microsoft Graph permissions, `User.Read.All` and `GroupMember.Read.All`, which let it look up who a person is and which groups they're in.
2. If you're a Global Administrator or Privileged Role Administrator, the script also grants admin consent and pre-consents the sign-in app for everyone, so nobody sees a consent prompt when they link. If you aren't, the card shows a consent link to send to someone who is.
3. Paste back what the script prints and choose **Save**.

Saving the sign-in app turns per-user filtering on for Azure, and it can't be turned off again.

### Graph permissions for a managed identity

A managed identity has no app registration to consent to, so its two Graph permissions are app role assignments on its service principal. The guided script makes them. When it can't, because the person running it isn't a Global Administrator or Privileged Role Administrator, or because the identity was entered by hand, a directory administrator runs these commands in Cloud Shell. Use the identity's **Object (principal) ID** from its **Overview** page in the Azure portal, or from the host's **Identity** page for a host's own identity:

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

"Permission being assigned already exists" means that grant was already in place. Once both are made, choose **The permissions have been granted** on the **Per-user permissions** card, or, for a hand-entered identity, set up the sign-in app under **Manual values**.

### Manual values

| Field | Where to find it |
|---|---|
| Directory (tenant) ID | Filled in from step 1. Change it only if the sign-in app is in another tenant |
| Application (client) ID | Entra admin center → **App registrations** → the sign-in app → **Overview** |
| Redirect URI | Filled in from the address you reached Connapse on, plus `/api/v1/auth/cloud/azure/callback`. Register the same value as a Web redirect URI on the app's **Authentication** page |
| Client certificate path | A path on the Connapse host to the sign-in app's PEM or PFX. Its public certificate goes under that app's **Certificates & secrets** |
| Certificate password | Optional, PFX only |

### Do it with the API

`PUT /api/v1/providers/azure/permissions` takes the sign-in app and needs the `admin:settings` scope:

```json
{
  "tenantId": "11111111-2222-3333-4444-555555555555",
  "clientId": "ffffffff-1111-2222-3333-444444444444",
  "redirectUri": "https://connapse.example.com/api/v1/auth/cloud/azure/callback",
  "clientCertificatePath": "/app/appdata/azure/connapse-signin.pem"
}
```

Admin consent needs a browser. While the response has `adminConsentPending` set to `true`, open its `adminConsentUrl`.

## 3. Connection

On **Connections** → **Add connection**, choose **Azure Blob Storage**.

| Field | Notes |
|---|---|
| Name | Filled in from the first container you choose if left blank |
| Storage account name | Pick from the accounts Connapse can see, or type it. The list needs the Subscription ID from step 1 and *Connapse RBAC Authorization Reader* (or Reader) on the subscription |
| Blob endpoint | Optional. Only for Azurite or another emulator; otherwise the account's public endpoint is used |
| Allowed locations | The containers, optionally `container/prefix`, that sources on this connection may name. Pick from the list or type them |
| Container to test against | Not saved. The first allowed location when blank |

Choose **Test connection**, then **Save**.

### Do it with the API

`POST /api/v1/connections` needs an Administrator token with the `admin:sources` scope:

```json
{
  "name": "Contoso docs",
  "provider": "AzureBlob",
  "config": {
    "accountName": "contosodocs",
    "allowedLocations": ["handbook", "policies/public/"]
  }
}
```

Test it with `POST /api/v1/connections/{id}/test`. `GET /api/v1/connections/{id}/discover` lists the containers it can reach.

## 4. Source

On **Sources** → **New source**, pick the connection, then a container from the ones it allows. **Prefix** narrows the source to a folder. Name is filled in from the container if left blank.

### Do it with the API

`POST /api/sources` needs the `admin:sources` scope:

```json
{
  "name": "Handbook",
  "connectionId": "CONNECTION_ID",
  "scope": { "type": "azureBlob", "containerName": "handbook", "prefix": "current/" }
}
```

## Sync behaviour

Each source is checked every 5 minutes by default, or at its `syncIntervalSeconds`. A blob is reindexed when its last-modified time or size changes, and a blob gone from the listing is removed, subject to the [deletion guard](index.md#deletions-are-guarded). Permission changes in Azure don't need a sync: they're read at search time.

## Limitations

- Only Blob Storage and Data Lake Gen2 are read. Azure Files, queues and tables aren't.
- Detection of a host's managed identity finds only a system-assigned identity. Enter a user-assigned one by hand.
- A file whose own Gen2 ACL is broader than its folder's is found by relevance like any other result and checked when it's returned. Connapse never skips a file's own check because its folder is readable.

## Troubleshooting

### Access card shows Failed

Entra refused Connapse's credential, and the card quotes the `AADSTS` code. For `AADSTS700027` (certificate not registered on the app) or an expired certificate, open the guide, choose **New certificate**, run the script, paste back and save. For `AADSTS700016` (app not found), the app was deleted: choose **Reset access** and set it up again.

### Access card shows Failed: cannot be used from this host

The certificate file is missing or unreadable, the settings are only partly filled in, or access is set to a managed identity that this host doesn't have. Fix the file or the identity, or set access up again.

### Access card shows Unconfirmed

Azure couldn't be reached from the server. Check outbound access to `login.microsoftonline.com`, then choose **Recheck**.

### Per-user permissions card shows Failed

Entra refused the sign-in app's certificate, and the card quotes the reason. Open the card's guide, choose **New certificate**, run the script, paste back and save.

### Entra refused the credential, so nothing was changed

The certificate or identity you tried to save doesn't work, and the current one was kept. Entra can take up to a minute to register a new certificate, so wait and save again. If it still fails, the certificate on the app isn't the one Connapse holds: run the command the page shows now.

### AADSTS700027: The certificate is not registered on application

The certificate Connapse signs with isn't the one uploaded to the app. Open the step's guide, which includes the certificate Connapse holds, run the script again, paste back and save. Choose **New certificate** only if you want to replace the key.

### AADSTS500113: No reply address is registered

The admin consent link failed because the access app has no reply URL. Run the **Per-user permissions** script again; it registers the Providers page as the reply URL.

### AADSTS65001: admin consent pending

The Graph permissions haven't been approved. Send the consent link from the **Per-user permissions** card to a Global Administrator.

### This identity may not list storage accounts or containers

The access identity lacks a listing role. Run the **Access** script again to upgrade the roles, or assign *Connapse Blob Data + Tags Reader* on the account and *Connapse RBAC Authorization Reader* on the subscription. Typing the names still works without the lists.

### Could not reach Entra to sign Connapse in

The test got no answer from Entra, so the credential wasn't judged. Check outbound access, then test again.

### Entra rejected Connapse's identity

This is the same fault as a Failed **Access** card. Fix it there, not on the connection.

### Not allowed to list container

The test got a 403 `AuthorizationPermissionMismatch`: the access identity has no blob-read role on that account or container. Use the **Role assignments granting exactly this access** snippet on the connection form, or assign *Connapse Blob Data + Tags Reader* on the account. Role assignments can take a few minutes to apply.

### No container exists

The test got a 404: the container name is wrong, or the account isn't the one you meant (check the resource group in the account list). Azure accounts have no default container, so the test needs one that exists.

### Could not reach the account

The account name doesn't resolve. Check the spelling, and the blob endpoint if you set one.

### Search returns nothing for a linked user

Per-user filtering is on and the identity lookup failed, so results were hidden. Check that admin consent was granted, that the subscription is set in step 1, and that the user still exists and is enabled in Entra. On Gen2 accounts, the user also needs Read on the file and Execute on every folder above it.

### Could not connect your Entra identity

Linking failed. The web app's log names the cause:

```bash
docker compose logs web | grep -A5 "Entra sign-in callback failed"
```

A redirect mismatch means the Redirect URI on the **Per-user permissions** card differs from the one registered on the sign-in app.

## Removing Azure

1. Delete the Azure sources, then the Azure connections, on the **Sources** and **Connections** pages.
2. On the **Per-user permissions** card, choose **Reset sign-in application**.
3. On the **Access** card, choose **Reset access**. This clears the tenant, subscription and identity and deletes the stored certificate. Azure connections stop working until access is set up again.
4. In Azure, delete the access app and the sign-in app, and remove the two custom roles and their assignments, if nothing else uses them. Connapse never removes anything in Azure itself.

With the API, `DELETE /api/v1/providers/azure/permissions` and `DELETE /api/v1/providers/azure/access` do steps 2 and 3.
