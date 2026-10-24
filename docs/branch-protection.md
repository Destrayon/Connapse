# #787 branch protection: drafted, not applied

A repository ruleset for `main` and `epic/**`, because classic branch protection takes one branch name and can't match `epic/**`. It requires status checks only: Connapse has a single maintainer, so required reviews or code-owner approval could never be satisfied (GitHub doesn't let a PR's author approve it).

```bash
gh api -X POST repos/Destrayon/Connapse/rulesets --input - <<'JSON'
{
  "name": "Proof tests gate (#787)",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["refs/heads/main", "refs/heads/epic/**"], "exclude": [] } },
  "rules": [
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": false,
        "required_status_checks": [
          { "context": "build-and-test", "integration_id": 15368 },
          { "context": "code-quality", "integration_id": 15368 }
        ]
      }
    }
  ]
}
JSON
```

- `build-and-test` is the final CI job: it fails unless every job it waits on (small, medium, integration 1–3, code-quality, mcp-conformance) succeeded, and it runs the speed budgets and `PrPipeline_WallTimeUnder6Min`. `code-quality` is listed too so it shows as required on its own.
- `integration_id` 15368 is the GitHub Actions app, so only Actions can report those checks.
- `.github/CODEOWNERS` gates nothing under this ruleset. For a solo maintainer it's informational: it marks which files are proof tests, their lists and the build-check settings, and starts requesting reviews if the ruleset later adds code-owner review with a second reviewer.
