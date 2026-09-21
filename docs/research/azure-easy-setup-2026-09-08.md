# Best "easy setup" (guided) flow for connecting Connapse to Azure
**Date:** 2026-09-08
**Status:** Reviewed
**Built on:** no prior corpus material (Connapse knowledge MCP not available this session)

## Executive summary
The recommended Azure "easy setup" is the direct analog of Connapse's AWS CloudShell flow: **Connapse generates keypairs locally, emits an `az`-CLI Bash script the operator pastes into Azure Cloud Shell, and parses a delimited block the script prints back** (tenant, subscription, and two client IDs). Use **two separate Entra app registrations** (a low-privilege OIDC *sign-in* app and a certificate-authenticated *access* app) and **two least-privilege custom RBAC roles** (blob+tags data reader; authorization reader) rather than the over-broad built-ins. The one step that **cannot be guaranteed automatable** is granting admin consent to the access app's Microsoft Graph *application* permissions (`User.Read.All`, `GroupMember.Read.All`): **only Global Administrator or Privileged Role Administrator can do it**. The flow must therefore attempt consent, treat "insufficient privileges" as an expected branch, and fall back to surfacing a ready-to-send v1 `adminconsent` URL while marking per-user permissions "pending admin consent." Bicep/ARM is a non-starter (it cannot create Entra apps, service principals, or Graph grants).

## Research brief
**Question:** What is the best way to implement a low-friction guided ("easy") Azure setup for Connapse (self-hosted .NET), analogous to its AWS CloudShell + IAM Roles Anywhere flow, and what genuinely cannot be automated?

**Sub-questions investigated:**
1. Scripting approach & host (`az`/Cloud Shell vs Bicep vs Graph PowerShell vs portal) + the exact command sequence.
2. The admin-consent constraint: exact role requirements, the out-of-band consent URL, graceful degradation.
3. Identity & Azure RBAC model: one app vs two; built-in vs custom roles for blob+tags and for the RBAC read.
4. Certificate handling, the paste-back handshake values, and common tenant blockers.

**Out of scope:** the Blazor UI implementation itself; AWS specifics; non-Azure clouds.

**Success criteria:** a concrete, implementable recommendation — approach + actual scripts/commands + how to handle the non-automatable admin-consent step + the recommended UX — grounded in current (2026) Azure/Entra behavior with citations.

## Findings by sub-question

### Sub-question 1 — Scripting approach & host: Azure Cloud Shell + discrete `az` commands
**Claim:** Emit an `az`-CLI Bash script for Azure Cloud Shell; do not use Bicep, and do not use the `create-for-rbac` combo command.

