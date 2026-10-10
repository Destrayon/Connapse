# What are the accurate admin setup steps for Connapse's Atlassian integration?
**Date:** 2026-10-10
**Status:** Reviewed (inline)
**Built on:** atlassian-provider-lay-of-land-2026-09-30.md

## Executive summary
The in-app setup text missed three requirements:
- The service account needs view access to every space it indexes. Admin roles don't grant that, and the space list only shows spaces it can view.
- Checking another person's access requires Confluence's own "Confluence Administrator" global permission. Atlassian doesn't document whether the service account's app-role dropdown grants it, so the admin should confirm it under Confluence → Settings → Global permissions.
- The linking app is private until Sharing is turned on, and that needs a vendor name, a privacy policy URL and a personal-data answer.

Separately, Atlassian's developer docs say apps that "instruct customers to create individual 3LO apps" don't comply with Atlassian's security requirements and Acceptable Use Policy. That conflicts with Connapse asking each self-hosted admin to create a linking app, and it needs a product decision.

## Research brief
**Question:** What are the exact current steps, as of October 2026, to set up the Atlassian linking app, the service-account site connection, and Confluence space sources?

**Sub-questions:**
1. The linking app's developer-console flow.
2. The service account, its role, and its OAuth 2.0 credential.
3. Confluence-side access needed for listing, indexing and per-user checks.

**Out of scope:** Jira; Data Center.

**Success criteria:** A corrected in-app guide.

## Findings: linking app (OAuth 2.0 3LO, read:me)
The developer-console flow, confirmed against Atlassian's documentation:
1. **Create:** developer console → **Create** → **OAuth 2.0 integration**. Enter a name, choose an **Access type**, and agree to the developer terms. [primary]
   - **Account-level:** consent covers the whole account.
   - **Resource-level:** the user picks a site when consenting.
   - The docs don't say which one `/me` needs. Account-level avoids a pointless site picker. [inference]
2. **Permissions:** next to **User identity API**, choose **Add**, then **Configure**, and add `read:me`. [primary]
3. **Authorization:** next to **OAuth 2.0 (3LO)**, choose **Configure**, enter the **Callback URL**, then **Save changes**. Multiple URLs and localhost rules are undocumented. [primary]
4. **Distribution:** an app is private by default, and "only you can install and use it". There is no same-organization exception, so **Sharing** is required for anyone else. Sharing needs a vendor name, a privacy policy URL, and the personal-data answer (**Yes**, because Connapse stores account IDs). Users then see an "unreviewed by Atlassian" warning. [primary; personal-data obligations are secondary]
5. **Settings:** this is where the client ID and secret are shown. Whether the secret can be viewed again or rotated is undocumented.
6. The **rotating refresh tokens** banner only matters with `offline_access`, which Connapse never requests. [primary]
7. **Risk:** an unresolved 2026 community thread reports "failed to retrieve client" errors for non-owners even with Sharing on. Test with a second Atlassian account. [secondary]

## Findings: service account and credential
1. **Prerequisites:** you must be an organization admin, and the organization must use centralized user management. Each organization gets **5 free service accounts**; more require Atlassian Guard. Service accounts with Confluence access don't count toward the user limit and add no charge. [primary]
2. **Create:** Atlassian Administration → **Directory** → **Service accounts** → **Create a service account**. The name is 6–30 letters or digits. The wizard's second step is **Select app roles**, with an optional **Groups** field. [primary]
3. **Admin permission:** the Confluence content permission check requires the **Confluence Administrator** global permission to check other users. Whether the app-role dropdown's admin option grants it is undocumented, so verify it in Confluence → **Settings** → **Global permissions**. [primary for the requirement; unresolved for the mapping]
4. **Credential:** select the account → **Create credentials** → **OAuth 2.0** → select scopes → **Create**.
   - Scopes can be chosen for Jira, Confluence, Goals and Projects, so one credential can carry both Jira and Confluence scopes.
   - The secret is shown once.
   - Tokens come from `auth.atlassian.com/oauth/token` with `client_credentials`, and API calls go through `api.atlassian.com/ex/confluence/{cloudId}`. [primary]

## Findings: Confluence-side prerequisites
1. **Spaces:** `GET /wiki/api/v2/spaces` returns only spaces the caller can view, and admin status doesn't grant space access. Grant the service account, or a group it belongs to, view access per space. On sites that use space roles, that's the **Viewer** role. [primary for the API; secondary for admin behaviour]
2. **Restricted pages:** view restrictions bind everyone, admins included. Pages that don't list the service account aren't indexed. Admin key is a Premium UI feature for a person and not an API bypass. [primary and secondary]
3. **Plans:** Free has no page restrictions. **Standard** is enough to test per-user filtering. Trials run 14 days for Standard and 30 days for Premium, and dropping to Free keeps existing restrictions but blocks new ones. [primary]
4. **Permission check behaviour:** "User is not allowed to use Confluence" means `hasPermission=false`. Deactivated users are undocumented. Connapse already treats anything other than `true` as a deny. [primary]
5. **Policies that may block access:** app access rules (data security policies) and IP allowlists (Premium+) may block reads, but whether they apply to service accounts or 3LO apps is undocumented. [primary and secondary]

## Conflicts and uncertainties
- What the Confluence Roles dropdown offers, and whether its admin role grants the Confluence Administrator global permission. Unresolved; the live test settles it.
- Whether service-account client-credentials traffic is on the 3LO points quota. Undocumented.
- Whether Resource-level access type affects `/me`. Undocumented.
- Whether the "individual 3LO apps" policy applies to self-hosted software where the customer is also the operator. It isn't addressed; the wording covers it literally.

## Gaps — what we did not find
- How the scope picker is laid out.
- Whether the linking app's secret can be viewed again or rotated.
- The obligations attached to the personal-data answer.
- Whether a service account can be assigned to a space role directly or only through a group.

## Source quality assessment
Most findings rest on primary Atlassian docs (developer.atlassian.com and support.atlassian.com). The role-to-permission mapping and the non-owner sign-in failures rest on community threads only.

## Sources
**Primary:**
- https://developer.atlassian.com/cloud/jira/platform/oauth-2-3lo-apps/
- https://developer.atlassian.com/cloud/jira/platform/oauth-2-authorization-code-grants-3lo-for-apps/
- https://developer.atlassian.com/cloud/oauth/getting-started/managing-oauth-apps/
- https://support.atlassian.com/user-management/docs/understand-service-accounts/
- https://support.atlassian.com/user-management/docs/create-oauth-2-0-credential-for-service-accounts/
- https://developer.atlassian.com/cloud/confluence/rest/v1/api-group-content-permissions/
- https://developer.atlassian.com/cloud/confluence/rest/v2/api-group-space/
- https://support.atlassian.com/confluence-cloud/docs/learn-about-confluence-cloud-plans/
- https://support.atlassian.com/confluence-cloud/docs/faq-role-based-access-in-confluence/
- https://developer.atlassian.com/cloud/confluence/rate-limiting/
- https://developer.atlassian.com/platform/marketplace/security-requirements/
- https://www.atlassian.com/blog/development/building-secure-and-scalable-integrations-our-guidance-for-third-party-apps

**Secondary:**
- https://community.developer.atlassian.com/t/oauth-3lo-failed-to-retrieve-client-for-non-contributors-despite-sharing-mode-enabled/100834
- https://community.developer.atlassian.com/t/how-to-access-restricted-pages-by-admin-in-confluence-cloud/69354
- https://jira.atlassian.com/browse/CONFCLOUD-40167
