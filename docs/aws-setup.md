# AWS Setup

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse reads S3 as its own AWS identity, issued through **IAM Roles Anywhere**: a certificate
generated on the Connapse host, exchanged with AWS for short-lived credentials. No access key is
ever pasted in or stored. Per-user search permissions come from **S3 Access Grants**, read through
**IAM Identity Center**; Connapse only reads grants, it never creates or deletes one.

Everything below is configured on **Admin → Providers → AWS** (`/admin/providers/aws`), then a
**connection** names which buckets may be used and a **source** picks one to index.

## Prerequisites

| Need | Why |
|------|-----|
| An AWS account, and a person who can open [CloudShell](https://console.aws.amazon.com/cloudshell/home) in it | Every guided step is one command pasted into CloudShell |
| Permission for that person to run `iam:CreateRole`, `iam:PutRolePolicy`, `rolesanywhere:CreateTrustAnchor`, `rolesanywhere:CreateProfile` (plus the matching `Get`/`List` calls and `sts:GetCallerIdentity`) | The Access step creates a role, trust anchor, and profile |
| Connapse served over **HTTPS**, or reached on `localhost` | Required only for per-user permissions: a SAML assertion is a bearer credential and is refused over plain HTTP elsewhere |
| An IAM Identity Center instance, if you want per-user permissions | Grants are held against directory users and groups |

Nothing on the provider page is required for a plain install. A deployment that reads S3 through an
instance or task role can skip the Access step; the card says so when it detects that.

## Step 1 — Access

**What it is:** the identity Connapse syncs S3 with. Connections choose buckets; this decides whether any of them can reach AWS at all.

### Easy setup with CloudShell (recommended)

1. Enter the **AWS region** the trust anchor and profile should live in (for example `us-east-1`).
2. Choose **Generate setup command**. Connapse mints a certificate locally; the private key never leaves the host.
3. Copy the command, open CloudShell, paste it, and copy the block it prints (both `-----` marker lines included).
4. Paste that block back into **Paste what it printed** and choose **Check and save**. Connapse asks AWS for a session with the new credential before storing anything, so a bad run never replaces a working one.

Keep the page open until step 4 is done: the generated key exists only in that page. An abandoned run leaves one unused trust anchor in AWS, which the next run replaces.

The role it creates is read-only: every S3 bucket in the account, plus the S3 Access Grants and Identity Center reads that per-user permissions need. Choose **Update role permissions** after upgrading Connapse to re-apply the current policy; Connapse never edits IAM itself.

### Manual values

For a Roles Anywhere setup you made yourself. Every field is verified against AWS before saving.

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Region | Yes | The region you created the trust anchor and profile in |
| Certificate (PEM) | Yes | The certificate registered as the trust anchor, or a leaf it issued directly. Intermediate CAs are not supported |
| Trust anchor ARN | Yes | [IAM Roles Anywhere console → Trust anchors](https://console.aws.amazon.com/rolesanywhere/home#/trust-anchors), on the trust anchor's page |
| Role ARN | Yes | [IAM console → Roles](https://console.aws.amazon.com/iam/home#/roles), on the role's summary page |
| Profile ARN | Yes | [IAM Roles Anywhere console → Profiles](https://console.aws.amazon.com/rolesanywhere/home#/profiles), on the profile's page |
| Private key (PEM) | First save only | The key matching the certificate. Stored encrypted, never shown again; leave blank when editing to keep the current one |

### Health

The card shows when the credential was stored, when AWS last honoured it, and when the certificate expires. **Recheck** re-tests without re-entering anything. An expired certificate is reported as such and S3 sources stop syncing until access is set up again.

**Remove stored credential** deletes only Connapse's copy. The trust anchor and profile stay enabled in AWS until you run the cleanup command the page shows afterwards.

## Step 2 — IAM Identity Center

**What it is:** the directory a person is looked up in when Connapse checks what they may read. This step only finds it; nothing is created.

Identity Center lives in exactly one region per organisation, and looking in the wrong region reads as there being no instance at all. The guided scan runs a read-only command in CloudShell and prints what it found; paste the block back and choose **Use this instance**.

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Region | Yes | The one region the instance lives in |
| Identity store ID | Yes | [IAM Identity Center console](https://console.aws.amazon.com/singlesignon/home) → Settings, beside the instance ARN (`d-…`) |
| Instance ARN | Yes | Same page, or `aws sso-admin list-instances` (`arn:aws:sso:::instance/ssoins-…`) |

## Step 3 — Per-user permissions

**What it is:** the Identity Center application a person signs into to prove which directory user they are. Connapse uses it to filter search results to the S3 locations their access grants cover.

The guided setup deploys a CloudFormation template that creates the S3 Access Grants instance and registers the `s3://` location. The SAML application itself must be created in the console — AWS offers no API for it. Labels on the form match the console's own names.

| Field | Direction | Where to get it |
|-------|-----------|-----------------|
| Application SAML audience | Give to AWS | Prefilled from Connapse's address; type it into the application's metadata page |
| Application ACS URL | Give to AWS | Prefilled from Connapse's address; type it into the same page |
| IAM Identity Center SAML issuer URL | Take from AWS | In the **IAM Identity Center metadata** file the application page offers to download; pasting the whole file into the guided step fills all three |
| IAM Identity Center sign-in URL | Take from AWS | Same file |
| IAM Identity Center certificate | Take from AWS | Same file. Base64, with or without the `BEGIN CERTIFICATE` lines |

Two things to get right in the console:

- **Attribute mappings:** `Subject` → `${user:subject}`, format `unspecified`. Not `${user:preferredUsername}`, which gives the display name and matches nobody.
- **Assign** the people or groups who may sign in. AWS refuses to turn the assignment requirement off for a SAML application.

Grants are created in the S3 console (**S3 → Access Grants → Create grant**), naming a directory user or group and a bucket, prefix, or object. Connapse honours a new grant within a minute. Until a grant exists for a bucket, searches over it return nothing to anyone: filtering hides what it cannot confirm.

**Buckets outside the Identity Center region are not covered.** See [covering buckets outside the Identity Center region](aws-per-user-permissions-multi-region.md).

## Connection (Amazon S3)

On **Connections → Add connection**, provider **Amazon S3**.

| Field | Required | Notes |
|-------|----------|-------|
| Name | Yes | Filled in from the first bucket you choose if left blank |
| Allowed locations | No, but recommended | One bucket or `bucket/prefix/` per line. **Choose from buckets Connapse can see** lists them; each choice fills the region and the test target. Empty allows anything the identity can reach, logged as a warning at each sync |
| Region | No | Looked up from the bucket. Set it only to override |
| Role ARN (Cross-account access) | No | A role in another account that Connapse's identity may assume. From the role's summary page in the IAM console |
| Bucket to test against | For testing | Not saved |

**Test connection** reports which layers passed — reached AWS, authenticated, authorised, listed the bucket — and offers the raw AWS error on demand.

## Source

On **Sources → New source**, pick the connection; the **Bucket** list offers exactly what the connection allows. **Prefix** narrows the source to a subtree. Name is filled in from the bucket if left blank.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Access card: *Connapse has no AWS identity yet* | Nothing stored and nothing in the environment | Run the easy setup, or enter manual values |
| *AWS did not accept this credential* on Check and save | Wrong region, a mistyped ARN, a certificate that does not match the key, or clock skew on the host | Re-check each value against the console; the message quotes AWS's reason. Nothing was changed |
| Access card stays *Provisioning* | IAM is eventually consistent | Wait a minute and choose **Recheck** |
| Access card says *authenticates but cannot read S3* | The role's policy is missing or out of date | Choose **Update role permissions** and run the command |
| Test: *Authenticated, but not allowed to list bucket* | The identity lacks `s3:ListBucket`/`s3:GetObject` on that bucket, or the Role ARN is wrong | Attach the policy shown under **IAM policy granting exactly this access**, or fix the Role ARN |
| Test: *no bucket … exists in <region>* | Wrong name, or wrong region | Choose the bucket from the list; clear the region so it is looked up |
| Test: *Connapse has no AWS identity to test with* | The Access step is not done | Finish Step 1 |
| Test: *AWS did not answer within N seconds* | Outbound access to `s3.<region>.amazonaws.com` is blocked | Check the server's network egress |
| Bucket picker: *This identity may not list buckets* | The identity is scoped to named buckets and lacks `s3:ListAllMyBuckets` | Type the bucket name; this is not an error |
| Identity Center scan: *The scan was refused* | The CloudShell user lacks `sso:ListInstances` | Add the listed permissions to that user and scan again |
| Identity Center scan finds nothing | Wrong region, or no instance | Scan from the organisation's management account; enable an instance from the Identity Center console if none exists |
| Per-user permissions: *cannot receive a sign-in at this address* | Connapse is reached over plain HTTP off loopback | Serve it over HTTPS, or open it on `localhost` |
| Sign-in fails with a destination or audience mismatch | Connapse's address changed after the application was registered | Update the ACS URL and audience in the application to the values the form shows |
| Sign-in works but every S3 search returns nothing | No grant covers the bucket, `Subject` is mapped to the display name, or the bucket is outside the Identity Center region | Create a grant; fix the attribute mapping; see the multi-region note above |
| Sign-in stopped working after months | Identity Center rotated its certificate | Paste the new **IAM Identity Center certificate** into the form |
| Sync stopped and the Access card says the certificate expired | The Roles Anywhere certificate reached its end date | Generate a new setup command and save; then run the cleanup command for the old trust anchor |

## Removing AWS

1. **Remove stored credential** on the Access card, then run the cleanup command it prints in CloudShell to delete the trust anchor, profile, and role.
2. **Reset SAML application** clears the sign-in values; per-user filtering stays on, so S3 searches return nothing for anyone until it is set up again.
3. Delete the application in the Identity Center console, and the Access Grants instance in the S3 console, if nothing else uses them.
