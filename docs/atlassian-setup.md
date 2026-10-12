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

Back on the card, paste them and choose **Check and save**. Connapse asks Atlassian to accept the pair
before it saves anything:

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Client ID | Yes | The app's **Settings** page in the developer console: one string of letters and digits, with no spaces |
| Client secret | Yes | The same page, labelled **Secret**: one long string of characters. Stored encrypted and never shown again |

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
   | `read:attachment:confluence` | Indexing attachments (optional: needed only if a source includes attachments; see [step 4](#4-confluence-sources)) |
   | `read:folder:confluence` | Following folders in the page tree |
   | `read:content-details:confluence` | Finding what changed since the last sync and looking up people |
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
| Client ID | Yes | The service account's OAuth 2.0 credential, from step 2: one string of letters and digits, with no spaces |
| Client secret | Yes | One long string of characters, shown once when the credential is created in step 2. Stored encrypted and never shown again |

**Look up site** shows the site's **Cloud ID**, Atlassian's permanent identifier for the site, so you
can confirm it's the right one before entering the credential.

**Test and save** runs five checks in order: **Finding the site**, **Signing in as the service
account**, **Reading Confluence**, **Listing spaces**, and **Checking access permissions** (the admin
role). The site is saved when every check passes, named after its host. A failure names the check, says
what to do, and links to its entry under [Troubleshooting](#troubleshooting); **Show details** shows
Atlassian's own answer. If the test fails, the secret is kept for your next attempt. A brand-new site
with no pages or no other users can be saved with **Save anyway**.

A saved site shows its status and when it was last checked. **Test connection** checks it again
without entering the secret. **Replace credentials** tests a new client ID and secret and switches to
them only if the test passes, so the site's sources keep working. A site whose credential stops
working is marked **Needs new credentials**. Deleting a site requires typing its name, and is refused
while any source uses it: the refusal lists each of those sources, linked to its page. Deleting a site
leaves the service account and its credential in Atlassian. To revoke the credential, open Atlassian
Administration → **Directory** → **Service accounts**, choose the service account, and delete its
OAuth 2.0 credential.

## 4. Confluence sources

On **Sources → New source**, choose the site's connection. The dialog lists the site's spaces that the
service account can view.

| Field | Default | Notes |
|-------|---------|-------|
| Spaces | None ticked | Tick one or more. Each becomes its own source named `<site host> / <space name>`. A space that already has a source on this site shows **already added**. A space missing from the list is one the service account can't view: give it the **Viewer** role in that space (step 2) |
| Include personal spaces | Off | Also lists people's personal spaces |
| Include attachments | On if the site's last test confirmed `read:attachment:confluence`, otherwise Off | Indexes files attached to pages, when Connapse can read their file type. Needs the optional `read:attachment:confluence` scope (step 2.4) |
| Max attachment size (MB) | 25 | Attachments larger than this are skipped. 1 to 100 MB |

Choose **Create source**. The sources sync on the next cycle, or press **Sync**. Comments and blog posts are
always included.

**Attachments and their scope.** When the site's last **Test connection** did not confirm the
`read:attachment:confluence` scope, **Include attachments** starts off and the dialog says why. You can
still turn it on. If Atlassian then refuses to list or download attachments, the source keeps syncing
its pages and comments, skips attachments for that sync, and shows this warning on the Sources page:
*Attachments were skipped: the site's credential lacks the read:attachment:confluence permission. Add it
to the credential, or turn off Include attachments.* The site itself is not marked **Needs new
credentials**, because its credential still works for everything else. To fix it, see [Missing
scope](#missing-scope).

**Changing a source.** Choose **Edit** next to the source on the Sources page. Its **Reads from** section shows the site,
the space and its key, and the attachment settings. You can change **Include attachments** and **Max
attachment size (MB)** there, under the same rules as the New source dialog; the next sync re-reads the
space with the new settings. The space itself can't be changed: *A source covers one space; add a new
source for another space.*

Sources can also be created with `POST /api/sources` and changed with `PATCH /api/sources/{id}`; see
[Connections, Sources, and Containers](connectors.md#configuration-by-provider) and the
[API reference](api.md). The API runs the same checks as the dialog, and leaving `includeAttachments`
out follows the same default.

## Troubleshooting

### Site not found

**Symptom:** *Couldn't find an Atlassian Cloud site at …* or *The site address must look like
https://your-site.atlassian.net.*
**Likely cause:** the address is mistyped, is a custom domain, or the site is Data Center.
**Fix:** open Confluence in your browser and copy the `https://<name>.atlassian.net` part of the address.

### Client ID or secret rejected

**Symptom:** *Atlassian rejected the client ID or secret.* while **Signing in as the service account**,
a site marked **Needs new credentials** on the Connections page after its credential stopped working,
or a Confluence source on the Sources page that failed with *Atlassian refused this site's service
account credential, so Connapse can't read the site.*
**Likely cause:** the values were swapped or partly copied, they come from the linking app instead of
the service account, or the credential was deleted or revoked.
**Fix:** in Atlassian Administration, open the service account, create a new OAuth 2.0 credential (step
2.4), and enter both values. For a saved site, use **Replace credentials**; its sources keep working.

### Can't read Confluence

**Symptom:** *The credential can't read Confluence on this site.*, or a Confluence source that failed
with *Atlassian refused the service account's requests even with a new token.*
**Likely cause:** the service account has no Confluence role on this site, or the credential was created
without the Confluence scopes.
**Fix:** set Confluence to its admin role on the service account (step 2.2). If it already has it,
create a new credential with the scopes in step 2.4.

### No Confluence access or no spaces

**Symptom:** *The service account can't see any Confluence space.* while **Listing spaces**, or *This
site has no spaces the service account can see.* in the New source dialog.
**Likely cause:** the service account has not been given view access to any space. Admin rights do not
include it.
**Fix:** give the service account the **Viewer** role in each space you want to index (step 2.3), then
test again or reopen the dialog.

### Not Confluence Administrator

**Symptom:** *The service account isn't Confluence Administrator, so Connapse can't check what other
people may read.* while **Checking access permissions**.
**Likely cause:** the service account's Confluence role is not the admin role.
**Fix:** in Atlassian Administration, under **Directory** → **Service accounts**, set its Confluence
role to admin, or add it to the site's Confluence admin group under **Directory** → **Groups**. Then
test again. If the role is already admin, see [Missing scope](#missing-scope).

### Missing scope

**Symptom:** *The credential is missing the read:content.permission:confluence scope, so Connapse can't
check what other people may read.* while **Checking access permissions**, or *The credential can't read
Confluence on this site.* although the service account has a Confluence role. For attachments only: a
Confluence source shows *Attachments were skipped: the site's credential lacks the
read:attachment:confluence permission. Add it to the credential, or turn off Include attachments.* on
the Sources page, or the New source dialog says *the site's last connection test didn't confirm the
read:attachment:confluence permission*.
**Likely cause:** the credential was created without one of the scopes in step 2.4. For attachments, it
is the optional `read:attachment:confluence`.
**Fix:** create a new OAuth 2.0 credential on the service account, paste the scope list into the scope
search and tick each one (step 2.4), then use **Replace credentials** on the site with the new client ID
and secret. The replacement is tested before it is used, and its test records which scopes it has, so
new sources then start with attachments on. If you don't want
attachments, turn off **Include attachments** on the source instead (see [step 4](#4-confluence-sources)).

### Site has no pages or no other users yet

**Symptom:** *This site has no pages yet…* or *The site has no user other than the service account…*
**Likely cause:** the admin check asks Confluence whether another person can read a page, so it needs
at least one page the service account can view and one other user.
**Fix:** choose **Save anyway** to save the site now; it is marked with a warning until the check can
run. Then create a page in a space the service account can view, or invite one more user to the site,
and choose **Test connection**.

### Linking app rejected

**Symptom:** one of these:
- linking an account under **Profile → Integrations** ends with *Could not connect your Atlassian
  account: Atlassian rejected Connapse's linking app. Tell an administrator to replace its client secret
  under Providers → Atlassian.*
- the **Linking app** card shows **Rejected by Atlassian**, or *Atlassian rejected the stored client ID
  or secret, so nobody new can link an account. Choose Change the linking app and paste a new secret.*
- **Check and save** answers *Atlassian rejected this client ID and secret, so nothing was saved.*
- Atlassian's own page shows an error about the app or the redirect URL.

**Likely cause:** the client ID or secret on the **Linking app** card is wrong or was rotated in the
developer console, the callback URL in the developer console doesn't match the one the card shows, or
the app is not shared, so only its creator can sign in.
**Fix:** on the **Linking app** card, choose **Change the linking app** and paste the client ID and
secret from the app's **Settings** again. Compare the callback URL on the card with the one under
**Authorization** in the developer console, and check that **Distribution** → **Sharing** is on.

### Sign-in failed

**Symptom:** one of these on **Profile → Integrations**:

| Message | Cause | Fix |
|---------|-------|-----|
| *Could not connect your Atlassian account: Atlassian sign-in is not set up yet. An administrator sets up the linking app under Providers.* | No linking app is saved | An administrator finishes [step 1](#1-linking-app) |
| *Could not connect your Atlassian account: an administrator has turned Atlassian sign-in off. Accounts already linked stay linked.* | Sign-in is paused on the **Linking app** card | An administrator chooses **Turn on Atlassian sign-in** on the **Linking app** card |
| *Could not connect your Atlassian account: Atlassian rejected Connapse's linking app. Tell an administrator to replace its client secret under Providers → Atlassian.* | Atlassian refused the linking app's client ID or secret | See [Linking app rejected](#linking-app-rejected) |
| *You cancelled on Atlassian, so no account was linked.* | Access was declined on Atlassian's page | Start again and choose **Accept** |
| *That Atlassian sign-in took too long or was started in another tab. Start it again.* | The sign-in expired, or it was finished in a different tab or browser | Start again, and finish in the same tab |
| *That Atlassian sign-in was started by a different Connapse user, so nothing was linked. Sign in to Connapse as yourself and start again.* | Someone else's Connapse session finished the sign-in | Sign in to Connapse as yourself and start again |
| *You disconnected Atlassian while the sign-in was finishing, so nothing was linked. Start it again to link.* | **Disconnect** was chosen while the sign-in was still finishing | Start again to link |
| *Atlassian did not complete the sign-in, so no account was linked. Try again; if it keeps failing, tell an administrator.* | Atlassian returned no sign-in code, or the sign-in failed for another reason (the server log has the detail) | Try again. If it keeps failing, an administrator checks the server log and [Linking app rejected](#linking-app-rejected) |

If a linked person still sees no Confluence results, check in Confluence that they can open the page,
that the page's space has a source, and that the source has synced.

### Space can no longer be read

**Symptom:** a Confluence source on the Sources page failed with *Confluence space … can no longer be
read: the service account lost access to it.* or *… it was deleted, or the service account can no longer
see it.* **Details** shows HTTP 403 or 404.
**Likely cause:** someone removed the service account's **Viewer** role from the space, changed the
space's permissions, or deleted or archived the space. The source's documents are hidden from search,
not deleted, until the space can be read again.
**Fix:** in the space, open **Space settings** → **Users** and give the service account the **Viewer**
role again (step 2.3), then choose **Sync** on the source. If the space was deleted, delete the source.

### Attachments skipped

**Symptom:** a Confluence source shows **Warning** on the Sources page with *… attachments were skipped:
larger than 25 MB, or a file type Connapse can't read.*
**Likely cause:** the attachments are larger than the source's **Max attachment size (MB)**, or they
are images or other files no parser can read. Pages and comments still synced.
**Fix:** to include larger files, choose **Edit** on the source and raise **Max attachment size (MB)**
(up to 100). Files of an unreadable type stay skipped.

### Couldn't reach Atlassian

**Symptom:** **Check and save** or **Recheck** on the **Linking app** card answers *Couldn't reach
Atlassian…*, or a Confluence source failed with *Confluence on this site didn't answer properly, so
nothing changed this cycle.* or *Atlassian's sign-in service answered with a redirect…*
**Likely cause:** this server can't reach Atlassian over HTTPS (a firewall, a proxy, or DNS), or
Atlassian had a temporary outage. **Details** on the Sources page shows what came back.
**Fix:** check that the server can open `https://auth.atlassian.com` and `https://api.atlassian.com`,
and that no proxy rewrites or redirects those requests. Then try again; a source also retries on its
next sync.

## Removing Atlassian

1. Delete the Confluence sources, then the site connections.
2. Delete the service account's credential, or the service account, in Atlassian Administration.
3. Delete the linking app in the developer console if nothing else uses it.

Related: [Confluence test site](confluence-test-site.md) sets up a small site that exercises every
permission case.
