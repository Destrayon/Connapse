# Atlassian Setup

> Part of [Connapse](https://github.com/Destrayon/Connapse) — open-source AI knowledge management platform.

Connapse reads Confluence Cloud and Jira Cloud in two parts, set up once each:

- A **linking app**: an Atlassian OAuth 2.0 integration that people sign in through to link their
  Atlassian account to Connapse. It only reads who each person is. Search uses the link to show each
  person only the Confluence pages and Jira issues they may read.
- A **service account** per site: an Atlassian account that is not a person, with an OAuth 2.0
  credential. Connapse reads the site as this account and asks Confluence or Jira, as this account,
  what each linked person may read.

Each site becomes a **connection** per product, a Confluence site and a Jira site, and each Confluence
space or Jira project you choose becomes a **source**.
Atlassian content appears in search only for people who have linked their Atlassian account under
**Profile → Integrations**, and only what Confluence or Jira lets them read. People who have not linked
an account see no Confluence or Jira results at all.

The linking app is set up on **Admin → Providers → Atlassian** (`/admin/providers/atlassian`). Sites
are added on **Connections**, and spaces and projects on **Sources → New source**.

## Before you start

| Need | Why |
|------|-----|
| For Confluence: a Confluence Cloud site, on the **Standard** plan or above | The Free plan has no page restrictions or private spaces, so there is nothing per person to enforce. Data Center and Server are not supported |
| For Jira: Jira Cloud on the same site, on any plan | Free works, but it can't edit project roles, so comments can't be restricted to roles you create. Data Center is not supported |
| An Atlassian **organization admin**, in an organization that uses **centralized user management** | Only they can create service accounts and their credentials. Each organization gets five free service accounts; they don't take a product seat |
| For Jira: a **Jira admin** | Only they can change permission schemes and space (project) roles (step 2.4). The organization admin often is one |
| An [Atlassian developer console](https://developer.atlassian.com/console/myapps/) account | The linking app is created there |
| A **privacy policy URL** for your organization | Atlassian asks for one before the linking app can be shared with anyone but you |
| Connapse opened at the address your users use, served over **HTTPS** (or reached on `localhost`) | Atlassian accepts only an HTTPS callback URL, except for `localhost` |
| Outbound HTTPS from the Connapse host to `auth.atlassian.com`, `api.atlassian.com` and `your-site.atlassian.net` | Every read and check goes there |

For a new setup, do steps 1 to 3 in order, then step 4 for Confluence, step 5 for Jira, or both: the
site check in step 3 needs the service account from step 2, and nobody sees results until step 1 is
done and they have linked their account. If Confluence already works and you are adding Jira, follow
[Add Jira to an existing Confluence setup](#add-jira-to-an-existing-confluence-setup) instead.

## Add Jira to an existing Confluence setup

These are all the steps when Confluence is already set up in Connapse and Jira has been added to the
site. The linking app, the service account and each person's linked account stay as they are. Each step
says where it happens and who must do it.

Jira on Premium sites now says **space** where these steps say **project**: **Space settings**, **Space
roles** and **Browse spaces** are **Project settings**, **Project roles** and **Browse projects** on
other sites. Where a label is shown, both words are given.

**In Atlassian Administration ([admin.atlassian.com](https://admin.atlassian.com)). You must be an
organization admin.**

1. Go to **Directory** → **Service accounts** and open the service account. In its app roles, set:
   - **Jira** to **User**. This gives access to Jira; admin rights alone don't include it.
   - **Jira Administration** to **App admin**. This is Jira's admin role, and App admin is the only
     role offered on that row.
   - Leave **User access admin** as it is: it only manages who can use the app.
2. On the same service account, choose **Create credentials** → **OAuth 2.0** → **Next**. On the scopes
   screen, search for and tick each Jira scope listed in [step 2.5](#2-service-account), then **Next** →
   **Create**.
   - The **Jira site** form in Connapse has a **Copy required scopes** button. If a source will include
     attachments, use **Copy with the attachments permission** instead, which adds `read:attachment:jira`.
   - Copy the **client ID** and **client secret**. Atlassian shows them once and can't recover them.
   - Make this a separate credential for Jira, and leave the Confluence connection's credential alone.
     Use one credential per product.
   - Scopes can't be edited later. To change them, create a new credential, use **Replace credentials**
     on the connection in Connapse, then **Revoke** the old credential.

**In Jira. You must be a Jira admin.** Jira's settings are behind the gear icon at the top right:
choose **Jira admin settings**, and the sidebar opens on **System**. A space's own settings are behind
**•••** next to the space's name at the top of the space → **Space settings**.

3. Leave **Global permissions** as it is. It grants **Administer Jira** and **Browse users and groups**
   to groups (`jira-admins-<site>`, `org-admins`, `jira-users-<site>`), and **App admin** from step 1
   gives the service account admin rights without adding it to any of them, so **View Users** there
   won't list it. That's expected: App admin is enough. Connapse's site check confirms it instead, at **Checking Jira
   access permissions** (step 5). Only if that check fails, an organization admin adds the service
   account to `jira-admins-<site>` in Atlassian Administration (**Directory** → **Groups** →
   `jira-admins-<site>` → **Add group members**, or **Directory** → **Service accounts** → **•••** →
   **Add to group**), and you test again.
4. Check that the service account can see every space you will index. A space it can't see isn't
   listed when you add a source, so you can also skip this and add it if a space is missing.
   - **Team-managed spaces** have no permission by that name; access comes from the space's access
     setting under **•••** next to the space's name → **Space settings** → **Access**. **Open** or
     **Limited** lets everyone with Jira access see the space, so step 1 is enough. **Private** needs the
     service account added there, as **Viewer** or higher.
   - **Company-managed spaces** use a permission scheme: gear icon → **Jira admin settings** → **Work
     items** → **Permission schemes** → the space's scheme → **Browse Spaces** (**Browse Projects**). The
     default scheme grants it to everyone with Jira access, so step 1 is enough.

   Then add the service account to every group and space role your team restricts comments to (step
   2.4 explains why): groups under **Directory** → **Groups** in Atlassian Administration, space roles
   under **Space settings** → **Access**. Jira creates a starter space when it's added to a site; leave
   it alone unless you will index it.

**In Connapse. You must be a Connapse admin.**

5. Go to **Connections** → **Add connection** → **Jira site** (or **Add a Jira site** on the Atlassian
   provider page). Enter the site URL and the new credential's client ID and secret, then **Check and
   save**. All seven checks should pass.
   - A site with no project yet only warns at **Listing Jira projects**, and can be saved with **Save
     anyway**.
   - A site with no project role the service account can read fails at **Reading Jira groups and
     project roles**: see [Can't read Jira membership](#cant-read-jira-membership).
6. Go to **Sources** → **New source**, choose **Jira · your-site.atlassian.net**, tick the projects,
   turn on **Include attachments** only if the credential has `read:attachment:jira`, then **Create source**. A site saved with **Save anyway** offers its
   projects too.
7. People who already linked their Atlassian account for Confluence need to do nothing: the same link
   covers Jira.

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

**What it is:** the account Connapse reads the site as. For Confluence it needs Confluence's admin role,
to ask what each person may read, and view access to every space you index. For Jira it needs Jira
access, the **Administer Jira** and **Browse users and groups** global permissions, and permission to
browse every project you index.

**If a service account already exists for this site, keep it.** One service account can serve both
products, with one credential per product: add the site twice, once as each connection. A Confluence
site and a Jira site are separate connections on the Connections page (step 3), each tested for its own
product only. Give the existing service account the other product's role (2.2) and access (2.3 or 2.4),
and create a second credential for that product (2.5). Don't add the other product's scopes to the
credential you already use.

In [Atlassian Administration](https://admin.atlassian.com), as an organization admin:

1. Go to **Directory** → **Service accounts** → **Create a service account**. Name it with 6 to 30
   letters or digits, for example `connapse`.
2. On **Select app roles**, set **Confluence** to its admin role if you will index Confluence, and
   if you will index Jira, **Jira** to **User** (access to Jira) and **Jira Administration** to **App
   admin** (Jira's admin role, which holds Administer Jira). Leave every other app at **None**, and leave
   **User access admin** as it is. Connapse needs admin rights to check other people's access, and each
   site's check in step 3 confirms them. You can change the role later under **Directory** → **Service
   accounts**, or add the account to the site's Confluence admin group under **Directory** → **Groups**.
3. **For Confluence**, in **every space you will index**, give the service account (or a group it is in)
   the **Viewer** role: open the space, choose **•••** next to its name in the sidebar → **Space
   settings** → **Users**, add the service account, and choose **Viewer**. On older sites without space
   roles the page is **Permissions**, where it needs **View**. Being an admin does not grant this.
4. **For Jira**, the service account needs the **Administer Jira** and **Browse users and groups**
   global permissions, so Connapse can ask Jira what each person may read. **App admin** on **Jira
   Administration** (2.2) gives them without adding the account to any group, so Jira's **Global
   permissions** page can't show it; the Jira site's check in step 3 (**Checking Jira access
   permissions**) confirms it. If that check fails, add the service account to `jira-admins-<site>`:
   **Directory** → **Groups** → `jira-admins-<site>` → **Add group members**, or **Directory** →
   **Service accounts** → **•••** → **Add to group**.

   The service account must be able to see every space (project) you will index:

   - **Team-managed spaces** have no permission by that name; access comes from the space's access
     setting under **•••** next to the space's name → **Space settings** → **Access**. **Open** or
     **Limited** lets everyone with Jira access see the space, so step 1 is enough. **Private** needs the
     service account added there, as **Viewer** or higher.
   - **Company-managed spaces** use a permission scheme: gear icon → **Jira admin settings** → **Work
     items** → **Permission schemes** → the space's scheme → **Browse Spaces** (**Browse Projects**). The
     default scheme grants it to everyone with Jira access, so step 1 is enough.

   Then add the service account to **every group and project role your team restricts comments
   to**: a comment restricted to a group or role the service account isn't in is never indexed, and
   Connapse can't tell it exists.
5. Back on the service account, choose **Create credentials** → **OAuth 2.0** → **Next**, one credential
   per product. On **Select scopes**, paste the product's required scopes into the scope search box and
   tick each one, then **Next** → **Create**. The **Copy required scopes** button on each site form
   (step 3) copies that product's list. Scopes can't be edited after the credential is created: to change
   them, create a new credential, use **Replace credentials** on the site's connection, then **Revoke**
   the old credential.

   **Confluence**: the list narrows to exactly these six.

   ```
   read:space:confluence read:page:confluence read:comment:confluence read:folder:confluence read:content-details:confluence read:content.permission:confluence
   ```

   Only if a source will include attachments, also add `read:attachment:confluence` (**Copy with the
   attachments permission** copies all seven). You can add it later, when a source first needs it: see
   [Missing scope](#missing-scope).

   | Scope | Used for |
   |-------|----------|
   | `read:space:confluence` | Listing the spaces you choose from |
   | `read:page:confluence` | Indexing pages and blog posts |
   | `read:comment:confluence` | Indexing comments |
   | `read:attachment:confluence` | Indexing attachments (optional: needed only if a source includes attachments; see [step 4](#4-confluence-sources)) |
   | `read:folder:confluence` | Following folders in the page tree |
   | `read:content-details:confluence` | Finding what changed since the last sync and looking up people |
   | `read:content.permission:confluence` | Checking what each person may read |

   **Jira**: these 25.

   ```
   read:project:jira read:issue:jira read:issue-details:jira read:comment:jira read:permission:jira read:group:jira read:project-role:jira read:issue-security-level:jira read:user:jira read:application-role:jira read:avatar:jira read:audit-log:jira read:comment.property:jira read:field:jira read:field-configuration:jira read:issue-meta:jira read:issue-type:jira read:issue-type-hierarchy:jira read:issue.changelog:jira read:issue.vote:jira read:project-category:jira read:project-version:jira read:project.component:jira read:project.property:jira read:status:jira
   ```

   Only if a Jira source will include attachments, also add `read:attachment:jira` (**Copy with the
   attachments permission** on the Jira site form adds it). These are the granular scopes the Jira calls
   Connapse makes require.
6. Create the credential, then copy the **client ID** and **secret**. Atlassian shows them only once.

**Pages with view restrictions that don't include the service account are never indexed.** To index
one, add the service account (or one of its groups) to the page's restrictions.

## 3. Site connection

Each product of a site is its own connection: a **Confluence site** and a **Jira site**. To index both
products of one site, add it twice, once as each, each with its own product's credential (step 2.5).
Each connection is tested for its own product only, keeps its own status, and
holds only its own product's sources.

Both use the same fields:

| Field | Required | Where to get it |
|-------|----------|-----------------|
| Site URL | Yes | The address in your browser when you open Confluence or Jira, such as `https://acme.atlassian.net` |
| Client ID | Yes | The service account's OAuth 2.0 credential, from step 2: one string of letters and digits, with no spaces |
| Client secret | Yes | One long string of characters, shown once when the credential is created in step 2. Stored encrypted and never shown again |

**Look up site** shows the site's **Cloud ID**, Atlassian's permanent identifier for the site, so you
can confirm it's the right one before entering the credential.

A failure names the check, says what to do, and links to its entry under
[Troubleshooting](#troubleshooting); **Details** shows Atlassian's own answer. If the test fails, the
secret is kept for your next attempt. A brand-new site with no pages, projects or other users can be
saved with **Save anyway**. It is marked with a warning, and still offers its spaces or projects on
**Sources → New source**.

### Add a Confluence site

On **Connections → Add connection**, choose **Confluence site**, or choose **Add a Confluence site** on
the Atlassian provider page. The form lists the Confluence scopes and the steps for Confluence only.

**Check and save** runs six checks in order: **Finding the site**, **Signing in as the service
account**, **Reading Confluence**, **Listing spaces**, **Reading comments, folders and changes** (one
small read for each of `read:comment:confluence`, `read:folder:confluence` and
`read:content-details:confluence`, so a credential missing one fails here rather than at the first
sync), and **Checking access permissions** (the admin role). The optional `read:attachment:confluence`
is tried too: the test records whether it is there but never fails without it. The site is saved when
every check passes, named after its host.

### Add a Jira site

On **Connections → Add connection**, choose **Jira site**, or choose **Add a Jira site** on the Atlassian
provider page. The form lists the Jira scopes and the steps for Jira only.

**Check and save** runs **Finding the site**, **Signing in as the service account**, then **Reading
Jira**, **Listing Jira projects**, **Reading issues and comments**, **Checking Jira access permissions**
(Administer Jira) and **Reading Jira groups and project roles**. The optional `read:attachment:jira` is
tried too, and never fails the test. The site is saved when every check passes, named after its host
with **(Jira)** added, for example `acme.atlassian.net (Jira)`, so it stands apart from the site's
Confluence connection. A site has at most one connection of each product.

### A saved site

The Connections list names each site's product beside its host, for example **Confluence ·
acme.atlassian.net**. A saved site shows its status and when it was last checked. **Test connection**
checks it again without entering the secret. **Replace credentials** tests a new client ID and secret
and switches to them only if the test passes, so the site's sources keep working. A site whose
credential stops working is marked **Needs new credentials**. Deleting a site requires typing its name,
and is refused while any source uses it: the refusal lists each of those sources, linked to its page.
Deleting a site leaves the service account and its credential in Atlassian. To revoke the credential,
open Atlassian Administration → **Directory** → **Service accounts**, choose the service account, and
delete its OAuth 2.0 credential.

## 4. Confluence sources

On **Sources → New source**, choose the site's Confluence connection. The dialog lists the site's spaces
that the service account can view.

| Field | Default | Notes |
|-------|---------|-------|
| Spaces | None ticked | Tick one or more. Each becomes its own source named `<site host> / <space name>`. A space that already has a source on this site shows **already added**. A space missing from the list is one the service account can't view: give it the **Viewer** role in that space (step 2) |
| Include personal spaces | Off | Also lists people's personal spaces |
| Include attachments | On if the site's last test confirmed `read:attachment:confluence`, otherwise Off | Indexes files attached to pages, when Connapse can read their file type. Needs the optional `read:attachment:confluence` scope (step 2.5) |
| Max attachment size (MB) | 25 | Attachments larger than this are skipped. 1 to 100 MB |

Choose **Create source**. The sources sync on the next cycle, or press **Sync**. Comments and blog posts are
always included.

**Attachments and their scope.** When the site's last **Test connection** did not confirm the
`read:attachment:confluence` scope, **Include attachments** starts off and the dialog says why. You can
still turn it on; the New source dialog and the source's edit page then say what to add and link to
**Replace credentials** on that site, opened on the Connections page. If Atlassian then refuses to list or download attachments, the source keeps syncing
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

## 5. Jira sources

Jira sources go on the site's Jira connection (step 3), once its last **Test connection** passed or it
was saved with **Save anyway**. On
**Sources → New source**, choose that connection, listed as **Jira · acme.atlassian.net**: the
dialog goes straight to its projects. A Confluence connection is never offered projects, and a Jira
connection never spaces.

| Field | Default | Notes |
|-------|---------|-------|
| Projects | None ticked | Tick one or more. Each becomes its own source named `<site host> / <project name> (Jira)`, so a Confluence space of the same name on the site keeps its own name. A project that already has a source on this site shows **already added**. A project missing from the list is one the service account can't see: on a private team-managed space, add it as **Viewer** under **Space settings** → **Access**; on a company-managed space, grant it **Browse Spaces** (**Browse Projects**) in the permission scheme (step 2.4) |
| Include attachments | On if the site's last test confirmed `read:attachment:jira`, otherwise Off | Indexes files attached to issues, when Connapse can read their file type. Needs the optional `read:attachment:jira` scope (step 2.5) |
| Max attachment size (MB) | 25 | Attachments larger than this are skipped. 1 to 100 MB |

**What a Jira source indexes.** Each issue is one document: its summary, description, fields and the
comments everyone who can see the issue can read. Search shows an issue only to a person whose linked
Atlassian account Jira lets browse it, checked live for every search.

**Restricted comments and internal notes.** A comment restricted to a group or a project role is indexed
as its own document, and search shows it only to people Jira lists in that group or role. A Jira Service
Management internal note is its own document too, shown only to the project's service desk agents. A
restriction Connapse can't read is treated as visible to no one, and a comment restricted to a group or
role the service account isn't in is never indexed (step 2.4).

**Attachments.** An attachment is shown to the people who can read where it was added: the issue, or the
restricted comment or internal note it was attached in. When Connapse can't tell which comment that was,
the attachment is shown to no one. As for Confluence, a site whose last test didn't confirm
`read:attachment:jira` starts with **Include attachments** off, and the dialog says what to add and links
to **Replace credentials**.

**Changing a source.** Choose **Edit** next to the source. You can change **Include attachments** and
**Max attachment size (MB)**; the project itself can't be changed: *A source covers one project; add a
new source for another project.* With the API, create a Jira source with `POST /api/sources` and a scope
of `{"kind":"jira-project","projectId":"10000"}`; list the project ids with
`GET /api/v1/atlassian/sites/{id}/projects`. The API runs the same checks as the dialog.

## Troubleshooting

### Site not found

**Symptom:** *Couldn't find an Atlassian Cloud site at …* or *The site address must look like
https://your-site.atlassian.net.*
**Likely cause:** the address is mistyped, is a custom domain, or the site is Data Center.
**Fix:** open Confluence or Jira in your browser and copy the `https://<name>.atlassian.net` part of the
address.

### Client ID or secret rejected

**Symptom:** *Atlassian rejected the client ID or secret.* while **Signing in as the service account**,
a site marked **Needs new credentials** on the Connections page after its credential stopped working,
or a Confluence or Jira source on the Sources page that failed with *Atlassian refused this site's service
account credential, so Connapse can't read the site.*
**Likely cause:** the values were swapped or partly copied, they come from the linking app instead of
the service account, or the credential was deleted or revoked.
**Fix:** in Atlassian Administration, open the service account, create a new OAuth 2.0 credential with
that product's scopes (step 2.5), and enter both values. For a saved site, use **Replace credentials**;
its sources keep working.

### Can't read Confluence

**Symptom:** *The credential can't read Confluence on this site.*, or a Confluence source that failed
with *Atlassian refused the service account's requests even with a new token.* The site is not marked **Needs new credentials**, because Atlassian accepted the credential itself.
**Likely cause:** the service account has no Confluence role on this site, or the credential was created
without the Confluence scopes.
**Fix:** set Confluence to its admin role on the service account (step 2.2). If it already has it,
create a new credential with the scopes in step 2.5.

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
check what other people may read.* while **Checking access permissions**, *The credential is missing the
read:comment:confluence scope* (or `read:folder:confluence`, or `read:content-details:confluence`) while
**Reading comments, folders and changes**, or *The credential can't read
Confluence on this site.* although the service account has a Confluence role. For attachments only: a
Confluence source shows *Attachments were skipped: the site's credential lacks the
read:attachment:confluence permission. Add it to the credential, or turn off Include attachments.* on
the Sources page, or the New source dialog says *the site's last connection test didn't confirm the
read:attachment:confluence permission*. For Jira attachments: a Jira source shows *Attachments were
skipped: the site's credential lacks the read:attachment:jira permission. Add it to the credential, or
turn off Include attachments.* For Jira: *The credential is missing the read:project:jira
scope* while **Listing Jira projects**, *The credential is missing the read:issue-details:jira scope*
(or `read:issue:jira`, or `read:comment:jira`) while **Reading issues and comments**, or *The credential
is missing the read:permission:jira scope* or *The service account can't list Jira users…* while
**Checking Jira access permissions**.
**Likely cause:** the credential was created without one of the scopes in step 2.5; listing Jira
users also needs the **Browse users and groups** global permission (step 2.4). For attachments, it
is the optional `read:attachment:confluence`, or `read:attachment:jira` for Jira.
**Fix:** scopes can't be added to an existing credential. Create a new OAuth 2.0 credential on the
service account, paste that product's scope list into the scope search and tick each one (step 2.5; for
attachments, use **Copy with the attachments permission**, which includes `read:attachment:confluence`
or `read:attachment:jira`), then use **Replace credentials** on the site with the new client ID
and secret, and **Revoke** the old credential. The replacement is tested before it is used, and its test
records which scopes it has, so new sources then start with attachments on. For **Browse users and
groups**, set **Jira Administration** to **App admin** on the service account (step 2.2). If you don't want attachments, turn off
**Include attachments** on the source instead (see [step 4](#4-confluence-sources) or
[step 5](#5-jira-sources)).

### Site has no pages or no other users yet

**Symptom:** *This site has no pages yet…* or *The site has no user other than the service account…*,
or for Jira, *The service account can't see any Jira project yet…* or *The site has no Jira user other
than the service account…*
**Likely cause:** the admin check asks Confluence whether another person can read a page, or Jira
whether another person can browse a project, so it needs at least one page or project the service
account can see and one other user.
**Fix:** choose **Save anyway** to save the site now; it is marked with a warning until the check can
run, and its spaces or projects can already be added as sources. Then, for Confluence, create a page in
a space the service account can view; for Jira, create a space or let the service account see one (step 2.4). If the site has only one user, invite one more with
access to that product. Then choose **Test connection**. On a Jira site with projects but no project
role the service account can read, the test fails at **Reading Jira groups and project roles** instead:
see [Can't read Jira membership](#cant-read-jira-membership).

### Can't read Jira

**Symptom:** *The credential can't read Jira on this site.* while **Reading Jira** or **Listing Jira
projects**, or a Jira source that failed with *Atlassian refused the service account's requests even
with a new token. Check that the service account has Jira access on this site…* The site is not marked
**Needs new credentials**, because Atlassian accepted the credential itself.
**Likely cause:** the service account has no Jira access on this site, or the credential was created
without the Jira scopes.
**Fix:** in Atlassian Administration, under **Directory** → **Service accounts**, set the service
account's **Jira** row to **User** (step 2.2): admin rights alone don't give access to Jira. If it is
already User, create a new credential with the Jira scopes (step 2.5) and use **Replace credentials**.

### Jira project missing

**Symptom:** **New source** lists no Jira projects (*This site has no projects the service account can
browse.*), or a project you expect isn't in the list.
**Likely cause:** the service account can't see that space (project).
**Fix:** on a private team-managed space, add the service account as **Viewer** under **•••** next to the
space's name → **Space settings** → **Access**. On a company-managed space, grant it **Browse Spaces**
(**Browse Projects**) in the space's permission scheme (step 2.4). Then reopen **New source**.

### Not Jira administrator

**Symptom:** *The service account doesn't have the Administer Jira permission, so Connapse can't check
what other people may read in Jira.* while **Checking Jira access permissions**.
**Likely cause:** the service account's **Jira Administration** row isn't **App admin**, or the
Administer Jira global permission didn't reach it, so it can't ask Jira what another person may browse.
**Fix:** in Atlassian Administration, under **Directory** → **Service accounts**, set **Jira** to
**User** and **Jira Administration** to **App admin**, and test again. If it still fails, add the
service account to the `jira-admins-<site>` group (**Directory** → **Groups** → `jira-admins-<site>` →
**Add group members**), which holds Administer Jira on Jira's **Global permissions** page, and test
again. If it already has
Administer Jira, see [Missing scope](#missing-scope): the credential may lack `read:permission:jira`.

### Can't read Jira membership

**Symptom:** *The service account can't read Jira group or project role membership…* while **Reading
Jira groups and project roles**.
**Likely cause:** the service account lacks Administer Jira, or the credential lacks
`read:group:jira` or `read:project-role:jira`. Connapse needs both to tell who may read a comment
restricted to a group or a project role.
**Fix:** set **Jira Administration** to **App admin** on the service account, or add it to
`jira-admins-<site>` (see [Not Jira administrator](#not-jira-administrator)). If it has it, create a new credential with the Jira
scopes (step 2.5) and use **Replace credentials**. If **Details** says *Jira listed no project role
Connapse could read*, the site has no project role yet: create one under the gear icon → **Jira admin settings** →
**System** → **Space roles** (**Project roles**), add the service account to it in a space under **•••**
next to the space's name → **Space settings** → **Access**, and test again.

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

### Project can no longer be read

**Symptom:** a Jira source on the Sources page failed with *Jira project … can no longer be read: the
service account lost access to it.* or *… it was deleted, or the service account can no longer see it.*
**Details** shows HTTP 403 or 404.
**Likely cause:** someone made the space private or removed the service account from it, changed its
permission scheme, or deleted or archived the project. The source's documents are
hidden from search, not deleted, until the project can be read again.
**Fix:** in the space, open **•••** next to its name → **Space settings** → **Access** (or the permission
scheme) and let the service account see the space again (step 2.4), then choose **Sync** on the source. If the project was
deleted, delete the source.

### Attachments skipped

**Symptom:** a Confluence or Jira source shows **Warning** on the Sources page with *… attachments were
skipped: larger than 25 MB, or a file type Connapse can't read.*
**Likely cause:** the attachments are larger than the source's **Max attachment size (MB)**, or they
are images or other files no parser can read. Pages, issues and comments still synced.
**Fix:** to include larger files, choose **Edit** on the source and raise **Max attachment size (MB)**
(up to 100). Files of an unreadable type stay skipped.

### Couldn't reach Atlassian

**Symptom:** **Check and save** or **Recheck** on the **Linking app** card answers *Couldn't reach
Atlassian…*, or a Confluence or Jira source failed with *Confluence on this site didn't answer
properly, so nothing changed this cycle.*, *Jira on this site didn't answer properly, so nothing changed
this cycle.*, *Atlassian's sign-in service answered with a redirect…* or *Atlassian's sign-in service answered without an access token.* These don't mark the site **Needs new credentials**: its credential may still be fine.
**Likely cause:** this server can't reach Atlassian over HTTPS (a firewall, a proxy, or DNS), or
Atlassian had a temporary outage. **Details** on the Sources page shows what came back.
**Fix:** check that the server can open `https://auth.atlassian.com` and `https://api.atlassian.com`,
and that no proxy rewrites or redirects those requests. Then try again; a source also retries on its
next sync.

## Removing Atlassian

1. Delete the Confluence and Jira sources, then the site's Confluence and Jira connections.
2. Delete the service account's credential, or the service account, in Atlassian Administration.
3. Delete the linking app in the developer console if nothing else uses it.

Related: [Confluence test site](confluence-test-site.md) sets up a small site that exercises every
permission case.
