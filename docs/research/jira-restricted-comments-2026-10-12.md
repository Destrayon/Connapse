# Can Connapse tell whether another user can see a restricted Jira Cloud comment?
**Date:** 2026-10-12
**Status:** Reviewed (inline adversarial pass)
**Built on:** atlassian-provider-lay-of-land-2026-09-30.md

## Executive summary

No Jira Cloud REST endpoint answers "can user U see comment C" for anyone but the caller. The rule can be rebuilt locally: browse access to the issue, plus membership in the comment's group or project role, with no admin override. For group restrictions that rebuild is probably at parity. For role restrictions it depends on three things no source confirms: that a service-account token can read role actors, that a role name resolves to one role per project, and that the comment's author has no exception. The only exact route, asking Jira for the comment as the user, needs either a Forge app with offline impersonation installed on every site, or stored per-user refresh tokens. Both reverse earlier decisions. Most products copy the issue's permission onto every comment or don't check visibility at all. Onyx and Elastic leak restricted comments; BA Insight documents skipping them. **Recommendation: skip restricted comments and restricted worklogs for the first Jira release, with a membership-based follow-up only after a live site settles the role questions.** Two related leaks need their own handling: attachments added inside restricted comments, and JSM internal notes.

## Research brief

**Question:** For Jira Cloud, is there any supported way for a server-side integration holding an admin service-account credential (OAuth client credentials) to determine whether a specific other user can see a comment whose `visibility` is restricted to a group or project role?

**Sub-questions:**
1. Does any Jira endpoint check comment or worklog visibility for another user?
2. Can a local membership evaluation match Jira's own rule, and at what cost?
3. Can Connapse fetch content as the user instead (Forge, Connect, 3LO tokens)?
4. Do attachments in restricted comments inherit the restriction, and can they be identified?
5. How do other enterprise search products handle restricted comments?

**Out of scope:** Jira Data Center; writing to Jira; anything outside comments, worklogs and their attachments.

**Success criteria:** a recommendation among (1) skip restricted comments, (2) index them per comment with live membership checks, (3) admin-only, with evidence quality stated.

## Findings by sub-question

### 1. No Jira Cloud endpoint checks comment visibility for another user

Every permission-check surface in the Jira Cloud v3 API stops at the issue.
- **Bulk check.** `POST /rest/api/3/permissions/check` (`BulkPermissionsRequestBean`) has exactly `accountId`, `globalPermissions` and `projectPermissions`. `BulkProjectPermissions` has `permissions`, `issues` and `projects`. There is no comment or worklog field [primary: v3 OpenAPI, downloaded 2026-10-12].
- **No permission to check.** No permission key grants viewing a comment. The comment keys are `ADD_COMMENTS`, `EDIT_*_COMMENTS` and `DELETE_*_COMMENTS`. JRACLOUD-3699, "View all comments" permission, was closed Won't Do [primary].
- **Caller-only check.** `GET /mypermissions?commentId=` has no `accountId`, accepts only `BROWSE_PROJECTS` with `commentId`, and answers for the caller only [primary].
- **User search.** `GET /user/permission/search` evaluates project and issue permissions only [primary].
- **No run-as.** The spec has no impersonation or run-as parameter outside Forge [primary]. The v2 and v3 schemas are identical here [primary].
- **No announcements.** No changelog or RFC announces a comment-level check for another user [not found after checking the developer community and changelogs].

### 2. Jira's rule for restricted comments, and whether Connapse can rebuild it

Jira shows a restricted comment to a user only if they can browse the issue (project permission plus issue security) **and** belong to the comment's group or project role.

**The rule itself:**
- The `getComments`, `/comment/list` and `/worklog/list` endpoints filter silently by exactly this rule [primary].
- **No admin override:** admins outside the group or role can't see the comment [primary: JRACLOUD-3699; tertiary: Appfire KB].
- **Author exception: conflicting sources.**
  - The REST rule lists none [primary].
  - A 2024 Atlassian-staff JSM announcement says "only the comment creator and users added to the selected group or project role" can see it [secondary].
  - If the author is an exception, a local check that ignores it is stricter than Jira, so it still fails closed.

**How a restriction identifies its group or role:**
- `visibility.identifier` is the group's id, which is stable across renames; `value` is a mutable name [primary spec; secondary Jira Cloud PM post, 2022].
- For a role, `identifier` is documented as the role's **name** [primary, single source].
- A deleted group or role makes the comment invisible to everyone: JRACLOUD-82409, closed "Not a bug" on 2025-10-08 [primary]. A local check against a missing group therefore denies, which is consistent.

