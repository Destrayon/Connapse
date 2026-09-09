# Can the Azure Blob connector be as guided as the S3 one?
**Date:** 2026-09-08
**Status:** Reviewed
**Built on:** azure-easy-setup-2026-09-08.md (the app-identity and custom-role design this extends)

## Executive summary
Yes — everything the S3 connection form automates has an Azure equivalent reachable with the
certificate-authenticated access app Connapse already has, using only read-only operations:
storage-account discovery (ARM), container discovery (blob data plane), hierarchical-namespace
detection (ARM `isHnsEnabled`), and a real test-connection call. The one cost is two small
control-plane **read** permissions the least-privilege custom roles currently omit
(`Microsoft.Storage/storageAccounts/read` and `.../blobServices/containers/read`). Neither exposes
keys or data; both are part of the built-in Storage Blob Data Reader / Reader roles. Microsoft's
own Data Factory presents exactly this shape: "From Azure subscription" pickers or "Enter manually",
plus Test connection.

## Research brief
**Question:** What can the Azure Blob connection form automate to match the S3 form, under
Connapse's identity model (certificate app or managed identity; no keys, SAS, or connection strings)?

**Sub-questions:** (1) What does the S3 form automate and how? (2) What does the Azure form have?
(3) Which Azure operations and RBAC actions give account/container discovery and a test?
(4) How do Microsoft's products present this?

**Out of scope:** account keys / SAS / connection strings; write operations of any kind; changing
the per-object permission engine.

## Findings

### What S3 automates today (Connections.razor, S3Discovery, S3ConnectionTester)
Readiness banner from `IProviderSetupReader` (links to the AWS provider page when access is not set
up); bucket picker ("Choose from buckets Connapse can see", `IS3Discovery.ListBucketsAsync`) that
adds to the allowed-locations list and auto-fills the connection name; region auto-detect on pick
(`GetBucketRegionAsync`); cross-account Role ARN behind a disclosure; a generated least-privilege IAM
policy for exactly the chosen buckets (`S3SetupPolicy`); Test connection (lists ≤5 objects) with a
troubleshooting link on failure. Discovery is a server-side service injected into the page, not a
REST endpoint.

### What Azure has today
Two text fields (storage account name; optional blob endpoint for emulators), the shared
allowed-locations list, and Test connection (`AzureBlobConnectionTester`, lists ≤5 blobs). No
readiness banner, no account or container picker, no name auto-fill, no RBAC snippet, no
troubleshooting link, and no discovery service at all (`ConnapseAzureCredentials` is only a
`TokenCredential`).

### Azure operations and the permissions they need (learn.microsoft.com, high confidence)
| Need | Call | RBAC operation | In our roles today? |
|---|---|---|---|
| List storage accounts in the subscription | `Azure.ResourceManager.Storage` `SubscriptionResource.GetStorageAccountsAsync()` → `PrimaryEndpoints.BlobUri`, `IsHnsEnabled` | `Microsoft.Storage/storageAccounts/read` (Action) | **No** |
| List containers in an account | `BlobServiceClient.GetBlobContainersAsync()` | `Microsoft.Storage/storageAccounts/blobServices/containers/read` (control-plane **Action**, even though the call is data-plane) | **No** → 403 today |
| List blobs with a prefix / read blobs | `BlobContainerClient.GetBlobsAsync` | `.../containers/blobs/read` (DataAction) | Yes |
| Read blob index tags | | `.../blobs/tags/read` (DataAction) | Yes |

Storage Blob Data Reader's definition is Actions `containers/read` + `generateUserDelegationKey`,
DataActions `blobs/read`; our blob role is that minus `containers/read` plus `tags/read`. The
subscription-wide Reader role would also work for account listing but exposes every resource type,
so a targeted action on our existing subscription-scoped role is tighter. `listKeys/action` is not
needed for anything here and must stay out.

There is no Azure "GetCallerIdentity" for an app. Token acquisition alone proves only that the
certificate is valid, not that any role is assigned; the meaningful readiness probe is a scoped
read that exercises the real permission (e.g. `GetBlobContainersAsync` on the chosen account, or
`GET /subscriptions/{id}` for ARM reachability).

### How Microsoft's products present it
Azure Data Factory: "Account selection method" toggle — **From Azure subscription** (subscription →
storage account dropdown, auto-fills the endpoint) or **Enter manually** (type the URL) — with a
**Test connection** button; service-principal auth stores the service endpoint URL. Purview browses
subscription → storage account and tests with the chosen identity (needs Storage Blob Data
Reader). Fabric and Databricks take a URL (`https://…dfs.core.windows.net` / `abfss://`). All store
a URL or endpoint, never a bare account name; HNS is implied by endpoint choice rather than shown,
though ARM exposes `isHnsEnabled` directly — Connapse can do better by showing it.

## Recommendation
1. **Roles (needs the operator's OK — two read-only additions):** add
   `Microsoft.Storage/storageAccounts/blobServices/containers/read` to the blob data role's
   Actions, and `Microsoft.Storage/storageAccounts/read` to the subscription-scoped authorization
   reader role. Both are read-only listing rights; no data or key access changes. The Access script
   should create-or-update the role definitions so re-running upgrades existing roles.
2. **`IAzureBlobDiscovery`** (Core interface, Storage implementation, mirroring `IS3Discovery`
   with an `AzureProbe<T>` outcome type: not-configured / denied / failed / ok):
   `ListStorageAccountsAsync` (name, blob endpoint, resource group, `IsHnsEnabled`),
   `ListContainersAsync(accountName | endpoint)`, `ProbeAccessAsync`.
3. **Connections form, Azure block, mirroring S3 line for line:** readiness banner (Azure Access
   status, link to the Azure provider page); "From your subscription" account picker with an
   "Enter manually" fallback (auto-fills account + endpoint; shows a "Data Lake Gen2 (hierarchical
   namespace)" badge, which matters because Gen2 accounts are enforced via POSIX ACLs); container
   picker feeding allowed locations and auto-filling the name; a generated `az role assignment`
   snippet scoping the blob role to exactly the chosen account(s)/container(s); troubleshooting
   link to `docs/azure-setup.md` on test failure.
4. Package: `Azure.ResourceManager.Storage` (new, Storage project). Sovereign clouds: construct
   `ArmClient` with the environment matching the credential's authority host.

## Conflicts and uncertainties
- The List Containers REST doc names Storage Blob Data Contributor as least-privileged although
  Reader carries the same `containers/read` Action — treated as a docs inconsistency; Reader-level
  rights suffice. Verify live after the role change.
- `PrimaryEndpoints.BlobUri` vs `.Blob` depends on the SDK version; check the installed package.
- ADF's exact Test-connection operation is not documented (community claim: a lightweight list).

## Gaps
No product documents surfacing `isHnsEnabled` in a picker; Connapse would be doing something the
references don't. Managed-identity deployments get discovery the same way, but the Providers page
still cannot see a system-assigned identity (known limitation).

## Sources
Primary: learn.microsoft.com — Azure built-in roles (Storage); List Containers and List Blobs REST
authorization sections; `StorageAccountData.PrimaryEndpoints`; storage-blob-query-endpoint-srp;
hierarchical-namespace (isHnsEnabled); Data Factory Azure Blob connector; Purview register/scan
Azure Blob; Fabric ADLS shortcuts; Databricks external locations (ADLS). Secondary: Airbyte Azure
Blob source docs; RBACMap role listing; a community Q&A on ADF test connection (410 Gone at fetch).
