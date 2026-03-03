# Azure Identity Setup

Connapse integrates with Azure AD (Microsoft Entra ID) using **OAuth2 authorization code + PKCE**. Users authenticate via a browser redirect to Microsoft's login page — Connapse acts as a confidential client with defense-in-depth PKCE.

## Prerequisites

- An Azure AD (Microsoft Entra ID) tenant
- Permission to create app registrations in Azure AD
- Admin access to Connapse (for global settings configuration)

## How It Works

```
Admin registers an Azure AD app and configures ClientId + TenantId + ClientSecret
                           ↓
User clicks "Connect Azure" on their Profile page
                           ↓
Connapse generates PKCE code_verifier + code_challenge (S256)
                           ↓
Browser redirects to Azure AD authorize endpoint with PKCE challenge
                           ↓
User authenticates with Microsoft → Azure returns authorization code
                           ↓
Connapse exchanges code for ID token (with client_secret + code_verifier)
                           ↓
ID token parsed for oid (Object ID) + tid (Tenant ID) + name
                           ↓
Identity linked — user can now access Azure Blob containers scoped to their RBAC
```

## Admin Configuration

### 1. Create an Azure AD App Registration

1. Open the [Azure Portal](https://portal.azure.com) > **Azure Active Directory** > **App registrations**
2. Click **New registration**
3. Configure:
   - **Name:** `Connapse` (or your preferred name)
   - **Supported account types:** Single tenant (this organization only)
   - **Redirect URI:** Select **Web** platform, enter `https://<your-connapse-host>/api/v1/auth/cloud/azure/callback`
4. Click **Register**
5. Copy the **Application (client) ID** and **Directory (tenant) ID**

> **Important:** Select the **Web** platform, not SPA. Connapse performs a server-side token exchange that requires client credentials.

### 2. Create a Client Secret

1. In your app registration, go to **Certificates & secrets**
2. Click **New client secret**
3. Set a description and expiry (e.g., 24 months)
4. Copy the **Value** immediately (it won't be shown again)

### 3. Configure in Connapse

1. Go to **Settings** > **Azure AD** tab
2. Enter:
   - **Client ID:** Application (client) ID from step 1
   - **Tenant ID:** Directory (tenant) ID from step 1
   - **Client Secret:** Secret value from step 2
3. Click **Test Connection** to verify the tenant is reachable
4. Click **Save Changes**

**What "Test Connection" does:** Fetches the Azure AD OIDC metadata endpoint (`/.well-known/openid-configuration`) to verify the tenant ID is valid and the authorization server is accessible. This does not validate the client secret.

### Settings Reference

| Field | Required | Description |
|-------|----------|-------------|
| Client ID | Yes | Application (client) ID from Azure AD app registration |
| Tenant ID | Yes | Directory (tenant) ID from Azure AD |
| Client Secret | Yes | Client secret value from Certificates & secrets |

**API:**
```http
GET  /api/settings/azuread          # Read current settings
PUT  /api/settings/azuread          # Update settings
POST /api/settings/test-connection   # Test with category "azuread"
```

## User Flow

### 1. Connect Azure

1. Navigate to your **Profile** page (click your username in the navigation)
2. In the **Cloud Identities** section, find the **Microsoft Azure** card
3. Click **Connect Azure**

### 2. Authenticate

Your browser redirects to Microsoft's login page:
1. Sign in with your Microsoft account
2. Grant consent if prompted (scopes: `openid profile`)
3. You're automatically redirected back to Connapse

### 3. Identity Linked

After authentication, the Profile page shows your connected identity:
- **Display Name** — from the `name` claim in your ID token
- **Tenant ID** — your Azure AD directory
- **Object ID** — your unique identifier in Azure AD
- **Connected date** / **Last used date**

### 4. Disconnect

To remove your Azure identity:
1. On the Profile page, click **Disconnect** on the Azure card
2. Confirm the disconnection
3. Cached scope entries are evicted immediately

## How Scope Enforcement Works

When you access an Azure Blob container, Connapse checks your linked Azure identity:

1. **Identity Check:** Your Object ID is retrieved from the stored cloud identity
2. **Connectivity Verification:** Connapse verifies the Azure Blob container is accessible using `DefaultAzureCredential`
3. **Prefix Scoping:** Access is granted to the configured prefix within the blob container (or the full container if no prefix is set)
4. **Scope Cache:** Results are cached per user + container (15-minute allow TTL, 5-minute deny TTL)
5. **Cache Eviction:** Disconnecting your Azure identity immediately evicts all cached scope entries

**Affected endpoints:**
- Document upload/download/delete
- Search (scope filter injected as path prefix)
- Folder listing
- Sync trigger

## Security Details

### PKCE (Proof Key for Code Exchange)

Even though Connapse is a confidential client (has a client secret), it also sends PKCE parameters for defense in depth:

- **code_verifier:** 32 random bytes, Base64-URL encoded
- **code_challenge:** SHA256 of the code_verifier, Base64-URL encoded
- **Method:** S256

The code_verifier is stored in an HttpOnly, Secure cookie (`__connapse_az_pkce`) during the authorization flow and sent during token exchange.

### CSRF Protection

A random state parameter is generated and stored in an HttpOnly, Secure cookie (`__connapse_az_state`). The callback endpoint validates the state matches before processing the authorization code.

Both cookies:
- HttpOnly (not accessible to JavaScript)
- Secure (HTTPS only)
- SameSite=Lax
- 10-minute TTL
- Scoped to `/api/v1/auth/cloud/azure` path

### Token Exchange

The token exchange sends **both** `client_secret` and `code_verifier` to the Azure token endpoint:
- `client_secret` — proves the client identity (confidential client requirement)
- `code_verifier` — proves the authorization request originated from this session (PKCE)

The returned ID token is parsed for claims (`oid`, `tid`, `name`) without external JWKS validation — the Azure token endpoint is the authoritative source.

## Troubleshooting

### "Azure AD is not configured"
An admin must set the Client ID, Tenant ID, and Client Secret in Settings > Azure AD.

### Test Connection fails
The Tenant ID is invalid or the Connapse server can't reach `login.microsoftonline.com`. Verify:
- The Tenant ID is a valid GUID from your Azure AD directory
- Network access to `login.microsoftonline.com` is not blocked

### "Invalid or expired state parameter"
The CSRF state cookie expired (10-minute TTL). Try connecting again. This can also happen if:
- You have cookies disabled
- Your browser blocked the HttpOnly cookie
- You waited too long on the Azure login page

### "Missing PKCE code verifier"
The PKCE cookie was not preserved across the redirect. Ensure:
- Cookies are enabled for your Connapse domain
- You're accessing Connapse over HTTPS (Secure cookies require HTTPS)
- Your browser is not in a strict privacy mode that blocks cookies

### Token exchange fails with 401
The client secret is invalid or expired. Generate a new client secret in the Azure portal and update the Connapse settings.

### "Azure ID token missing 'oid' claim"
The app registration may not have the correct permissions. Ensure:
- The `openid` and `profile` scopes are granted
- The app registration is configured for the **Web** platform (not SPA)

### Azure Blob container access denied after connecting
- Verify the user has appropriate RBAC roles on the storage account (e.g., `Storage Blob Data Reader`)
- Check the container's connector config has the correct `storageAccountName` and `containerName`
- The scope cache expires after 15 minutes — wait or disconnect/reconnect to force refresh
- If using managed identity, verify the `managedIdentityClientId` is correct