**Membership data:**
- `GET /user/groups?accountId=` returns all of a user's groups with `groupId`. It needs Browse users and groups and `read:group:jira` (Beta) [primary].
- Atlassian Cloud has no native nested groups, so a flat check suffices [primary: support "What are nested groups"]. The spec's "nested groups" wording on `expand=groups` conflicts with that, but the effective-membership answer is the same either way.
- `GET /project/{key}/role/{id}` returns user and group actors. It needs Administer Projects or Administer Jira [primary].
- The spec itself describes checking role membership as "user plus their groups against the role's actors" [primary].
- Default actors apply only when a project is created, so they're irrelevant [primary].

**Parity gaps for role restrictions:**
1. No source confirms a client-credentials token can read role actors. One community thread reports admin-level group endpoints refusing client-credentials tokens, though only for writes [tertiary, single source].
2. Team-managed projects reuse role names across projects, so mapping a name to a role id per project is ambiguous [primary spec; needs a live test].
3. No source confirms that team-managed role actors are fully exposed through the API.

**Cost and freshness:**
- Membership reads cost 2 points each on the 65,000-points-per-hour quota, if service-account traffic is on it at all, which is unknown [primary rate-limiting page].
- With a 5-minute cache it costs a few calls per user per window. The same 5-minute staleness the Confluence design already accepts would apply.

**Index side:** the service account only receives restricted comments it is itself allowed to see [primary]. Option 2 therefore also needs the service account in every restricting group and role. Anything it misses is silently absent, which fails closed.

### 3. Fetching as the user: possible, but only with a Forge app or stored user tokens

The list endpoints filter silently, so fetching `/comment/list` *as user U* and checking which ids come back is an exact answer [primary]. Three ways exist to act as a user:
- **Forge offline impersonation:**
  - **How it works.** `asUser(accountId)` through a Forge Remote backend and the `offlineUserAuthToken` mutation [primary]. Each scope needs `allowImpersonation: true` and site-admin re-approval. Tokens last about 15 minutes [secondary: RFC-92].
  - **Who can be impersonated.** It works for users who never used the app, but not for deactivated users, service accounts (401, ID-9158 [primary]) or users without app access.
  - **Maturity.** One thread reports `MISSING_USER_CONSENT` [tertiary]. A 2026-09-30 changelog opens RFC-149 on Connect-parity gaps, so the design is still moving [primary].
  - **Deployment cost.** It needs a Forge app installed per site, which is workable for Connapse Cloud but means a separate app per self-hosted install.
- **Connect "act as user":** `ACT_AS_USER` JWT grant [primary]. This is a dead end: no new Connect apps since 2025-09-17, and end of support is 2027-01-31 [primary].
- **Stored 3LO refresh tokens:** calls are constrained to the user's permissions [primary]. The costs:
  - it reverses the owner's decision to discard user tokens;
  - tokens rotate on every use and expire after 90 days idle [primary];
  - every user must re-consent yearly, after the 365-day absolute expiry [secondary: staff post];
  - users who never return can't be checked.

### 4. Attachments in restricted comments: probably restricted, and not identifiable

An attachment added inside a restricted comment probably inherits the restriction, and nothing documented links it to its comment.
- **Inheritance: the sources conflict.**
  - **For it:** the current v3 spec says on `/attachment/{id}`, `/content`, `/thumbnail` and `/expand`: "If attachments are added in private comments, the comment-level restriction will be applied" [primary, single sentence repeated]. JRACLOUD-80445 (2023) says the new issue view hides them [primary].
  - **Against it:** JRACLOUD-10992 (closed for low engagement, 2024-01) and CLOUD-12458 (Gathering Interest) say restrictions don't apply to attachments. Google's connector docs say the same [primary tickets, but older].
  - **Reading:** inheritance arrived with the new issue view and the tickets are stale. This needs a live test.
- **No link from attachment to comment.**
  - The attachment metadata has no comment field [primary].
  - An ADF `media` node's `id` is a Media Services UUID with no documented mapping to an attachment id [primary].
  - Community workarounds parse attachment ids out of `renderedBody` HTML [tertiary]. A June 2026 thread on the mapping has no staff answer [tertiary].
