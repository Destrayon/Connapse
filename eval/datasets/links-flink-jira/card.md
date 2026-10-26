# links-flink-jira

Link questions over Apache Flink's public Jira (CON-30), built the same way as `links-connapse-github`; see its
card for how questions were sampled, split, written and validated.

- **Corpus:** the 5,036 FLINK issues created in 2022, with their public comments, from the REST v2 search API at
  issues.apache.org. The harness renders each with `JiraRecordRenderer` and ingests it as `<issue id>.md` with
  the Record chunking strategy, as the Jira connector does. Two differences from a Cloud site: descriptions and
  comments are wiki markup rather than Atlassian Document Format, so they are indexed as written, and the
  project's display name is not in a search answer, so the breadcrumb says "Flink" from the key prefix.
- **Links (the answer keys):** from each issue's `issuelinks` and `parent` fields, kept when both ends are in the
  corpus: Blocker (blocks), Duplicate, Problem/Incident (causes) and sub-task of. 1,290 links.
- **Questions:** 170.

  | Kind | Hops | Asks for |
  |---|---|---|
  | jira-duplicate | 1 | the tickets that report the same problem |
  | jira-blocked-by | 1 | the tickets blocking this one |
  | jira-caused-by | 1 | the ticket that caused this problem |
  | jira-parent | 1 | the parent of a sub-task |
  | jira-subtasks | 1 | the sub-tasks of a ticket |
  | jira-siblings | 2 | the other sub-tasks of the same parent |

Jira content on issues.apache.org is public; the corpus is built locally by
`python eval/tools/build_link_suite.py links-flink-jira` and not redistributed.