Azure Cloud Shell is the correct AWS-CloudShell analog: it is **pre-authenticated** ("Cloud Shell automatically securely authenticates for instant access to your resources through the Azure CLI" — [Cloud Shell overview](https://learn.microsoft.com/en-us/azure/cloud-shell/overview)) and runs **as the signed-in operator**, so its permissions match whatever rights the operator already holds — the same trust model as AWS CloudShell. **Bicep/ARM cannot create Entra app registrations, service principals, or Graph permission grants at all** — those are Microsoft Graph/Entra objects, not ARM resources — so Bicep is disqualified. `az ad sp create-for-rbac` can bundle app+SP+cert+role in one call but **cannot add Graph API permissions**, so the discrete command sequence is required regardless. Microsoft Graph PowerShell is workable but more verbose; portal click-through fails the "scriptable/low-friction" bar.

Cloud Shell limitations to design around: it requires a one-time Azure Files (5 GB) mount prompt on first use, and times out after ~20 min of inactivity.

**Verified command sequence** (current `az` syntax, Microsoft Learn CLI reference):
```bash
APP_ID=$(az ad app create --display-name "Connapse-Azure-Access" --query appId -o tsv)
az ad app credential reset --id "$APP_ID" --cert "@connapse-access.pem" --append   # PUBLIC cert only
az ad sp create --id "$APP_ID"
az role assignment create --assignee "$APP_ID" --role "<data role>"  --scope "<storage account scope>"
az role assignment create --assignee "$APP_ID" --role "<rbac-read role>" --scope "/subscriptions/<sub>"
az ad app permission add --id "$APP_ID" --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions a154be20-db9c-4678-8ab7-66f6cc099a59=Role   # User.Read.All
az ad app permission add --id "$APP_ID" --api 00000003-0000-0000-c000-000000000000 \
  --api-permissions 98830695-27a2-44f7-8c18-0c3ebc9698f6=Role   # GroupMember.Read.All
az ad app permission admin-consent --id "$APP_ID"               # may fail — see sub-question 2
```
**Corrections vs. common/older guidance** ([az ad app credential](https://learn.microsoft.com/en-us/cli/azure/ad/app/credential)): `az ad app credential add` does not exist — `az ad app credential reset` (with `--append`) is the unified command. `az ad app permission grant` activates **delegated** (`Scope`) permissions only; **application** (`Role`) permissions like these require `az ad app permission admin-consent`, a different command.

*Confidence:* Commands and Cloud Shell pre-auth are **primary-source (Microsoft Learn)**. The two Graph app-role GUIDs were confirmed via a widely-cited third-party reference (graphpermissions.merill.net), **not** Microsoft directly — **verify live at build time** with `az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?value=='User.Read.All'||value=='GroupMember.Read.All'].{v:value,id:id}"`.

### Sub-question 2 — Admin consent is the one hard, non-automatable gate
**Claim:** Only Global Administrator or Privileged Role Administrator can consent to the access app's Microsoft Graph *application* permissions; the flow must degrade gracefully.

Per Microsoft's Graph permissions doc, **"Only Privileged Role Administrator and Global Administrator can consent to application permissions"** ([Permissions overview](https://learn.microsoft.com/en-us/graph/permissions-overview)). The Entra "Grant tenant-wide admin consent" prerequisites make the split explicit: **Privileged Role Administrator** can consent to any permission for any API; **Application Administrator / Cloud Application Administrator** can consent to any permission **except Microsoft Graph app roles (application permissions)** ([Grant tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent)). Global Administrator is a superset. So an operator who can *create* the app (Application/Cloud App Admin, or any member if the tenant allows self-service registration) **still cannot consent** to `User.Read.All`/`GroupMember.Read.All` unless they are Global Admin or Privileged Role Admin.

Adjacent role requirements: **assigning Azure RBAC roles** (`Microsoft.Authorization/roleAssignments/write`) needs **Owner, User Access Administrator, or Role Based Access Control Administrator** at the target scope ([RBAC troubleshooting](https://learn.microsoft.com/en-us/azure/role-based-access-control/troubleshooting)) — Azure RBAC, distinct from Entra roles. **Creating custom roles** (`roleDefinitions/write`) needs the same Owner/User Access Admin level.

**Out-of-band consent URL (verified, v1):**
```
https://login.microsoftonline.com/{tenant}/adminconsent?client_id={access-app-id}
```
`{tenant}` = tenant ID / a verified domain / the literal `organizations`. This grants **all permissions already configured** on the app's API-permissions blade — no scope enumeration, no redirect URI required ([Grant tenant-wide admin consent](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent)). The v2 form (`/v2.0/adminconsent?...&scope=https://graph.microsoft.com/.default&redirect_uri=...`) requires a registered redirect URI and is unnecessary here.

**Recommended graceful degradation:** the script attempts `az ad app permission admin-consent` right after adding the permissions; treat `AADSTS650052`/"Insufficient privileges" as a **non-fatal expected branch**. On that branch: finish everything else (app + SP + RBAC assignments + emit the paste-back block), and have Connapse render the v1 consent URL with plain instructions ("send this to a Global Administrator or Privileged Role Administrator; they click Accept"). Connapse marks per-user Azure permissions **"pending admin consent"** (not "failed") with a re-check button, since consent completes out-of-band and takes a few minutes to propagate. This mirrors Microsoft's own SaaS deferred-admin-consent onboarding pattern.

*Confidence:* **Primary-source (Microsoft Learn)** for the role split, the URL, and propagation delay.

### Sub-question 3 — Two apps, and two custom least-privilege roles
**Claim:** Use separate sign-in and access app registrations; build custom RBAC roles rather than the over-broad built-ins.

**Two app registrations, not one.** Microsoft's Zero Trust guidance is explicit: "Use separate app registrations for apps that sign in users and apps that expose data and operations via API," to keep high-privilege API permissions and credentials "at a distance from user-facing components" ([Register applications – Zero Trust](https://learn.microsoft.com/en-us/security/zero-trust/develop/app-registration)). A combined app would make the public, every-user OIDC sign-in flow carry the same identity that holds subscription-wide reads and Graph app permissions. Separate apps also give independent credential rotation, independent consent, and clean audit separation (sign-ins vs. service reads under different app IDs).

**Blob + tags data role — the built-in is insufficient; use a custom role.** `Microsoft.Storage/.../blobs/read` and `Microsoft.Storage/.../blobs/tags/read` are **distinct** actions in the Azure permissions reference, and **Storage Blob Data Reader's `dataActions` contains only `blobs/read`, not `blobs/tags/read`** ([storage permissions](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/storage)). Storage Blob Data Owner/Contributor would add write/delete (far broader). A custom data role is the least-privilege fit — and it directly closes the `blobs/tags/read` prerequisite that Connapse's tag-ABAC verification needs (repo issue #498):
```json
{
  "Name": "Connapse Blob Data + Tags Reader",
  "IsCustom": true,
  "Description": "Read blob content and blob index tags for ABAC-conditioned access verification.",
  "Actions": [], "NotActions": [], "NotDataActions": [],
  "DataActions": [
    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read",
    "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/tags/read"
  ],
  "AssignableScopes": ["/subscriptions/{subscriptionId}"]
}
```
Assign it at the storage-account (or container) scope; `AssignableScopes` can be narrowed to the specific account(s) if that fits Connapse's per-container model.

**RBAC-read role — custom is more correct than built-in Reader.** Both `Microsoft.Authorization/roleAssignments/read` and `Microsoft.Authorization/denyAssignments/read` are confirmed read-only control-plane actions. Built-in **Reader** grants `*/read` across *every* resource type in scope (VMs, networking, SQL metadata, …) — far more than Connapse needs. Least-privilege custom role:
```json
{
  "Name": "Connapse RBAC Authorization Reader",
  "IsCustom": true,
  "Description": "Read role and deny assignments at subscription scope for per-user permission filtering.",
  "Actions": [
    "Microsoft.Authorization/roleAssignments/read",
    "Microsoft.Authorization/denyAssignments/read"
  ],
  "NotActions": [], "DataActions": [], "NotDataActions": [],
  "AssignableScopes": ["/subscriptions/{subscriptionId}"]
}
```
(Custom-role schema per [custom roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles).) **`Microsoft.Authorization/roleDefinitions/read` is NOT needed** — a research worker raised it as a possible dependency, but Connapse's `ArmRbacReader` matches assignments by the hard-coded Storage-Blob-Data role GUIDs (the last segment of `RoleDefinitionId`) and never fetches role *definitions*, so `roleAssignments/read` + `denyAssignments/read` suffice (resolved against this repo's code, not an external source). Built-in Reader remains a defensible one-line fallback if custom-role creation is undesirable.

**Managed identity vs certificate app — split by topology.** A managed identity **cannot be used outside Azure** ("a managed identity can only be used by the Azure resource for which it was created"), so Connapse's self-hosted/Docker deployments **must** use a certificate-authenticated app registration + service principal — not optional, a hard platform constraint. On an Azure VM/AKS a managed identity is preferable (no cert to store/rotate), **but** its auto-created service principal still needs the same Graph application permissions granted and admin-consented and the same RBAC roles assigned — MI only removes credential management, not the permission grants. Design the access identity around the certificate app as the baseline; treat MI as an optional enhancement, applying the identical roles/consent to whichever service principal represents the access identity.

*Confidence:* **Primary-source** for the built-in-role facts, the two-app guidance, and the MI constraint. The two custom-role recommendations are a least-privilege synthesis (well-documented principle) applied to confirmed read-only actions, not copied from a named MS example.

### Sub-question 4 — Certificates, the paste-back handshake, and tenant blockers
**Claim:** Upload only the public cert; return four values in a delimited block; detect the common tenant blockers.

**Certificate (public only).** Entra accepts `.cer`/`.pem`/`.crt` public certificates; you upload the public key and sign the client-assertion JWT locally with the private key — the private key never leaves the host, matching the AWS flow ([Add and manage app credentials](https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials)). `az ad app credential reset --cert @cert.pem` takes a PEM/DER public cert (docs: "Do not include the private key"); **without `--append` it wipes existing credentials** (destructive default), so rotation and multi-cert use must pass `--append`. Known gotcha: re-running the same `--append` isn't idempotent (it imports a duplicate keyCredential) — the script should `az ad app credential list` first. **Rotation:** append the new cert before the old expires, switch local config to the new thumbprint, verify, then `az ad app credential delete --key-id <old>`. Some hardened tenants enforce an `appManagementPolicy` capping cert lifetime (e.g., `maxLifetime P180D`) — **disabled by default** but, when on, will reject a longer-lived cert; catch and surface that error ([Enforce secret/certificate standards](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/tutorial-enforce-secret-standards)).

**Paste-back handshake — four values suffice:**
```
-----BEGIN CONNAPSE AZURE SETUP-----
TENANT_ID=<guid>            # az account show --query tenantId -o tsv
SUBSCRIPTION_ID=<guid>      # az account show --query id -o tsv
ACCESS_APP_CLIENT_ID=<guid> # az ad app create (access) --query appId -o tsv
SIGNIN_APP_CLIENT_ID=<guid> # az ad app create (sign-in) --query appId -o tsv
-----END CONNAPSE AZURE SETUP-----
```
`appId` (client ID) is shared by an app and its SP; the separate *object* IDs aren't needed at paste-back (Connapse can derive the SP object id later via `az ad sp show --id <appId> --query id` if required). The delimited-block format is a **design recommendation** (mirroring Connapse's AWS `-----BEGIN…-----` marker), not an MS-documented convention.

**Common tenant blockers** (script should detect the error and instruct):
- **"Users can register applications" = No** (default is Yes): app creation then needs Application Developer / Application Administrator / Cloud Application Administrator / Global Administrator ([Delegate app roles](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/delegate-app-roles)).
- **Guest (B2B) operator**: guests get restricted directory permissions by default and typically cannot register apps ([Default user permissions](https://learn.microsoft.com/en-us/entra/fundamentals/users-default-permissions)).
- **PIM / Conditional Access / MFA**: a PIM-eligible privileged role must be *activated* (MFA/justification) before privileged `az` calls succeed — instruct the operator to activate then re-run. *(Inferred from PIM/CA docs, not a single Cloud-Shell-specific statement — lower confidence.)*
- **Sovereign/national clouds** (Azure Government, China): different ARM/Graph/login endpoints — the script must `az cloud set --name <cloud>` and target the matching Graph endpoint. *(General pattern; needs a dedicated endpoint citation if launch targets these.)*

## Conflicts and uncertainties
- **No material conflicts** between workers. Both the admin-consent and blockers workers independently confirmed default self-service app registration is **Yes** and that consent is the gated step.
- **Load-bearing single-source:** the Graph app-role GUIDs came from a third-party reference, not Microsoft directly. Mitigation: the script/Connapse should resolve them live via `az ad sp show` on the Graph SP at build/run time rather than hard-coding.
- **Lower-confidence, inferred (not single-source MS):** the PIM→Cloud-Shell failure mode; sovereign-cloud endpoint specifics; the paste-back delimiter format (our design, not an MS convention).
- **Resolved against this repo (not external):** the custom RBAC-read role does not need `roleDefinitions/read` (Connapse hard-codes the role GUIDs).

## Gaps — what we did not find
- No official Microsoft guidance on a "handshake" output format (expected — it's an app-specific design choice).
- No single canonical Learn page enumerating all sovereign-cloud ARM/Graph/login endpoints was captured this pass.
- Whether a client *secret* is acceptable in non-production self-host (vs. mandatory certificate) was out of scope; Connapse's design already mandates certificate/MI, so this doesn't block.
- Not verified live: that the exact Graph app-role GUIDs above are current in every cloud — flagged for build-time verification.

## Source quality assessment
The load-bearing conclusions — Cloud Shell pre-auth and command semantics, the admin-consent role split, the v1 consent URL, the built-in-role dataActions, the certificate-public-key/`--append` behavior, and the MI-cannot-run-off-Azure constraint — rest on **primary sources (Microsoft Learn / Entra / Graph docs)**. The custom-role JSON recommendations are a **synthesis** of the (documented) least-privilege principle over confirmed read-only actions. Three items are explicitly lower-confidence/inferred (Graph GUIDs' provenance, PIM-in-Cloud-Shell, sovereign endpoints, and the handshake format) and are flagged inline.

## Sources
**Primary (Microsoft Learn / Entra / Graph):**
- Cloud Shell overview — https://learn.microsoft.com/en-us/azure/cloud-shell/overview
- az ad app credential — https://learn.microsoft.com/en-us/cli/azure/ad/app/credential
- Add and manage app credentials — https://learn.microsoft.com/en-us/entra/identity-platform/how-to-add-credentials
- Permissions overview (who can consent) — https://learn.microsoft.com/en-us/graph/permissions-overview
- Grant tenant-wide admin consent (role split + v1 URL) — https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent
- v2 admin consent protocol — https://learn.microsoft.com/en-us/entra/identity-platform/v2-admin-consent
- Azure RBAC troubleshooting (roleAssignments/write roles) — https://learn.microsoft.com/en-us/azure/role-based-access-control/troubleshooting
- Storage permissions reference (blobs/read vs blobs/tags/read) — https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/storage
- Azure custom roles — https://learn.microsoft.com/en-us/azure/role-based-access-control/custom-roles
- Register applications – Zero Trust (separate apps) — https://learn.microsoft.com/en-us/security/zero-trust/develop/app-registration
- Delegate app registration permissions — https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/delegate-app-roles
- Default user permissions (guest limits) — https://learn.microsoft.com/en-us/entra/fundamentals/users-default-permissions
- Enforce secret/certificate standards (cert lifetime policy) — https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/tutorial-enforce-secret-standards

**Secondary/tertiary (flagged, verify at build):**
- graphpermissions.merill.net — Graph app-role GUIDs (User.Read.All, GroupMember.Read.All)
- azure-cli GitHub issue #12797 — admin-consent propagation/failure modes