- **Unknown:** whether `fields.attachment` hides such attachments from a caller who can't see the comment.

**Consequence:** indexing attachments under the issue's permission could leak a restricted comment's attachment to anyone who can browse the issue.

### 5. Other products: restricted comments are mostly inherited or ignored

| Product | Restricted-comment handling | Evidence |
|---|---|---|
| Onyx | None; all comments joined into the issue text; permissions from project BROWSE only | source code [primary] |
| Elastic connectors | None; `visibility` never read; issue-level ACL (security level, roles, BROWSE) | source code [primary] |
| Amazon Q Business | Comments are separate documents but "inherit the same access control as the associated issue" | AWS docs [primary] |
| BA Insight | "Restricted comments are not stored in the search index" | Upland docs [primary] |
| Google Gemini Enterprise | Probably a per-comment ACL ("attachments added to private comments don't inherit the access restrictions") | Google docs [primary, inference] |
| Microsoft Graph connector, Glean, Kore.ai, Rovo, Kendra, Coveo | Not documented | vendor docs [primary, silent] |
| LlamaIndex, LangChain | No comments or no permission model | source code [primary] |

No product documents checking group or role membership at query time. Skipping restricted comments has direct precedent in BA Insight. The two open-source connectors leak, which Connapse's fail-closed rule rules out.

### JSM internal notes (found in passing)

In Jira Service Management (JSM), an internal note (`jsdPublic: false`, property `sd.public.comment`) carries no `visibility`. It is hidden only from portal customers; any agent with Browse sees it [primary: spec; JSDCLOUD-3392]. Since 2024-05 a note can additionally be restricted to a group or role [primary: JSDCLOUD-829].

Jira's permission checks evaluate JSM permissions "similarly to a user in the customer portal" [primary]. So a customer the bulk check approves for an issue would see internal notes if Connapse indexed them under the issue. Internal notes therefore need their own gate. Skipping them is the fail-closed choice.

## Conflicts and uncertainties

- **Comment-author exception:** the REST rule lists none [primary]; a 2024 staff JSM announcement says the author sees it [secondary]. Ignoring the exception fails closed.
- **Attachment inheritance:** the current spec says yes; older tickets and Google's docs say no. Live test needed.
- **Role `identifier`:** documented as a role name, which is ambiguous in team-managed projects [single source].
- **Client-credentials access to admin-level membership reads:** one tertiary thread suggests it's blocked, and only for writes. Load-bearing for option 2.
- **Nested groups:** support docs say none exist in Cloud; spec wording mentions them. Effective membership makes this moot.
- **Forge consent:** docs list no per-user consent, but one thread hit `MISSING_USER_CONSENT`.

## Gaps — what we did not find

- Any changelog or roadmap item for a comment-level permission check for another user.
- Whether `fields.attachment` or the issue view filters attachments from restricted comments for the caller.
- Whether service-account (client-credentials) traffic counts against the points quota.
- Whether team-managed project role actors are fully listed by `/project/{key}/role/{id}`.
- Any documented way to map an ADF media node to an attachment id.
- How Glean, Microsoft and Rovo treat comment visibility.

## Source quality assessment

The API facts rest on the Jira v3 OpenAPI spec downloaded 2026-10-12 and on jira.atlassian.com tickets (primary). The behaviour of other products rests on their docs and, for Onyx and Elastic, on source code (primary). The load-bearing weak points are:
- the author exception (secondary);
- the client-credentials admin block (tertiary, single thread);
- the role-identifier-is-a-name sentence (primary but single);
- attachment inheritance (one spec sentence against older tickets);
- the Forge token limits (secondary).

## Recommendation

**Option 1: skip restricted comments**, specifically every comment or worklog with a `visibility`, and every JSM comment with `jsdPublic: false`.
- **Why:** it is the only option that is exact today without new credentials or new apps. It has precedent (BA Insight), and it costs nothing at search time.
- **What's lost:** restricted text is never searchable, even for people who can read it in Jira.

**Attachments:** because an attachment can't be tied to its comment, the fail-closed rule is to skip every attachment on an issue that has any restricted comment the service account can see. The live site must also confirm that the service account doesn't receive attachments from restricted comments it *can't* see. If it does, attachments need a stronger gate.

**Option 2 (per-comment documents with live membership checks) stays a follow-up.** Build it only after a live site confirms:
- client-credentials access to `/user/groups` and to role actors;
- unambiguous role resolution;
- the author-exception behaviour.

