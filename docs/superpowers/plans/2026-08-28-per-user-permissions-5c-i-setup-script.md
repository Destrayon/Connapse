# Per-user AWS search permissions — 5c-i, the setup script

**Issue:** #442. Part of #436.
**Goal:** An administrator provisions the whole AWS side of Cognito by pasting one script into AWS
CloudShell, then pastes one block back into Connapse.
**Research:** `docs/research/cognito-setup-automation-2026-08-28.md`.

**Precedent to follow exactly:** `AwsIamUserSetup` — script shown in full, run where credentials
already are, delimited block read back. Everything below is that pattern at larger scale.

## Boundaries

- The script **never authors a permission**. Access Grants instance, location and role: yes. Grants:
  never. That is the standing constraint of the epic.
- It runs on the **administrator's** credentials. Connapse issues text and parses a result.
- No Azure path is touched.
- All work targets `epic/aws-per-user-permissions`.

## Phases

### Phase 1 — The settings gap
**Goal:** `CognitoSettings` can hold the Identity Center application ARN that
`CreateTokenWithIAM` needs as its `clientId`.
**Done when:** the field exists, `IsConfigured` accounts for it, and the admin form shows it as
read-only output of setup rather than something to type.

### Phase 2 — The script
**Goal:** `CognitoSetup` in `Connapse.Core.Utilities` builds the CloudShell script and parses its
output block, with no I/O and no AWS SDK — a pure function of the inputs, like `AwsIamUserSetup`.
**Done when:** unit tests cover the generated script's shape, both federation modes, and round-trip
parsing including the malformed cases.

### Phase 3 — The page
**Goal:** The AWS provider page offers the script, takes the pasted block, and saves the settings.
**Done when:** the Per-user permissions section shows setup when unconfigured and the current pool
when configured, and the manual form remains for someone who provisioned by hand.

### Phase 4 — Verify
**Goal:** Build clean, whole unit suite green, container rebuilt, script read end to end for
correctness against the research's resource table.
**Done when:** all four pass and the actor-principal question is stated plainly as unverified.

## Progress

_(appended per phase)_

**Phase 1 — done.** `CognitoSettings.ApplicationArn` added, plus `CanResolvePermissions`. Kept out
of `IsConfigured` so adding it could not take the connect button away from a pool configured before
it existed. Files: `src/Connapse.Core/Models/CognitoSettings.cs`.

**Phase 2 — done.** `CognitoSetup` builds the script and parses its block; 27 unit tests cover both
federation modes, the ordering of the account check, the absence of any grant, and the malformed
paste cases. Files: `src/Connapse.Core/Utilities/CognitoSetup.cs`, tests alongside.

**Phase 3 — done.** The provider page offers the script, reads the block back, and keeps the manual
form for a pool built by hand. Gated on the Access requirement being satisfied, since the script has
to name an identity that works. Files: `src/Connapse.Web/Components/Pages/Providers.razor`.

**Phase 4 — done.** Build clean, 1405 unit tests green, container rebuilt. Not verified: whether an
IAM user is accepted as the actor principal — the AWS CLI session on this machine has expired, and
the spike only ever proved a role ARN. Next action: re-authenticate and run that one check.
