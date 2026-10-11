# Atlassian Setup

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse reads Confluence Cloud in two parts, set up once each:

- A **linking app**: an Atlassian OAuth 2.0 integration that people sign in through to link their
  Atlassian account to Connapse. It only reads who each person is. Search uses the link to show each
  person only the Confluence pages they may read.
- A **service account** per site: an Atlassian account that is not a person, with an OAuth 2.0
  credential. Connapse reads the site as this account and asks Confluence, as this account, what each
  linked person may read.

Each site becomes a **connection**, and each Confluence space you choose becomes a **source**.
Confluence content appears in search only for people who have linked their Atlassian account under
**Profile → Integrations**, and only the pages Confluence lets them read. People who have not linked
an account see no Confluence results at all.

The linking app is set up on **Admin → Providers → Atlassian** (`/admin/providers/atlassian`). Sites
are added on **Connections**, and spaces on **Sources → New source**.

## Before you start

| Need | Why |
|------|-----|
| A Confluence Cloud site, on the **Standard** plan or above | The Free plan has no page restrictions or private spaces, so there is nothing per person to enforce. Data Center and Server are not supported |
| An Atlassian **organization admin**, in an organization that uses **centralized user management** | Only they can create service accounts. Each organization gets five free service accounts; they don't take a product seat |
| An [Atlassian developer console](https://developer.atlassian.com/console/myapps/) account | The linking app is created there |
| A **privacy policy URL** for your organization | Atlassian asks for one before the linking app can be shared with anyone but you |
| Connapse opened at the address your users use, served over **HTTPS** (or reached on `localhost`) | Atlassian accepts only an HTTPS callback URL, except for `localhost` |
| Outbound HTTPS from the Connapse host to `auth.atlassian.com`, `api.atlassian.com` and `your-site.atlassian.net` | Every read and check goes there |

Do the steps in order: the site check in step 3 needs the service account from step 2, and nobody sees
Confluence results until step 1 is done and they have linked their account.

## 1. Linking app

**What it is:** the app people sign in through on Atlassian so Connapse knows which Atlassian account
is theirs. It asks only for `read:me` and keeps no token: Connapse stores each person's Atlassian
account ID and nothing else.

On **Admin → Providers → Atlassian**, the **Linking app** card shows the callback URL to copy. Then,
in the [Atlassian developer console](https://developer.atlassian.com/console/myapps/):

1. Choose **Create** → **OAuth 2.0 integration**.
2. Name it, for example **Connapse – your organization**, so Atlassian and your users can see which
   software it is. Set **Access type** to **Account-level**, agree to the developer terms, and create
   it. Account-level means people don't have to pick a site when they sign in.
3. **Permissions**: next to **User identity API**, choose **Add**, then **Configure**, and add the
   `read:me` scope. Nothing else is needed.
4. **Authorization**: next to **OAuth 2.0 (3LO)** choose **Configure**, set the **Callback URL** to the
   one the card shows, and **Save changes**. It looks like
   `https://<your Connapse>/api/v1/auth/cloud/atlassian/callback`, and must be HTTPS unless Connapse
   runs on `localhost`.
5. **Distribution**: a new app is private, so only you can sign in through it. Choose **Sharing** and
   fill in:
   - **Vendor name**: your organization.
   - **Privacy policy URL**: your organization's privacy policy.
   - **Does your app store personal data?**: **No**. Connapse keeps only each linked person's
     Atlassian account ID, never their name or email.

   People then see a notice that Atlassian hasn't reviewed the app. That is expected.
6. **Settings**: copy the client ID and the secret.

Back on the card, paste them and choose **Save**:

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Client ID | Yes | The app's **Settings** page in the developer console |
| Client secret | Yes | The same page, labelled **Secret**. Stored encrypted and never shown again |

Then link your own account under **Profile → Integrations → Atlassian** to confirm the app works.

## 2. Service account

**What it is:** the account Connapse reads the site as. It needs Confluence's admin role, to ask what
each person may read, and view access to every space you index.

In [Atlassian Administration](https://admin.atlassian.com):

1. Go to **Directory** → **Service accounts** → **Create a service account**. Name it with 6 to 30
   letters or digits, for example `connapse`.
2. On **Select app roles**, set **Confluence** to its admin role and leave other apps at **None**.
   Connapse needs Confluence admin rights to check other people's access, and the site check in step 3
   confirms them. You can change the role later under **Directory** → **Service accounts**, or add the
   account to the site's Confluence admin group under **Directory** → **Groups**.
3. In **every space you will index**, give the service account (or a group it is in) the **Viewer**
   role: open the space, choose **•••** next to its name in the sidebar → **Space settings** →
   **Users**, add the service account, and choose **Viewer**. On older sites without space roles the
   page is **Permissions**, where it needs **View**. Being an admin does not grant this.
4. Back on the service account, choose **Create credentials** → **OAuth 2.0**. On **Select scopes**,
   paste this list into the scope search box. The list narrows to exactly these seven; tick each one:

   ```
   read:space:confluence read:page:confluence read:comment:confluence read:attachment:confluence read:folder:confluence read:content-details:confluence read:content.permission:confluence
   ```

   | Scope | Used for |
   |-------|----------|
   | `read:space:confluence` | Listing the spaces you choose from |
   | `read:page:confluence` | Indexing pages and blog posts |
   | `read:comment:confluence` | Indexing comments |
   | `read:attachment:confluence` | Indexing attachments |
   | `read:folder:confluence` | Following folders in the page tree |
   | `read:content-details:confluence` | Reading page restrictions |
   | `read:content.permission:confluence` | Checking what each person may read |

5. Create the credential, then copy the **client ID** and **secret**. The secret is shown only once.

**Pages with view restrictions that don't include the service account are never indexed.** To index
one, add the service account (or one of its groups) to the page's restrictions.

## 3. Site connection

On **Connections → Add connection**, choose **Atlassian site**, or follow **Connections page** from the
provider page.

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Site URL | Yes | The address in your browser when you open Confluence, such as `https://acme.atlassian.net` |
| Client ID | Yes | The service account's OAuth 2.0 credential, from step 2 |
| Client secret | Yes | Shown once when the credential is created in step 2. Stored encrypted and never shown again |

**Test and save** checks, in order, that the site exists, that Atlassian accepts the client ID and
secret, that the credential can read Confluence, that the service account can see at least one space,
and that it can check another person's access (the admin role). The site is saved only when every check
passes, named after its host. Any failure names the check that failed; see
[Troubleshooting](#troubleshooting).

An existing site can be checked again with **Test connection** without entering the secret. To change
the site or its credential, delete the connection and add the site again.

## 4. Confluence sources

On **Sources → New source**, choose the site's connection. The dialog lists the site's spaces that the
service account can view.

| Field | Default | Notes |
|-------|---------|-------|
| Spaces | None ticked | Tick one or more. Each becomes its own source named `<site host> / <space name>`. A space that already has a source on this site shows **already added**. A space missing from the list is one the service account can't view: give it the **Viewer** role in that space (step 2) |
| Include personal spaces | Off | Also lists people's personal spaces |
| Include attachments | On | Indexes files attached to pages, when Connapse can read their file type |
| Max attachment size (MB) | 25 | Attachments larger than this are skipped. 1 to 100 MB |

Choose **Create source**. The sources sync on the next cycle, or press **Sync**. Comments and blog posts are
always included.

Sources can also be created with `POST /api/sources`; see [Connections, Sources, and
Containers](connectors.md#configuration-by-provider). The API runs the same checks as the dialog.

## Troubleshooting

### Site not found

**Symptom:** *Couldn't find an Atlassian Cloud site at …* or *The site address must look like
https://your-site.atlassian.net.*
**Likely cause:** the address is mistyped, is a custom domain, or the site is Data Center.
**Fix:** open Confluence in your browser and copy the `https://<name>.atlassian.net` part of the address.

### Client ID or secret rejected

**Symptom:** *Atlassian rejected the client ID or secret.*
**Likely cause:** the values were swapped or partly copied, they come from the linking app instead of
the service account, or the credential was deleted.
**Fix:** in Atlassian Administration, open the service account, create a new OAuth 2.0 credential (step
2.4), and paste both values again.

### Can't read Confluence

**Symptom:** *The credential can't read Confluence on this site.*
**Likely cause:** the service account has no Confluence role on this site, or the credential was created
without the Confluence scopes.
**Fix:** set Confluence to its admin role on the service account (step 2.2). If it already has it,
create a new credential with all seven scopes (step 2.4).

### No Confluence access or no spaces

**Symptom:** *The service account has no Confluence access.* on the site check, or *This site has no
spaces the service account can see.* in the New source dialog.
**Likely cause:** the service account has not been given view access to any space. Admin rights do not
include it.
**Fix:** give the service account the **Viewer** role in each space you want to index (step 2.3), then
test again or reopen the dialog.

### Not Confluence Administrator

**Symptom:** *The service account isn't Confluence Administrator; Connapse can't check other users'
access without it.*
**Likely cause:** the service account's Confluence role is not the admin role.
**Fix:** in Atlassian Administration, under **Directory** → **Service accounts**, set its Confluence
role to admin, or add it to the site's Confluence admin group under **Directory** → **Groups**. Then
test again. If the role is already admin, see [Missing scope](#missing-scope).

### Missing scope

**Symptom:** *The service account can't list pages. Check that its app has the read:page:confluence
scope.*, or the admin check fails although the role is already admin.
**Likely cause:** the credential was created without one of the seven scopes. A missing
`read:content.permission:confluence` makes the access check fail the same way a missing admin role does.
**Fix:** create a new OAuth 2.0 credential on the service account, paste the scope list into the scope
search and tick all seven (step 2.4), then add the site with the new client ID and secret.

### Site has no pages or no other users yet

**Symptom:** *This site has no pages yet…* or *The site has no user other than the service account…*
**Likely cause:** the admin check asks Confluence whether another person can read a page, so it needs
at least one page the service account can view and one other user.
**Fix:** create a page in a space the service account can view, or invite one more user to the site,
then test again.

### Linking app rejected

**Symptom:** linking an account under **Profile → Integrations** ends with *Atlassian did not complete
the sign-in, so no account was linked*, or Atlassian's own page shows an error about the app or the
redirect URL.
**Likely cause:** the client ID or secret on the **Linking app** card is wrong, the callback URL in the
developer console doesn't match the one the card shows, or the app is not shared, so only its creator
can sign in.
**Fix:** compare the callback URL on the card with the one under **Authorization** in the developer
console. Check that **Distribution** → **Sharing** is on. If both match, paste the client ID and secret
from the app's **Settings** again.

### Sign-in failed

**Symptom:** one of these on **Profile → Integrations**:

| Message | Cause | Fix |
|---------|-------|-----|
| *Atlassian sign-in is not set up yet* | No linking app is saved | An administrator finishes [step 1](#1-linking-app) |
| *You cancelled on Atlassian* | Access was declined on Atlassian's page | Start again and choose **Accept** |
| *took too long or was started in another tab* | The sign-in expired | Start again from the same tab |
| *started by a different Connapse user* | Someone else's browser finished the sign-in | Sign in to Connapse as yourself and start again |

If a linked person still sees no Confluence results, check in Confluence that they can open the page,
that the page's space has a source, and that the source has synced.

## Removing Atlassian

1. Delete the Confluence sources, then the site connections.
2. Delete the service account's credential, or the service account, in Atlassian Administration.
3. Delete the linking app in the developer console if nothing else uses it.

Related: [Confluence test site](confluence-test-site.md) sets up a small site that exercises every
permission case.
