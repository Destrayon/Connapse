# Confluence test site

A small Confluence Cloud site for testing the Atlassian connector (#540) end to end. The content is about 200 pages from [EnterpriseRAG-Bench](https://github.com/onyx-dot-app/EnterpriseRAG-Bench) (MIT), one fictional company's internal wiki, so every test question has a known answer page. The layout covers each permission case Connapse has to get right:

| Where | Who can read it in Confluence | What it tests |
|---|---|---|
| **Engineering** space | everyone on the site | ordinary indexing and search |
| **Engineering → Incident reviews** page tree | you and the service account | page restrictions, inherited by child pages |
| **Office plant care and decor guidelines** (in Engineering) | only you | a page the service account can't read is never indexed |
| **Company Operations** space | you and the service account (private space) | a space a second person can't see |
| A comment on one runbook, a PDF on one guide | everyone | comments and attachments are indexed |

## 1. Create the site
1. Sign up for [Confluence Cloud Free](https://www.atlassian.com/software/confluence/free) with your own email and pick a site name, for example `connapse-test`. This creates the site (`connapse-test.atlassian.net`) and your Atlassian organization.
2. Start a **Standard trial** before running the seeding script: Free has no page restrictions or private spaces. In [Atlassian Administration](https://admin.atlassian.com), go to **Billing** → **Manage subscriptions** → Confluence → change plan → Standard trial. It lasts 14 days.
3. Invite a second person: another email address you own. In Atlassian Administration, go to **Directory** → **Users** → **Invite users**, give them Confluence access, and accept the invitation from that inbox. This person should see the Engineering space but not Incident reviews or Company Operations.

## 2. Create the service account
Follow [step 2 of the Atlassian setup guide](atlassian-setup.md#2-service-account) (the same steps are on Connapse's **Connections** page under **Add connection** → **Atlassian site**). Then copy the service account's **account ID**: in Atlassian Administration, go to **Directory** → **Service accounts** and open the account. The ID is the long value at the end of the page's address. The seeding script uses it to give the account access to both spaces and the Incident reviews tree.

## 3. Seed the content
1. Create an API token for **your own** account at [id.atlassian.com → Security → API tokens](https://id.atlassian.com/manage-profile/security/api-tokens) (**Create API token**, not the scoped kind). The script uses it to create pages as you.
2. In a terminal at the repository root (the first `build` downloads the benchmark, about 1.4 GB, into `eval/.cache`):

   ```powershell
   $env:CONFLUENCE_EMAIL = "you@example.com"
   $env:CONFLUENCE_API_TOKEN = "<your API token>"
   python eval/tools/seed_confluence.py build
   python eval/tools/seed_confluence.py push --site connapse-test.atlassian.net --service-account-id <account ID>
   ```

   It takes a few minutes. Re-running it is safe: it reuses anything already created. If a step can't be done through the API, it prints what to do by hand at the end.

## 4. Connect Connapse
The full field-by-field steps are in the [Atlassian setup guide](atlassian-setup.md).

1. **Admin** → **Providers** → **Atlassian**: create and save the linking app ([step 1 of the guide](atlassian-setup.md#1-linking-app)). Without it, nobody can link an account in step 4.4, so every search shows no Confluence results.
2. **Connections** → **Add connection** → **Atlassian site**: enter the site, the service account's client ID and secret, and **Check and save**. This site needs attachments (step 3), so create that credential with **Copy with the attachments permission**, which adds `read:attachment:confluence` to the required scopes.
3. **Sources** → **New source** → choose the site's connection → tick **Engineering** and **Company Operations**, tick **Include attachments** if it isn't already (it starts off when the site's last test didn't confirm the `read:attachment:confluence` scope, and the GPU budget question below is answered only by an attached PDF), and **Create source**.
4. Link accounts: as yourself, go to **Profile** → **Integrations** → Atlassian and sign in as your Atlassian account. Then, as a second Connapse user, link the second person's Atlassian account.

## 5. Check the results
Search in Connapse as each person once the sources finish syncing. These six cover every case; the full list of 64 questions, with the page that answers each one, is in `eval/.cache/confluence-seed/questions.md` after `build`.

| Question | Answer page | You | Second person | Unlinked user |
|---|---|---|---|---|
| How long is the validity period for the telemetry driven runbook author certification from the operator training bootcamp? | Runbook Writing and Telemetry Bootcamp Curriculum (Engineering / Runbooks) | found | found | not found |
| What is the expected update rhythm for the shared incident channel from the first message through containment? | Tribe Liaison & Status-Wire Protocol: Incident Playbook (Engineering / Incident reviews) | found | not found | not found |
| How long should contractor access last by default before it expires? | entitlements-and-purchase-pathways-playbook-2030 (Company Operations) | found | not found | not found |
| How often should facilities water common-area snake plants and ZZ plants? | Office plant care and decor guidelines | not found | not found | not found |
| Who owns the fallback pager rotation for perf-canary, and what is the escalation code? | a comment on one runbook (ORCHID-7) | found | found | not found |
| What is the 2026 GPU burst budget ceiling per quarter? | a PDF attached to one guide (412 thousand dollars) | found | found | not found |

Then, to check that permissions are live: in Confluence, restrict one Engineering page to yourself and the service account (lock icon → **Specific people**) and search for it again as them. It should disappear within five minutes.

## 6. Jira (optional)
The same site can also test the Jira connector (#708). Add Jira (the free plan is enough) from Atlassian Administration, and give the service account the Jira scopes and **Administer Jira**. The script needs the service account's account ID from step 2; the second person's ID is optional and only used to warn you if they would see a restricted comment.

1. Create an API token for your own account as in step 3. `JIRA_EMAIL` and `JIRA_API_TOKEN` are used first; the `CONFLUENCE_` pair works if those are unset.
2. Run the script. Add `--dry-run` first to see every request it would make:

   ```powershell
   $env:JIRA_EMAIL = "you@example.com"
   $env:JIRA_API_TOKEN = "<your API token>"
   python -I eval/tools/seed_jira.py push --site connapse-test.atlassian.net --service-account-id <account ID> --second-user-id <account ID>
   ```

   It needs only the `requests` package and is safe to re-run. It creates:

   | What | Who can read it |
   |---|---|
   | Project **ENG** (company-managed): 30 short issues, three under one epic, three pairs linked | everyone with access to the project |
   | A comment restricted to the group `connapse-restricted-readers` (you and the service account), with an attachment `burst-compute-budget-2026.pdf` | you and the service account |
   | A comment restricted to the project role **Developers** (you and the service account are added) | you and the service account |
   | A comment restricted to a project role named **QA/Ops Team** (you and the service account are added) | you and the service account |
   | Service project **SUP** with one internal note, if the site has Jira Service Management | agents only |

   The attachment is uploaded to an ENG issue, and the group-restricted comment links to its content URL (a link, not an embedded media node, because the REST API doesn't return the Media file ID an embedded node needs). Anything the API refuses is printed at the end as a manual step: creating the group or roles needs site-admin rights, and a service project needs a Service Management license; making the service account a Service Management agent is always by hand.
3. Connapse: add ENG (and SUP) as Jira sources on the site's connection, link accounts as in step 4, and search as each person. The script prints this table at the end:

   | Question | Answer | You | Second person | Unlinked user |
   |---|---|---|---|---|
   | How many retries per request does the ingest client allow? | an ENG issue (three) | found | found | not found |
   | Who owns the cobalt failover drill for the billing queue, and what is the runbook code? | the group-restricted comment (COBALT-41) | found | not found | not found |
   | When does the nightly index rebuild run after the Juniper migration? | the Developers-restricted comment (02:15 UTC) | found | not found | not found |
   | How many QA/Ops approvals does the mobile client release sign-off need? | the QA/Ops Team-restricted comment (two) | found | not found | not found |
   | What is the 2026 burst compute ceiling per quarter? | the attached PDF (733 thousand dollars) | found, if attachments are indexed | not found | not found |
   | Who is the vendor escalation contact for the print queue outage? | the SUP internal note (PRINT-ECHO-12) | found, if internal comments are indexed | not found | not found |
