# Update Connapse's role permissions when they change

**Status:** Design (bounded) — approved in brainstorming
**Date:** 2026-09-03
**Builds on:** the grant create/cleanup work that widened `S3SetupPolicy.ForManagedIdentity()`.

## Problem

`S3SetupPolicy.ForManagedIdentity()` is applied to Connapse's role **once**, when the admin runs
the Roles Anywhere setup script. Widening it later (adding `s3:CreateAccessGrant`, then the cleanup
actions) does nothing for a role already created — the install keeps the old inline policy and every
new action returns `AccessDenied`. Observed live: a role set up before the grant-create feature
could not create grants; the connection page correctly fell back to the CloudShell script, but there
was no path to bring the role's **own** policy up to date.

The policy comment already predicted this: "widening it here does nothing for an identity that
already exists … keeps the narrower policy and fails the new service with AccessDenied."

## Constraint that shapes the solution

**Connapse never edits IAM at runtime.** A runtime identity that could rewrite its own policy could
grant itself anything — privilege escalation. So Connapse cannot update the role itself. As with
initial setup and the grant script, it generates a command the admin runs with their own
credentials. `iam:PutRolePolicy` replaces an inline policy of the same name in place, so re-applying
`ConnapseRead` updates it and touches nothing else (trust anchor, profile, access key all untouched).

## Decisions (from brainstorming)

1. **Always-available update command**, not drift auto-detection. An "Update role permissions"
   affordance on the AWS provider Access card that always shows the current `put-role-policy`
   command. Idempotent, so running it when nothing changed is harmless. (Drift detection can come
   later.)
2. **Covers Roles Anywhere and ambient/BYO.** Roles Anywhere: the exact command, role name and
   account taken from the stored `RoleArn`. Ambient: show the policy document to apply to whatever
   role Connapse runs as (Connapse did not create it and does not know its name).

## §1 Command generator (Core)

`AwsRolePolicyUpdate` in `Connapse.Core/Utilities`, pure, reusing `S3SetupPolicy.ForManagedIdentity()`
as the single source of the policy:

- `const string PolicyName = "ConnapseRead";` — matches what `AwsRolesAnywhereSetup` attaches.
- `string? GenerateCommand(string? roleArn)` — parses the account and role **name** (last segment
  after `role/`) out of the ARN, substitutes the account into the policy's `__CONNAPSE_ACCOUNT_ID__`
  placeholder, and returns:
  `aws iam put-role-policy --role-name <name> --policy-name ConnapseRead --policy-document '<policy>'`.
  Returns null when the ARN will not parse (so the UI shows the ambient path instead).
- `string PolicyDocument(string? account)` — the policy JSON with the account substituted (or the
  placeholder kept when the account is unknown), for the ambient/BYO display.

## §2 UI (AWS provider Access card, `Providers.razor`)

An "Update role permissions" block on the Access card, always shown when AWS is in use:

- Explains: run this if Connapse's permissions changed (e.g. after an update) and a feature reports
  `AccessDenied` — it brings the role's `ConnapseRead` policy up to the current set. Safe to re-run.
- **Roles Anywhere** (`GetRolesAnywhereAsync("aws")` returns a config with a `RoleArn`): show
  `GenerateCommand(config.RoleArn)` with a copy button + Open CloudShell link — the same affordance
  pattern the Roles Anywhere setup script and the grant script already use.
- **Ambient** (no stored Roles Anywhere config): show `PolicyDocument(account)` and a note to apply
  it to the role Connapse runs as. The account is resolved from a `WhoAmI` probe when available,
  else the placeholder is shown with an instruction to substitute it.

## §3 Pointer from the AccessDenied fallback

The grant button's fallback message in `Connections.razor` ("…not allowed to create access grants…
add s3:CreateAccessGrant to its policy") gains a pointer to the AWS provider page's Access card,
where the update command now lives — so the admin who hits the denial is sent to the fix.

## §4 Testing

- **Unit — `AwsRolePolicyUpdate`:** a Roles Anywhere ARN yields a command naming the right role,
  `--policy-name ConnapseRead`, the real account (not the placeholder), and the current actions
  (`s3:CreateAccessGrant`, `s3:DeleteAccessGrant`); a role ARN with a path takes the last segment; a
  malformed ARN returns null; `PolicyDocument` substitutes the account and, with none, keeps the
  placeholder.
- UI verified manually (no bUnit harness).

## §5 Delivery

Its own issue/PR. One Core utility + a Providers.razor affordance + a one-line copy pointer on
Connections.razor.
