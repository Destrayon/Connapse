# Restore the Identity Center discovery step on the admin page

## Why

`AwsSsoSetup` and `AwsSsoSettingsTab` were deleted in `f2d1e1d` along with the AWS device flow. The
device flow was correctly removed — Identity Center cannot act as an OIDC provider for a third-party
application — but two things went with it that the current design still needs:

- **Region discovery.** Identity Center lives in exactly one region per organisation, nothing in the
  portal URL encodes it, and the Cognito setup script only looks in CloudShell's own region. An
  administrator whose instance is one region over gets "No IAM Identity Center instance is visible",
  which reads like an absence rather than a wrong-region lookup.
- **Guidance when there is none.** The old script probed AWS Organizations so it could tell
  "you can enable an instance here" apart from "you are in a member account and must do this from the
  management account", and could report a `sso:ListInstances` denial as a denial rather than a miss.

What is *not* restored: `portalUrl`, which existed only to register the device flow's OIDC client, and
the organisation-versus-account-instance rule, which was a permission-sets constraint the device flow
had and trusted identity propagation does not.

## Phases

### Phase 1 — The discovery script and its parser
Goal: `IdentityCenterSetup` generates a read-only CloudShell script and reads back what it printed.
Done when: the script probes the session region then the candidate list, stops at the first hit,
reports an `sso:ListInstances` denial distinctly from a miss, carries the Organizations posture, and
`ParseResult` tolerates a pasted terminal buffer; unit tests cover each outcome.

### Phase 2 — Settings hold what was found
Goal: the discovered region, instance ARN and identity store id survive a page reload.
Done when: `IdentityCenterSettings` exists with a `SectionName`, is registered in the options system
and the database settings map, and round-trips through the settings store.

### Phase 3 — The step on the AWS provider page
Goal: an administrator can run the scan, paste the block, and see the answer or why there isn't one.
Done when: the AWS provider page shows the script with copy and CloudShell buttons, a paste box, the
found instance, and posture-specific guidance when nothing was found; the Cognito setup script is
generated against the discovered region rather than CloudShell's.

### Phase 4 — Verify
Goal: nothing regressed.
Done when: the solution builds, Core, Identity and Integration suites pass, and the container is
rebuilt and reachable.

## Log

- Phase 1 not started.
- Phase 1 done. `IdentityCenterSetup` (script + parser) and `IdentityCenterSetupTests`.
  Adapted from the deleted `AwsSsoSetup`: dropped `portalUrl` and the organisation-instance rule,
  replaced the heredoc and the multi-line string assignment with a herestring and $'\n' so the
  script cannot enter continuation mode. Verified: 17 tests pass.
  Next: Phase 2, IdentityCenterSettings and its registration.
- Phase 2 done. `IdentityCenterSettings` (Region, InstanceArn, IdentityStoreId, IsConfigured),
  registered in `IdentityServiceExtensions` and mapped as "identitycenter" in
  `DatabaseSettingsProvider`. Verified: solution builds clean.
  Next: Phase 3, the discovery step on the AWS provider page.
- Phase 3 done. `Providers.razor` gained an `id="identity-center"` step above per-user permissions:
  script, copy/CloudShell buttons, paste box, and four outcomes (found / refused / none-can-enable /
  none-member-account), plus a summary card with "Scan again" once saved. `CognitoSetupRequest`
  gained `Region`, and the generated Cognito script now pins to the discovered region instead of
  CloudShell's. Verified: Web builds clean.
  Next: Phase 4, full suites and container.
- Phase 4 done. Verified: 997 Core + 108 Identity + 410 Integration tests pass; container rebuilt,
  starts clean and answers. All four phases complete.
