# How do you set up a Jira Cloud site for Connapse's Jira connector, step by step, as of October 2026?
**Date:** 2026-10-13
**Status:** Reviewed (inline adversarial pass)
**Built on:** atlassian-setup-guide-2026-10-10.md, jira-restricted-comments-2026-10-12.md

## Executive summary

Connapse's Jira setup can be done in about a dozen clicks across four places:
- **Atlassian Administration:** two roles and one credential.
- **Jira settings:** confirm two global permissions.
- **A terminal:** only for the test seed.
- **Connapse:** the connection and the source.

Three things make the current instructions hard to follow:
- **Jira on Premium sites now says "space" where the docs say "project"** (since 2025-10-20), so "Project settings" and "Project roles" are "Space settings" and "Space roles".
- **Jira splits access and admin into two rows:** **Jira → User** and **Jira Administration → App admin**. Five places in Connapse still say "set Jira to its admin role".
- **Steps for the two products are interleaved** with no single "add Jira to an existing Confluence setup" path.

The main uncertainty: no Atlassian page states outright that App admin on Jira Administration grants the **Administer Jira** global permission. The walkthrough therefore has the admin confirm it on Jira's Global permissions page.

## Research brief

**Question:** What exact, current steps give a Jira Cloud service account everything Connapse's Jira connector needs, and where do Connapse's docs diverge from them?

**Sub-questions:**
1. App roles and global permissions: Jira access, Administer Jira, Browse users and groups, and browse access to spaces.
2. The OAuth 2.0 credential for a service account and its scopes, plus the admin's own API token for the seed script.
3. Groups, space roles, Service Management agents and comment restrictions, for test data.
4. Where Connapse's docs and messages disagree with its code or with each other.

**Out of scope:** Jira Data Center; Connapse's code design.

**Success criteria:** a verified walkthrough with exact menu names, and a list of doc fixes.

## Verified walkthrough: add Jira to an existing Confluence setup

The walkthrough assumes a site that already has Confluence, a Connapse service account (for example `connapse`), and Jira newly added. Each step names where it happens.

**A. Atlassian Administration (admin.atlassian.com). You must be an organization admin.**
1. Go to **Directory → Service accounts** and open the service account. In its app roles, set:
   - **Jira** to **User**. This gives access to Jira; admin rights alone don't include it.
   - **Jira Administration** to **App admin**. This is the Jira admin role; App admin is the only role offered on that row.
   - Leave **User access admin** alone: it only manages who can use the app.
2. On the same service account, choose **Create credentials → OAuth 2.0 → Next**. On the scopes screen, search for and tick each Jira scope, then **Next → Create**.
   - Connapse's **Jira site** form has a **Copy required scopes** button, and **Copy with the attachments permission** adds `read:attachment:jira`.
   - Copy the **client ID** and **client secret**. Atlassian shows them once and can't recover them.
   - Make this a separate credential for Jira and leave the Confluence connection's credential alone. No Atlassian page confirms that one OAuth credential can carry both products' scopes.
   - Scopes can't be edited later. To change them, create a new credential, use **Replace credentials** in Connapse, then **Revoke** the old one.

**B. Jira (gear icon → System). You must be a Jira admin.**

3. Open **Settings (gear) → System → Global permissions** and check that the service account has two permissions:
   - **Administer Jira**, normally through the site's Jira admins group (`jira-admins-{site}`, or `jira-administrators` on older sites);
   - **Browse users and groups**.

   If either is missing, go to **Grant Permission** at the bottom, choose the permission and a group that contains the service account, then **Add**.

   To put the service account in a group, use Atlassian Administration → **Directory → Service accounts → ••• → Add to group**. Trust this page over the audit log, which can misreport grants (JRACLOUD-93726).
4. Browse access to spaces:
   - **Company-managed spaces** using the default permission scheme grant **Browse spaces** to anyone with Jira access, so step 1 is enough.
   - **A custom scheme or a Private team-managed space** needs the service account added: **Space settings → Access**, or the scheme's **Browse spaces** grant.

**C. Test data only: a terminal and Jira.**

5. Create a token for your own account at id.atlassian.com → **Security → API tokens → Create API token**. Choose the plain one, not **Create API token with scopes**: scoped tokens don't work against `https://{site}.atlassian.net`.
6. Find the account IDs the script needs:
   - **The service account's:** Atlassian Administration → **Directory → Service accounts** → open the account. The ID is the long value at the end of the page address.
   - **The second person's:** **Directory → Users** → open the person; same place.
