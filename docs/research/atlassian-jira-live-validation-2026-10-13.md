# Jira connector: live validation on connapseos.atlassian.net

**Date:** 2026-10-13 · **Issue:** #708 · **Site:** connapseos.atlassian.net (Jira Premium trial, alongside Confluence)

This file records the answers to the spec's live checklist (`docs/superpowers/specs/2026-10-12-atlassian-jira-provider-design.md`, section 5) as they're observed on the test site.

## Answered

| # | Question | Answer | How it was observed |
|---|---|---|---|
| 1 | Can a service account with client credentials read other users' groups and role actors? | **Yes** | The Jira site test passed its **Reading Jira groups and project roles** step, which calls `/user/groups` for another account and reads a space role's actors. |
| — | Does **Jira Administration → App admin** on a service account grant Administer Jira? | **Yes**, without adding the account to `jira-admins-<site>` | The **Checking Jira access permissions** step passed (`permissions/check` for another account). On Jira's **Global permissions** page, `jira-admins-connapseos` had no members, so that page can't show the grant. |
| — | Can a service account be added to a group and to space roles through the REST API? | **Yes** | The seed script's `POST /rest/api/3/group/user` and `POST /rest/api/3/project/ENG/role/{id}` calls for the service account succeeded with no manual step. |
| — | Do restricted comments reach a linked member end to end? | **Yes**, for the owner's account | Searching as the site owner found each answer as result #1: the group-restricted comment (COBALT-41), the Developers comment (JUNIPER-88), the QA/Ops Team comment (SIGNOFF-ZEBRA-3, after the bulkfetch fix below), and the PDF tied to the group comment. The QA/Ops Team result shows that a role name containing `/` round-trips through the URI and the live role check. |
| — | Are restricted comments hidden from a linked user outside the group and roles? | **Yes** | As a second linked Connapse user with access to ENG but in neither role nor the group, the four restricted answers (COBALT-41, JUNIPER-88, SIGNOFF-ZEBRA-3, the PDF) did not appear; ordinary ENG issues did. SIGNOFF-ZEBRA-3, result #1 for the owner, vanished for this user. |
| — | Does an unlinked user see nothing from Jira? | **Yes** | The owner unlinked the second user's Atlassian account and searched; nothing from ENG appeared. |
| — | Is an attachment tied to its restricted comment through the rendered body's link? | **Yes**, for a content-URL link | The group comment's `renderedBody` linked `/rest/api/3/attachment/content/10000`. The PDF took the group restriction: found by the owner, hidden from the second user. Embedded media nodes are still unverified. |

## Found and fixed while setting up

- **The site test's issue search used POST.** Atlassian requires `read:field.default-value:jira` and `read:field.option:jira` for the POST form of `/rest/api/3/search/jql`, which the scope list doesn't include. A credential with every listed scope therefore failed at **Reading issues and comments**. The test now uses GET, as the sync does.
- **The optional attachment probe used `/attachment/meta`,** which needs `read:instance-configuration:jira`. It now probes `/attachment/content/0`, which needs `read:attachment:jira` only.
- **A new site's Comment visibility allows roles only.** The group-restricted comment failed with `GROUP_VISIBILITY_SETTING_NOT_ENABLED` until **General configuration → Comment visibility** was set to **Groups & Project Roles**. This doesn't affect Connapse: a site set to roles only simply has no group-restricted comments. The seed script and the test-site guide now say how to enable it.
- **This site has no Jira Service Management,** so the internal-note checks (item 6) wait for a site that does.
- **Bulkfetch's inline comment total can't be trusted.** For ENG-10 it reported 0 comments to the service account, although `/issue/{id}/comment` returned the comment restricted to **QA/Ops Team** (the service account was in that role). The Developers-restricted comment on ENG-9 was counted. The sync used that total to decide whether to read an issue's comments, so the QA/Ops comment was never indexed, by any sync or sweep. The connector now reads every fetched issue's comments directly. Whether the `/` in the role name is the cause is unknown.
- Every other Jira call the code makes was checked against the v3 spec's granular scopes, and all are covered by the 25-scope list.
- **The setup steps didn't match the live UI.** The gear opens **Jira admin settings**, and a space's settings are under **•••** next to its name → **Space settings**. Team-managed spaces have no "Browse spaces" permission. The guide, the Jira form and the error messages were corrected.

## Still open

2. How team-managed space roles resolve. In company-managed ENG, a role restriction's identifier is the role name, and resolving it through the project's role list worked for both test roles.
3. Whether a comment's author sees their own restricted comment.
4. Whether changing a comment's visibility bumps the issue's `updated` time (this decides whether the daily comment sweep is needed).
5. Whether the service account receives attachments from restricted comments it can't see.
6. Whether `SERVICEDESK_AGENT` denies a portal customer.
7. The rate-limit headers on Jira client-credentials traffic.
- From the Phase 8 review: whether the 1,000-issue cap counts per request or per permission entry, and whether a site without Service Management rejects the `SERVICEDESK_AGENT` key.
- From the Phase 9 review: the `renderedBody` form of an attachment *embedded* in a restricted comment (a linked one is confirmed above).

Items 2–5 need the seed script's restricted comments in a company-managed space (ENG).
