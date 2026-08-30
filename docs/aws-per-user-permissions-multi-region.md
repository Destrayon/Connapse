# Covering buckets outside the Identity Center region

Per-user permissions resolve through S3 Access Grants, and a grant is created against the Access
Grants instance **in the bucket's region**. That instance can only be associated with an IAM
Identity Center instance in the same region. So out of the box, per-user permissions cover buckets
in one region: the one Identity Center lives in.

This is not a Connapse limitation and cannot be worked around in Connapse. Covering a second region
means putting Identity Center in that region, which AWS supports through replication.

## What happens if you do nothing

Buckets in other regions are still indexed and synced. But once per-user permissions are switched
on, **they return nothing to anybody** — filtering excludes any document no grant covers, and no
grant can be created for them. Connapse detects this on the connection and says so instead of
offering a grant command that cannot succeed.

If those buckets do not need per-user filtering, the honest options are to leave per-user
permissions off, or to move that data into the Identity Center region.

## Verified behaviour

Tested against live AWS, 2026-08-30:

- An S3 Access Grants instance **can** be created in a second region on its own.
- Associating it with an Identity Center instance whose home region is elsewhere **fails**, both
  via `create-access-grants-instance --identity-center-arn` and via the separate
  `associate-access-grants-identity-center`:

  ```
  IdentityCenterAssociationFailedError: ... not authorized to perform: sso:CreateApplication
  on resource: arn:aws:sso::*:application/ssoins-...
  ```

  The principal differs between the two paths, so it is not a caller-permission gap: the regional
  SsoAdmin endpoint cannot resolve an instance whose home is elsewhere, so the resource ARN carries
  `*` for the account and nothing can authorise it.
- The same endpoint behaviour is visible directly: `describe-instance` against the instance ARN from
  another region returns *"the resource does not exist in this Region"*. Same ARN, same credentials,
  only the region differs.
- Creating a grant against an instance in the wrong region fails as
  `InvalidAccessGrant: The requested S3 Bucket is in a different region`.

## Prerequisites for replication

All four are hard gates:

- An **organization instance** of Identity Center. Replication is not available for account
  instances.

  ```bash
  aws sso-admin describe-instance --instance-arn <arn> --region <identity-center-region>
  ```

  The region matters: a regional endpoint cannot see an instance homed elsewhere, and says so —
  *"the resource does not exist in this Region"*. There is no instance-type field in the response;
  the tell is `"PermissionSetsEnabled": true`, since account instances do not support permission
  sets. `EncryptionConfigurationDetails.KeyType` also shows whether a customer managed key is
  already in use.
- An identity source that is an external IdP or the Identity Center directory. **Not** Active
  Directory.
- Commercial regions enabled by default. Opt-in regions are not supported.
- Identity Center configured with a **multi-Region customer managed KMS key**, in the same account.
  A single-Region key cannot be converted later.

If you use an external IdP, it must support multiple ACS URLs to get the full benefit. Okta, Entra
ID, PingFederate, PingOne and JumpCloud do; Google Workspace does not.

## Procedure

Order matters. The key has to be in use by Identity Center before it is replicated.

### 1. Create the KMS key in the Identity Center region

Multi-Region, symmetric, encrypt-and-decrypt, in the Organizations management account, in the same
region as the Identity Center instance.

Key administrators: the role you administer AWS with, **and an IAM user**. If a key policy mistake
makes the key unusable, Identity Center stops working — and if your only key administrator is a
role you reach through Identity Center, you cannot get back in to fix it. The IAM user is the
break-glass path.

Clear **"Allow key administrators to delete this key"**. A deleted key is unrecoverable and takes
Identity Center with it permanently.

### 2. Put the baseline policy on the key

The console wizard grants IAM principals. Identity Center also needs the `sso.amazonaws.com` and
`identitystore.amazonaws.com` service principals, with encryption-context conditions. Take the
policy from
[Baseline KMS key policy](https://docs.aws.amazon.com/singlesignon/latest/userguide/baseline-KMS-key-policy.html)
verbatim and fill in the account id. With no delegated administration account, remove that second
principal from `AllowIdentityCenterAdminAccounts`.

Use the same policy on every replica.

### 3. Point Identity Center at the key

```bash
aws sso-admin update-instance --instance-arn <instance-arn> --encryption-configuration KeyType=CUSTOMER_MANAGED_KEY,KmsKeyArn=<key-arn>
```

Identity Center validates that it can encrypt and decrypt with the key. If the policy is wrong it
reports an error and keeps the previous key, so this step fails safe.

### 4. Replicate the key to the second region

Create a multi-Region replica key there and give it the same key policy. KMS does not synchronise
key policies across regions; each replica has to be updated on its own.

### 5. Add the region to Identity Center

```bash
aws sso-admin add-region --instance-arn <instance-arn> --region-name <region>
aws sso-admin describe-region --instance-arn <instance-arn> --region-name <region>
```

Wait for `ACTIVE`. Initial replication takes as long as the amount of data warrants; later changes
propagate in seconds.

User and group ids are identical across regions, so the group id Connapse has stored stays valid.

### 6. Set S3 Access Grants up in the new region

The same steps as the first region: an Access Grants instance associated with Identity Center, and
an `s3://` location. The IAM role the location uses is global, so the one the Connapse setup already
created can be reused.

### 7. Grant

Open the connection in Connapse. With an Access Grants instance now present in the bucket's region,
it prints the grant command for that connection's buckets.

## Cost

Identity Center is free and the multi-Region documentation prices replication at nothing. The cost
is KMS: a customer managed key is billed per key per region, plus request charges.

S3 Access Grants pricing is not listed on the S3 pricing page. Connapse calls `ListAccessGrants` on
every search whose scopes are not cached — the cache is 60 seconds per user — so do not assume it
is free at scale.