7. Run `python -I eval/tools/seed_jira.py push --site connapseos.atlassian.net --service-account-id <id> --second-user-id <id> --dry-run`, then run it again without `--dry-run`. It needs `JIRA_EMAIL` and `JIRA_API_TOKEN` set.
8. Do the manual steps the script prints. Then confirm in **Settings → System → Space roles** that **Developers** and **QA/Ops Team** exist. New sites may have only **Administrators**, so create any role that's missing.
9. In the ENG space, open **Space settings → Access** and confirm the service account is in both roles. A new role has no permissions until it is added to the permission scheme. Comment visibility only needs membership.
10. Service Management, if added: in the service space, choose **Invite team**, which adds the person to the **Service Space Team** role, and invite the service account. No Atlassian page confirms a service account can be an agent; if the invite fails, skip internal notes.

**D. Connapse (http://localhost:5001). You must be a Connapse admin.**

11. Go to **Connections → Add connection → Jira site**. Don't use the Atlassian provider card's button, which opens the Confluence form. Enter `https://connapseos.atlassian.net` and the client ID and secret, then **Check and save**. All seven checks should pass.
    - Run this after seeding. With no space, **Listing Jira projects** only warns. With no readable role, **Reading Jira groups and project roles** fails.
12. Go to **Sources → New source**, choose **Jira · connapseos.atlassian.net**, tick ENG, keep **Include attachments**, then **Create source**.
13. Users who already linked their Atlassian account for Confluence need nothing more, because the same link covers Jira.

## Findings by sub-question

### 1. App roles and global permissions: two rows for Jira, and Administer Jira should be confirmed

**The two app-role rows.** In 2026, Jira access and Jira admin rights are separate app roles in Atlassian Administration.
- Atlassian documents that to make someone a Jira app admin, you "update the role for Jira Administration", and that App admin is the only role offered there [primary: support.atlassian.com, give-users-admin-permissions].
- Jira app admins "don't get app access automatically", so Jira must be set to User as well [primary: what-are-the-different-types-of-admin-roles].
- User access admin only manages app access [primary].

**Administer Jira is inferred, not stated.** App roles are granted through default groups, and `jira-admins-{site}` holds the "Jira Administrators" global permission by default [primary: default-groups-and-permissions]. No page states that App admin on Jira Administration equals Administer Jira, so verify it at **Settings → System → Global permissions** [primary: view-grant-revoke-global-permissions-in-jira].

**Granting global permissions.** The pages disagree on what you can grant to:
- one says global permissions can be granted to a group or a user;
- another says groups created by the org admin.

Granting through a group is the safe instruction.

**Browse spaces.** Browse spaces (formerly Browse projects) is granted in the default scheme to anyone with Jira access [secondary, inferred for service accounts]. Private team-managed spaces show "only Jira admins and people you add" [primary].

**Plan limits.**
- Five service accounts are free; Guard Standard allows up to 250 and Enterprise up to 1,000.
- They don't count toward user limits [primary: understand-service-accounts].

### 2. Credentials: OAuth for Connapse, a plain API token for the seed script

**The OAuth path.** It is **Directory → Service accounts → account → Create credentials → OAuth 2.0 → Next → select scopes → Next → Create**. The client ID and secret are shown once [primary: create-oauth-2-0-credential-for-service-accounts]. There is no expiry setting; access tokens last 60 minutes.

**Scopes.**
- Only Jira, Confluence, Goals and Projects scopes can be chosen [primary].
- Both classic scopes (for example `read:jira-work`) and granular scopes appear in the picker [secondary: JSDCLOUD-17697, fixed 2025-11]. A 50-scope limit is reported [secondary]; Connapse's 26 Jira scopes fit.
- No Beta labels were found in the scope reference [primary].
- Editing scopes after creation: API-token credentials explicitly can't [primary]; for OAuth credentials no statement exists, and a community report says their scopes can't even be viewed [tertiary]. Treat a credential as fixed.
- One credential carrying both Jira and Confluence scopes is unverified.

**The admin's own token.** For the seed script, **Create API token** (classic) works with Basic auth on the site URL. **Create API token with scopes** works only through `api.atlassian.com/ex/...`; against the site URL it gives misleading 401, 404 or empty 200 answers (JRACLOUD-94545, open) [primary + secondary]. Tokens expire in 1–365 days. No shutdown date for classic tokens has been published [tertiary].

**Rate limits.** The points quota (65,000 per hour, enforced since 2026-03-02) covers Forge, Connect and 3LO apps; API-token traffic is exempt. Client-credentials traffic isn't mentioned [primary: rate-limiting]. That stays unknown and is one of the live-check items in #708.

### 3. Groups, space roles, Service Management and comment restrictions

**The rename.** Jira's UI renamed projects to spaces on Premium sites from 2025-10-20 [secondary: Atlassian community announcement]. The REST API still says `project`.

**Groups.** A service account is added to a group from **Directory → Service accounts → ••• → Add to group** [primary]. One community report says add-on group checks didn't see a service account's group [secondary], so confirm on the test site that the service account can read the group-restricted comment. The REST calls `POST /rest/api/3/group` and `/group/user?groupId=` are current; whether they accept a service account's id is unverified.

**Custom roles.**
- Path: **Settings → System → Space roles → Add Space roles** [primary: create-a-new-space-role].
- A new role has no permissions until it is added to the permission scheme [primary].
- Roles can't be edited on Free plans [secondary].
- Older docs list Administrators, Developers and Users as defaults, but newer sites may have only Administrators [secondary + tertiary]. Check, and create Developers if it's missing.
- People are added at **Space settings → Access → Add people → role** [primary]. Whether the picker lists service accounts is unverified.

**Service Management.**
- Agents are added with **Invite team**, into the **Service Space Team** role (called "Service Desk Team" in the API) [primary].
- Service accounts don't count toward Jira or Confluence user limits; Service Management agent seats aren't mentioned [primary]. Whether a service account can be an agent is unverified.
- Internal notes: the documented REST call is `POST /rest/servicedeskapi/request/{key}/comment` with `public: false` [primary spec]. The `sd.public.comment` property the seed script uses has no official source.

**Comment restrictions.**
- **In the UI:** the comment editor's padlock lists only the groups and roles you belong to. Groups appear only if comment visibility allows "Groups & Project Roles" [secondary: DC KB].
- **Team-managed spaces:** no UI; REST only [primary].
- **REST:** `visibility` is `{type, value, identifier}`, with `identifier` a group id, or the role name for a role [primary spec]. This matches what Connapse reads.

**Attachments.** A file uploaded with a restricted comment is an issue-level attachment. Embedding it in a v3 comment needs a Media Services UUID that the API doesn't expose (JRACLOUD-96384) [secondary]. This is why the seed script links to the content URL rather than embedding. It also confirms that Connapse's fail-closed handling of untieable media references is needed.

### 4. Connapse's docs: accurate facts, stale role wording, and no Jira path

**Accurate.** The facts match the code [code reading]:
- the 25 Jira scopes;
- the seven test step labels;
- all 15 troubleshooting anchors;
- the "(Jira)" connection name.

**Wrong or unclear: role wording.** These five places still say "set Jira to its admin role" (Jira = User plus Jira Administration = App admin is correct):
- the Jira form's step 2 (`AtlassianConnectionForm.razor:237-240`);
- the "not Jira administrator" test message (`JiraSiteProbe.cs:29-32`);
- the guide's troubleshooting entries **Can't read Jira** (:374), **Not Jira administrator** (:383-384) and **Can't read Jira membership** (:394).

**Wrong or unclear: the test-site doc.**
- `docs/confluence-test-site.md` still says **Add connection → Atlassian site** (:19, :38).
- Its Jira section gives scopes to the account instead of a credential.
- It never says to add a **Jira site** connection, or to test it after seeding.
- It reuses one placeholder for both account IDs.

**Wrong or unclear: the provider card.** Its **Add an Atlassian site** button always opens the Confluence form.

**Wrong or unclear: the guide's order and coverage.**
- The intro, **Before you start** and the plan row mention only Confluence.
- "Do the steps in order" runs a Jira admin through Confluence sources.
- Step 2.4 repeats 2.2 and buries the Global permissions fallback.
- The "existing credential" decision comes last.
- Several troubleshooting entries are Confluence-only: site not found, client secret rejected, couldn't reach Atlassian, and missing scope.
- **Site has no pages or no other users yet** has no Jira fixes.

**Wrong or unclear: product labels and the seed script.**
- All Jira wording says "project". Premium sites show "space" in the UI, so the guide should name both.
- The seed script labels the group step as "in Jira" (it happens in Atlassian Administration).
- It always prints the Service Management agent step.
- It uses the undocumented `sd.public.comment` property for internal notes.

## Conflicts and uncertainties

- **Administer Jira from App admin:** inferred through default groups; no explicit Atlassian statement. Verify on Global permissions.
- **Global permissions grantee:** one page says "group or user", another "groups". Use a group.
- **One credential, both products:** official wording suggests yes; a 2026 community report on scoped tokens says one app per token. Unverified for OAuth, so use one credential per product.
- **Service accounts in pickers:** unverified for role and people pickers and for Service Management agents.
- **Attachment visibility:** the new issue view hides attachments from restricted comments; older reports say anyone who can view the issue sees them. This is checklist item 5 in #708.

## Gaps — what we did not find

- An explicit Atlassian statement mapping App admin on Jira Administration to Administer Jira.
- Whether OAuth credential scopes can be edited, and a maximum number of credentials per service account.
- Whether service-account OAuth traffic is on the points quota.
- Exact Cloud labels for the comment padlock menu.
- Whether Developers exists by default on a 2026 site.
- Whether role changes take time to apply.

## Source quality assessment

Most click paths come from support.atlassian.com pages and the Jira v3 OpenAPI spec (primary). The rest rests on weaker sources:
- **Secondary:** the projects-to-spaces rename (Atlassian community announcement), Free-plan role limits, and default-role lists.
- **Single-thread or tertiary:** the service-account group-membership caveat, the OAuth scopes-not-viewable claim, and the token-deprecation history.
- **Inference:** the App admin to Administer Jira link.

## Sources

**Primary**
- Understand service accounts: https://support.atlassian.com/user-management/docs/understand-service-accounts/
- Create OAuth 2.0 credential for service accounts: https://support.atlassian.com/user-management/docs/create-oauth-2-0-credential-for-service-accounts/
- Manage API tokens for service accounts: https://support.atlassian.com/user-management/docs/manage-api-tokens-for-service-accounts/
- Give users admin permissions: https://support.atlassian.com/user-management/docs/give-users-admin-permissions/
- Types of admin roles: https://support.atlassian.com/user-management/docs/what-are-the-different-types-of-admin-roles/
- Default groups and permissions: https://support.atlassian.com/user-management/docs/default-groups-and-permissions/
- Global permissions (view/grant): https://support.atlassian.com/jira-cloud-administration/docs/view-grant-revoke-global-permissions-in-jira/
- Manage global permissions: https://support.atlassian.com/jira-cloud-administration/docs/manage-global-permissions/
- Permission schemes: https://support.atlassian.com/jira-cloud-administration/docs/grant-or-revoke-permissions-in-a-scheme/
- Space permissions: https://support.atlassian.com/jira-cloud-administration/docs/space-access-and-configuration-permissions/
- Team-managed access: https://support.atlassian.com/jira-software-cloud/docs/manage-how-people-access-your-team-managed-project/
- Create a space role: https://support.atlassian.com/jira-cloud-administration/docs/create-a-new-space-role/
- JSM agents: https://support.atlassian.com/jira-service-management-cloud/docs/add-your-service-project-agents-to-work-on-requests/
- Team-managed comment visibility: https://support.atlassian.com/jira/kb/restrict-the-comment-visibility-in-a-team-managed-project/
- Personal API tokens: https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account/
- Scopes: https://developer.atlassian.com/cloud/jira/platform/scopes-for-oauth-2-3LO-and-forge-apps/
- Rate limiting: https://developer.atlassian.com/cloud/jira/platform/rate-limiting/
- Jira v3 OpenAPI: https://developer.atlassian.com/cloud/jira/platform/swagger-v3.v3.json

**Secondary**
- Jira Spaces have landed: https://community.atlassian.com/forums/Jira-articles/Jira-Spaces-have-landed/ba-p/3117620
- JSDCLOUD-17697 (scope picker), AX-1688 (edit scopes), JRACLOUD-94545 (scoped tokens on site URL), JRACLOUD-93726 (audit log), JRACLOUD-67381 (default roles), JRACLOUD-96384 / JRACLOUD-80445 / CLOUD-12458 (attachments): jira.atlassian.com
- Service account group caveat: https://community.atlassian.com/forums/Jira-questions/Using-new-Atlassian-Service-Accounts-with-add-on-Jira-Misc/qaq-p/3252792

**Tertiary**
- Scopes not viewable after creation: https://community.developer.atlassian.com/t/looking-for-a-way-to-troubleshoot-scopes-for-service-account-token/97204
- Token deprecation banner: https://community.developer.atlassian.com/t/api-tokens-without-scopes-are-being-deprecated/92510
- Default software roles: https://community.atlassian.com/learning/lesson/explore-roles-for-a-software-project

**Code reading:** Connapse `docs/atlassian-setup.md`, `docs/confluence-test-site.md`, `eval/tools/seed_jira.py`, `AtlassianConnectionTester.cs`, `JiraSiteProbe.cs`, `AtlassianConnectionForm.razor`, `Connections.razor`, `AtlassianProviderCard.razor`.
