# AWS SSO Setup

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse integrates with AWS IAM Identity Center (formerly AWS SSO) using the **device authorization flow**. Users authenticate by entering a code on the AWS sign-in page — no stored access keys, no redirect URIs.

## Prerequisites

- AWS IAM Identity Center enabled in your AWS account
- Admin access to Connapse (for global settings configuration)
- Users must have accounts in the IAM Identity Center directory

## How It Works

```
Admin configures IAM Identity Center settings (Issuer URL + Region)
                           ↓
User clicks "Sign in with AWS" on their Profile page
                           ↓
Connapse registers an OAuth2 client with IAM Identity Center (auto, first time only)
                           ↓
IAM Identity Center returns a user code + verification URL
                           ↓
User opens the URL and enters the code → authenticates with AWS
                           ↓
Connapse polls for completion → retrieves SSO access token
                           ↓
SSO token used to discover user's permitted AWS accounts (ListAccounts)
                           ↓
Identity linked — user can now access S3 containers scoped to their AWS permissions
```

## Admin Configuration

### 1. Get Your IAM Identity Center Details

1. Open the **AWS IAM Identity Center** console
2. In the left navigation, click **Settings**
3. Copy the **AWS access portal URL** — this is your **Issuer URL** (e.g., `https://d-1234567890.awsapps.com/start`)
4. Note the **Region** where IAM Identity Center is deployed (e.g., `us-east-1`)

### 2. Configure in Connapse

1. Go to **Settings** > **AWS SSO** tab
2. Enter the **Issuer URL** and **Region**
3. Click **Test Connection** to verify
4. Click **Save Changes**

**What "Test Connection" does:** Calls `RegisterClient` against the IAM Identity Center OIDC endpoint to verify the issuer URL and region are valid. This creates an ephemeral test client that is not stored.

### Settings Reference

| Field | Required | Description |
|-------|----------|-------------|
| Issuer URL | Yes | AWS access portal URL (e.g., `https://d-1234567890.awsapps.com/start`) |
| Region | Yes | AWS region where IAM Identity Center is deployed |
| Client ID | Auto | OAuth2 client ID — populated automatically on first user sign-in |
| Client Secret Expires | Auto | Expiration time of the auto-registered client credentials |

**API:**
```http
GET  /api/settings/awssso          # Read current settings
PUT  /api/settings/awssso          # Update settings
POST /api/settings/test-connection  # Test with category "awssso"
```

## User Flow

### 1. Start Sign-In

1. Navigate to your **Profile** page (click your username in the navigation)
2. In the **Cloud Identities** section, find the **AWS** card
3. Click **Sign in with AWS**

### 2. Authenticate

Connapse displays:
- A **user code** (e.g., `ABCD-EFGH`) in large monospace text
- A **verification URL** (clickable link, opens in new tab)

1. Click the verification URL (or open it manually)
2. Enter the user code on the AWS sign-in page
3. Authenticate with your AWS credentials
4. Approve the access request

### 3. Identity Linked

Once you authenticate:
- Connapse automatically detects completion (polls in the background)
- Your AWS accounts are discovered via `ListAccounts`
- The Profile page updates to show your connected identity:
  - Display Name
  - Primary Account ID
  - AWS Accounts (comma-separated account IDs)
  - Connected date / Last used date

### 4. Disconnect

To remove your AWS identity:
1. On the Profile page, click **Disconnect** on the AWS card
2. Confirm the disconnection
3. Cached scope entries are evicted immediately

## How Scope Enforcement Works

When you access an S3 container, Connapse checks your linked AWS identity:

1. **Identity Check:** Your `PrincipalArn` (account IDs) is retrieved from the stored cloud identity
2. **Scope Cache:** Results are cached per user + container (15-minute allow TTL, 5-minute deny TTL)
3. **Access Decision:** If your AWS identity is linked, access is granted to the S3 container's configured prefix
4. **Cache Eviction:** Disconnecting your AWS identity immediately evicts all cached scope entries

**Affected endpoints:**
- Document upload/download/delete
- Search (scope filter injected as path prefix)
- Folder listing
- Sync trigger

## Client Registration

Connapse automatically registers an OAuth2 public client with IAM Identity Center:

- **Client type:** `public` (no redirect URIs)
- **Grant types:** `device_code`, `refresh_token`
- **Scopes:** `sso:account:access`
- **Auto-renewal:** Re-registers when credentials expire (checks 10-minute buffer)
- **Storage:** Client ID, secret, and expiry stored in settings database (not in config files)

No manual client registration is needed in the AWS console.

## Troubleshooting

### "AWS SSO is not configured"
An admin must set the Issuer URL and Region in Settings > AWS SSO.

### Test Connection fails with "Invalid configuration"
The Issuer URL or Region doesn't match your IAM Identity Center instance. Verify:
- The URL is the full access portal URL (includes `/start`)
- The region matches where IAM Identity Center is deployed

### Test Connection times out
Check network access from the Connapse server to the IAM Identity Center endpoint. Verify the region is correct — a wrong region will route to a non-existent endpoint.

### User code expired
Device authorization codes expire (typically 10 minutes). Click "Cancel" and start the sign-in flow again.

### "Authorization pending" stays indefinitely
The user hasn't completed authentication on the AWS side. They need to:
1. Open the verification URL
2. Enter the user code
3. Authenticate and approve

### ListAccounts returns no accounts
The user exists in IAM Identity Center but has no permission sets assigned. Assign at least one permission set to an AWS account in the IAM Identity Center console.

### S3 container access denied after connecting
- Verify the S3 container's connector config has the correct `bucketName` and `region`
- Check that the user's IAM permissions include read access to the bucket
- The scope cache expires after 15 minutes — wait or disconnect/reconnect to force refresh
