# erb-10k

A 10,000-document subset of [erb-50k](../erb-50k/card.md) for routine checks that a change does no harm on enterprise data (CON-151). It indexes in under 2 hours under `eval/tools/run_light.sh`'s limits with `EVAL_GPU=1`, where erb-50k takes about 15. Same 470 scored questions, same source revision, built by `eval/tools/build_enterprise_suite.py` alongside erb-50k.

- **Corpus**, chosen by a fixed rule with no random seed, so every build gives the same bytes (pinned in `eval/MANIFEST.json`):
  - all 722 gold documents;
  - 9,120 hard distractors: each scored question's top 22 by the same full-corpus BM25 erb-50k uses. 22 is the largest depth at which gold plus the pool stays at 10,000 or fewer;
  - 158 more: erb-50k's remaining documents with the lowest stable hash of the document ID (the same hash erb-50k fills with).

  Every document is also in erb-50k.
- **Sources:** Slack 2,267, Linear 1,611, Google Drive 1,550, Gmail 1,347, Confluence 933, Jira 844, HubSpot 674, GitHub 480, Fireflies 294.
- **Reference:** BM25 over the full corpus finds 71.3% of gold documents in its top 22.
- **Use:** fast no-harm checks, such as the link layer's identical-rankings bar. Scores run higher than on erb-50k because there are fewer distractors, so compare a change against a baseline on this same set, never with erb-50k numbers. A gain or a drop that matters should still be confirmed on erb-50k.
