---
title: AWS setup
type: how-to
audience: admin
---

# AWS setup

Connapse reads Amazon S3 as its own AWS identity, issued through IAM Roles Anywhere: a certificate generated on the Connapse host is exchanged with AWS for short-lived credentials. You never paste in or store an access key. Per-user search permissions come from S3 Access Grants, read through IAM Identity Center. Connapse only reads grants; it never creates or deletes one.

Everything below is set up under **Admin** → **Providers** → **AWS** (`/admin/providers/aws`). Then a connection names which buckets may be used, and a source picks one to index.

## What gets indexed

Each source indexes the objects in one bucket, optionally under a prefix. Connapse reads each object's content as a file, using the same parsers as uploads.

## How permissions work

When per-user permissions are set up, each person links their IAM Identity Center user on **Profile** → **Integrations**, under **Amazon Web Services (Permissions)**. Their S3 results are then limited to the locations their access grants cover, whether granted to them or to a group they're in.

- A new grant takes effect within a minute.
- Until a grant covers a bucket, searches over it return nothing to anyone: filtering hides what it can't confirm.
- Buckets outside the Identity Center region aren't covered. See [Cover buckets outside the Identity Center region](aws-multi-region.md).

## Before you begin

| Need | Why |
|------|-----|
| An AWS account, and someone who can open [CloudShell](https://console.aws.amazon.com/cloudshell/home) in it | Every guided step is one command pasted into CloudShell |
| Permission for that person to run `iam:CreateRole`, `iam:PutRolePolicy`, `rolesanywhere:CreateTrustAnchor`, `rolesanywhere:CreateProfile` (plus the matching `Get`/`List` calls and `sts:GetCallerIdentity`) | The Access step creates a role, trust anchor and profile |
| Connapse served over HTTPS, or reached on `localhost` | Required only for per-user permissions: a SAML assertion is a bearer credential and is refused over plain HTTP elsewhere |
| An IAM Identity Center instance, if you want per-user permissions | Grants are held against directory users and groups |

A deployment that reads S3 through an instance or task role can skip the Access step; the card says so when it detects one.

## 1. Access

The identity Connapse syncs S3 with. Connections choose buckets; this step decides whether any of them can reach AWS at all.

### Easy setup with CloudShell (recommended)

1. Enter the **AWS region** the trust anchor and profile should live in, such as `us-east-1`.
2. Choose **Generate setup command**. Connapse makes a certificate locally; the private key never leaves the host.
3. Copy the command, open CloudShell, paste it, and copy the block it prints, including both `-----` marker lines.
4. Paste that block into **Paste what it printed** and choose **Check and save**. Connapse asks AWS for a session with the new credential before it stores anything, so a bad run never replaces a working one.

Keep the page open until step 4 is done: the generated key exists only in that page. An abandoned run leaves one unused trust anchor in AWS, which the next run replaces.

The role it creates is read-only: every S3 bucket in the account, plus the S3 Access Grants and Identity Center reads that per-user permissions need. After you upgrade Connapse, choose **Update role permissions** to apply the current policy; Connapse never edits IAM itself.

### Manual values

For a Roles Anywhere setup you made yourself. Every field is checked against AWS before saving.

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Region | Yes | The region you created the trust anchor and profile in |
| Certificate (PEM) | Yes | The certificate registered as the trust anchor, or a leaf it issued directly. Intermediate CAs aren't supported |
| Trust anchor ARN | Yes | [IAM Roles Anywhere console → Trust anchors](https://console.aws.amazon.com/rolesanywhere/home#/trust-anchors), on the trust anchor's page |
| Role ARN | Yes | [IAM console → Roles](https://console.aws.amazon.com/iam/home#/roles), on the role's summary page |
| Profile ARN | Yes | [IAM Roles Anywhere console → Profiles](https://console.aws.amazon.com/rolesanywhere/home#/profiles), on the profile's page |
| Private key (PEM) | First save only | The key matching the certificate. Stored encrypted and never shown again; leave blank when editing to keep the current one |

### Health

The card shows when the credential was stored, when AWS last accepted it, and when the certificate expires. **Recheck** tests it again without re-entering anything. An expired certificate is reported as such, and S3 sources stop syncing until access is set up again.

**Remove stored credential** deletes only Connapse's copy. The trust anchor and profile stay enabled in AWS until you run the cleanup command the page shows afterwards.

### Do it with the API

`PUT /api/v1/providers/aws/access` needs an Administrator token with the `admin:settings` scope. AWS is asked for a session first, and nothing is saved unless it gives one.

```json
{
  "region": "us-east-1",
  "certificatePem": "-----BEGIN CERTIFICATE-----\n...\n-----END CERTIFICATE-----",
  "trustAnchorArn": "arn:aws:rolesanywhere:us-east-1:123456789012:trust-anchor/...",
  "profileArn": "arn:aws:rolesanywhere:us-east-1:123456789012:profile/...",
  "roleArn": "arn:aws:iam::123456789012:role/ConnapseReader",
  "privateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----"
}
```

A blank `privateKeyPem` keeps the stored key. `GET /api/v1/providers/aws/access` returns the current setup and its status.

## 2. IAM Identity Center

The directory a person is looked up in when Connapse checks what they may read. This step only finds it; nothing is created.

Identity Center lives in exactly one region per organisation, and looking in the wrong region looks the same as having no instance. The guided scan runs a read-only command in CloudShell and prints what it found. Paste the block back and choose **Use this instance**.

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Region | Yes | The one region the instance lives in |
| Identity store ID | Yes | [IAM Identity Center console](https://console.aws.amazon.com/singlesignon/home) → Settings, beside the instance ARN (`d-…`) |
| Instance ARN | Yes | Same page, or `aws sso-admin list-instances` (`arn:aws:sso:::instance/ssoins-…`) |

### Do it with the API

`PUT /api/v1/providers/aws/identity-center`, with the `admin:settings` scope:

```json
{
  "region": "us-east-1",
  "identityStoreId": "d-1234567890",
  "instanceArn": "arn:aws:sso:::instance/ssoins-1234567890abcdef"
}
```

## 3. Per-user permissions

The Identity Center application a person signs into to prove which directory user they are.

The guided setup deploys a CloudFormation template that creates the S3 Access Grants instance and registers the `s3://` location. You create the SAML application itself in the console, because AWS has no API for it. The labels on the form match the console's names.

| Field | Direction | Where to get it |
|-------|-----------|-----------------|
| Application SAML audience | Give to AWS | Prefilled from Connapse's address; type it into the application's metadata page |
| Application ACS URL | Give to AWS | Prefilled from Connapse's address; type it into the same page |
| IAM Identity Center SAML issuer URL | Take from AWS | In the **IAM Identity Center metadata** file the application page offers to download. Pasting the whole file into the guided step fills all three |
| IAM Identity Center sign-in URL | Take from AWS | Same file |
| IAM Identity Center certificate | Take from AWS | Same file. Base64, with or without the `BEGIN CERTIFICATE` lines |

Two things to get right in the console:

- **Attribute mappings:** `Subject` → `${user:subject}`, format `unspecified`. Not `${user:preferredUsername}`, which gives the display name and matches nobody.
- **Assign** the people or groups who may sign in. AWS doesn't let you turn off the assignment requirement for a SAML application.

Create grants in the S3 console (**S3** → **Access Grants** → **Create grant**), naming a directory user or group and a bucket, prefix or object.

### Do it with the API

`PUT /api/v1/providers/aws/permissions`, with the `admin:settings` scope. `entityId` and `acsUrl` are the values you gave to AWS; the other three come from the metadata file:

```json
{
  "entityId": "https://connapse.example.com/saml/connapse",
  "acsUrl": "https://connapse.example.com/api/v1/auth/cloud/aws/acs",
  "idpEntityId": "https://portal.sso.us-east-1.amazonaws.com/saml/assertion/...",
  "idpSingleSignOnUrl": "https://portal.sso.us-east-1.amazonaws.com/saml/assertion/...",
  "idpSigningCertificate": "MIIC..."
}
```

Copy `entityId` and `acsUrl` from the form rather than typing them, since they're built from the address you reached Connapse on.

## 4. Connection (Amazon S3)

On **Connections** → **Add connection**, choose **Amazon S3**.

| Field | Required | Notes |
|-------|----------|-------|
| Name | Yes | Filled in from the first bucket you choose if left blank |
| Allowed locations | No, but recommended | One bucket or `bucket/prefix/` per line. **Choose from buckets Connapse can see** lists them; each choice fills in the region and the test target. Empty allows anything the identity can reach, and logs a warning at each sync |
| Region | No | Looked up from the bucket. Set it only to override |
| Role ARN (Cross-account access) | No | A role in another account that Connapse's identity may assume. From the role's summary page in the IAM console |
| Bucket to test against | For testing | Not saved |

**Test connection** reports which checks passed (reached AWS, authenticated, authorised, listed the bucket) and shows the raw AWS error on request.

### Do it with the API

`POST /api/v1/connections`, with an Administrator token and the `admin:sources` scope:

```json
{
  "name": "Company knowledge",
  "provider": "S3",
  "config": {
    "region": "us-east-1",
    "allowedLocations": ["company-knowledge", "shared-docs/public/"]
  }
}
```

Add `"roleArn"` to `config` for cross-account access. Test with `POST /api/v1/connections/{id}/test`, and list the buckets it can reach with `GET /api/v1/connections/{id}/discover`.

## 5. Source

On **Sources** → **New source**, pick the connection. The **Bucket** list offers exactly what the connection allows. **Prefix** narrows the source to a folder. Name is filled in from the bucket if left blank.

### Do it with the API

`POST /api/sources`, with the `admin:sources` scope:

```json
{
  "name": "Company docs",
  "connectionId": "CONNECTION_ID",
  "scope": { "type": "s3", "bucketName": "company-knowledge", "prefix": "docs/" }
}
```

## Sync behaviour

Each source is checked every 5 minutes by default, or at its `syncIntervalSeconds`. An object is reindexed when its last-modified time or size changes, and an object gone from the listing is removed, subject to the [deletion guard](index.md#deletions-are-guarded). Grants are read at search time, so permission changes don't need a sync.

## Limitations

- Per-user permissions cover only buckets in the Identity Center region. See [Cover buckets outside the Identity Center region](aws-multi-region.md).
- Intermediate certificate authorities aren't supported for Roles Anywhere.
- The SAML application must be created in the AWS console; AWS has no API for it.
- Connapse never creates, changes or deletes S3 Access Grants.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Access card: *Connapse has no AWS identity yet* | Nothing stored and nothing in the environment | Run the easy setup, or enter manual values |
| *AWS did not accept this credential* on Check and save | Wrong region, a mistyped ARN, a certificate that doesn't match the key, or clock skew on the host | Check each value against the console; the message quotes AWS's reason. Nothing was changed |
| Access card stays *Provisioning* | IAM changes take a moment to apply everywhere | Wait a minute and choose **Recheck** |
| Access card says *authenticates but cannot read S3* | The role's policy is missing or out of date | Choose **Update role permissions** and run the command |
| Test: *Authenticated, but not allowed to list bucket* | The identity lacks `s3:ListBucket`/`s3:GetObject` on that bucket, or the Role ARN is wrong | Attach the policy shown under **IAM policy granting exactly this access**, or fix the Role ARN |
| Test: *no bucket … exists in <region>* | Wrong name, or wrong region | Choose the bucket from the list; clear the region so it's looked up |
| Test: *Connapse has no AWS identity to test with* | The Access step isn't done | Finish step 1 |
| Test: *AWS did not answer within N seconds* | Outbound access to `s3.<region>.amazonaws.com` is blocked | Check the server's outbound network access |
| Bucket picker: *This identity may not list buckets* | The identity is limited to named buckets and lacks `s3:ListAllMyBuckets` | Type the bucket name; this isn't an error |
| Identity Center scan: *The scan was refused* | The CloudShell user lacks `sso:ListInstances` | Add the listed permissions to that user and scan again |
| Identity Center scan finds nothing | Wrong region, or no instance | Scan from the organisation's management account; turn on an instance in the Identity Center console if none exists |
| Per-user permissions: *cannot receive a sign-in at this address* | Connapse is reached over plain HTTP, not on `localhost` | Serve it over HTTPS, or open it on `localhost` |
| Sign-in fails with a destination or audience mismatch | Connapse's address changed after the application was registered | Update the ACS URL and audience in the application to the values the form shows |
| Sign-in works but every S3 search returns nothing | No grant covers the bucket, `Subject` is mapped to the display name, or the bucket is outside the Identity Center region | Create a grant; fix the attribute mapping; see [Limitations](#limitations) |
| Sign-in stopped working after months | Identity Center rotated its certificate | Paste the new **IAM Identity Center certificate** into the form |
| Sync stopped and the Access card says the certificate expired | The Roles Anywhere certificate reached its end date | Generate a new setup command and save; then run the cleanup command for the old trust anchor |

## Removing AWS

1. Delete the S3 sources, then the S3 connections, on the **Sources** and **Connections** pages.
2. **Remove stored credential** on the Access card, then run the cleanup command it prints in CloudShell to delete the trust anchor, profile and role.
3. **Reset SAML application** clears the sign-in values. Per-user filtering stays on, so S3 searches return nothing for anyone until it's set up again.
4. Delete the application in the Identity Center console, and the Access Grants instance in the S3 console, if nothing else uses them.

With the API, `DELETE /api/v1/providers/aws/access` returns the cleanup script, and `DELETE /api/v1/providers/aws/permissions` and `DELETE /api/v1/providers/aws/identity-center` clear the other two steps.