Group-only restrictions could ship first if the role questions fail.

**Option 3 (admin-only)** is coarser than Jira's rule, which breaks the per-object principle, and has no precedent. Rejected.

## Sources

**Primary**
- Jira Cloud v3 OpenAPI: https://developer.atlassian.com/cloud/jira/platform/swagger-v3.v3.json
- Rate limiting: https://developer.atlassian.com/cloud/jira/platform/rate-limiting
- Nested groups: https://support.atlassian.com/provisioning-users/docs/what-are-nested-groups
- Invisible comments (group level): https://support.atlassian.com/jira/kb/resolving-invisible-comments-issue-in-jira-due-to-incorrect-group-level-settings/
- Team-managed comment visibility: https://support.atlassian.com/jira/kb/restrict-the-comment-visibility-in-a-team-managed-project/
- JRACLOUD-3699, JRACLOUD-82409, JRACLOUD-80445, JRACLOUD-10992, CLOUD-12458, JSDCLOUD-3392, JSDCLOUD-829, ID-9158 (jira.atlassian.com)
- Forge security / offline impersonation: https://developer.atlassian.com/platform/forge/security/
- Forge Remote product API calls: https://developer.atlassian.com/platform/forge/remote/calling-product-apis/
- Forge changelog: https://developer.atlassian.com/platform/forge/changelog/
- Connect user impersonation: https://developer.atlassian.com/cloud/jira/platform/user-impersonation-for-connect-apps/
- 3LO apps: https://developer.atlassian.com/cloud/jira/platform/oauth-2-3lo-apps/
- ADF media node: https://developer.atlassian.com/cloud/jira/platform/apis/document/nodes/media/
- Onyx: https://github.com/onyx-dot-app/onyx (backend/onyx/connectors/jira, backend/ee/onyx/external_permissions/jira)
- Elastic: https://github.com/elastic/connectors (connectors/sources/atlassian/jira/datasource.py)
- Amazon Q Business: https://docs.aws.amazon.com/amazonq/latest/qbusiness-ug/jira-user-management.html
- Amazon Kendra: https://docs.aws.amazon.com/kendra/latest/dg/data-source-jira.html
- Google Gemini Enterprise: https://docs.cloud.google.com/gemini/enterprise/docs/jira-cloud
- Microsoft Graph connector: https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/jira-cloud-overview
- Glean: https://docs.glean.com/connectors/native/jira/
- BA Insight: https://help.uplandsoftware.com/bai/Connectors/AbouttheJiraConnector.htm
- Kore.ai: https://docs.kore.ai/agent-platform/connectors/jira
- Coveo: https://docs.coveo.com/en/1918

**Secondary**
- groupId in Jira REST APIs (Jira Cloud PM, 2022): https://community.developer.atlassian.com/t/adding-support-for-groupid-field-in-jira-rest-apis-expressions-types/61045
- JSM restricted internal notes (2024): https://community.atlassian.com/forums/Jira-Service-Management-articles/New-Restrict-your-internal-notes-to-project-roles-or-groups/ba-p/2704909
- RFC-92 Forge user impersonation: https://community.developer.atlassian.com/t/rfc-92-forge-user-impersonation/91156
- Refresh token deprecation (staff): https://community.developer.atlassian.com/t/updated-31-january-2022-action-required-deprecating-persistent-refresh-tokens/54172
- Connect end of support: https://community.developer.atlassian.com/t/connect-end-of-support-extended-to-january-31-2027/102002

**Tertiary**
- Client-credentials group-management block: https://community.developer.atlassian.com/t/no-path-for-a-forge-app-or-any-non-expiring-credential-to-manage-group-membership-confirmed-by-atlassian-feature-request-eco-1713-open-for-votes/102337
- Forge `MISSING_USER_CONSENT`: https://community.developer.atlassian.com/t/forge-remote-offline-user-impersonation-returns-no-atlassian-oauth-token-found-for-this-user-and-extension-even-after-user-interaction/100499
- ADF media to attachment id: https://community.developer.atlassian.com/t/how-to-map-adf-media-in-a-comment-to-a-specific-attachmentid-when-mediainline-has-no-filename-and-adf-id-attachment-id/94822
- Appfire KB, admin can't see restricted comment: https://appfire.atlassian.net/wiki/spaces/SUPPORT/pages/436305944/Issue+comment+is+not+visible+to+the+admin
