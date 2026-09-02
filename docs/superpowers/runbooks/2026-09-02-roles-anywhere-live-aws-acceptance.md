# Runbook — IAM Roles Anywhere live-AWS acceptance

**Purpose:** prove the one thing unit tests cannot — that AWS accepts Connapse's
**CA→leaf certificate chain** and issues temporary credentials for a leaf-signed
`CreateSession`. This is the epic's acceptance gate for the whole native signing +
setup path (PR 1 signer, PR 2b keypair/script, PR 3a save, PR 3b removals).

**When to run:** once per meaningful change to the signing engine, the keypair
generator, or the setup script — and before the epic merges to `main`. It reaches
real AWS, so it is tagged `Category=LiveAws` and never runs in CI or the normal
`Category=Unit`/`Category=Integration` filters.

**What it costs:** a trust anchor, a profile, an IAM role, and one inline policy in
your AWS account — all removed by the cleanup step. No charge of note.

## Prerequisites

- An AWS account and admin access to **CloudShell** with the setup permissions the
  script needs (`iam:CreateRole/PutRolePolicy/GetRole/ListRoleTags`,
  `rolesanywhere:CreateTrustAnchor/ListTrustAnchors/CreateProfile/ListProfiles`,
  `sts:GetCallerIdentity`).
- The repo checked out with the .NET SDK, able to run `dotnet test`.

## Steps

### 1. Generate a keypair + the matching setup script

```bash
CONNAPSE_LIVE_AWS_RA_KEYGEN_DIR=/tmp/ra-live \
CONNAPSE_LIVE_AWS_RA_REGION=us-east-1 \
dotnet test tests/Connapse.Integration.Tests \
  --filter "FullyQualifiedName~RolesAnywhereLiveAwsTests.GenerateKeypair"
```

Writes to `/tmp/ra-live`: `ca.pem` (the CA — becomes the trust anchor),
`leaf-cert.pem` + `leaf-key.pem` (what the verify step signs with), and `setup.sh`
— the exact CloudShell script the product generates for this CA. Using the product's
own script is the point: the live test then exercises the real setup path.

### 2. Create the AWS resources

Open **AWS CloudShell** in the same region, upload/paste `setup.sh`, and run it. It
creates a per-instance (fingerprint-named) trust anchor from `ca.pem`, an IAM role
whose trust policy pins to that trust anchor, the `ConnapseRead` policy, and a
profile. On success it prints a block:

```
----- BEGIN CONNAPSE AWS ROLE -----
trustAnchorArn=arn:aws:rolesanywhere:us-east-1:...:trust-anchor/...
profileArn=arn:aws:rolesanywhere:us-east-1:...:profile/...
roleArn=arn:aws:iam::...:role/connapse-ra-...
region=us-east-1
----- END CONNAPSE AWS ROLE -----
```

### 3. Run the verify test

Export the leaf files and the printed ARNs, then run the gated verify test:

```bash
CONNAPSE_LIVE_AWS_RA_CERT_FILE=/tmp/ra-live/leaf-cert.pem \
CONNAPSE_LIVE_AWS_RA_KEY_FILE=/tmp/ra-live/leaf-key.pem \
CONNAPSE_LIVE_AWS_RA_TRUST_ANCHOR_ARN=arn:aws:rolesanywhere:us-east-1:...:trust-anchor/... \
CONNAPSE_LIVE_AWS_RA_PROFILE_ARN=arn:aws:rolesanywhere:us-east-1:...:profile/... \
CONNAPSE_LIVE_AWS_RA_ROLE_ARN=arn:aws:iam::...:role/connapse-ra-... \
CONNAPSE_LIVE_AWS_RA_REGION=us-east-1 \
dotnet test tests/Connapse.Integration.Tests \
  --filter "FullyQualifiedName~RolesAnywhereLiveAwsTests.CreateSession"
```

**Pass** = AWS accepted the leaf-signed `CreateSession` and returned temporary
credentials (the test prints the expiry). That is the acceptance gate met.

**Failure to read:**
- `CreateSession failed with HTTP 403` mentioning the trust anchor → AWS rejected the
  chain. The most likely cause is a cert-shape defect (the trust anchor must be
  `CA:true`, the signing leaf `CA:false`); re-check `RolesAnywhereKeyGenerator`.
- `AccessDenied` on the role → the `ConnapseRead` policy or trust policy is wrong.
- A signing/canonicalization mismatch surfaces here as a 4xx even though every unit
  self-check passes — which is exactly why this gate exists.

### 4. Clean up

Remove the AWS resources (the reset cleanup the UI would show):

```bash
aws rolesanywhere delete-trust-anchor --region us-east-1 --trust-anchor-id <id-from-trustAnchorArn>
aws rolesanywhere delete-profile --region us-east-1 --profile-id <id-from-profileArn>
aws iam delete-role-policy --role-name <name-from-roleArn> --policy-name ConnapseRead
aws iam delete-role --role-name <name-from-roleArn>
```

Delete `/tmp/ra-live` (it holds a private key).

## Owner

Assign a specific person + date each time this runs; record the pass (test output +
expiry) alongside the epic's release notes before merging to `main`.
