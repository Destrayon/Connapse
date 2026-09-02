# PR 3a — Adaptive AWS Access card (Roles Anywhere UI + detection)

**Epic:** AWS role-based credentials (`epic/aws-per-user-permissions`)
**Spec:** [design](../specs/2026-09-01-aws-role-based-credentials-design.md) §1, §3, §4, §6, §7, §8
**Branch:** `feature/aws-roles-anywhere-ui` (base `epic/aws-per-user-permissions`)
**Predecessors merged:** PR 1 (#454 signer), PR 2a (#455 storage+wiring), PR 2b (#456 setup utility + CA/leaf keygen)

## Scope boundary (why this is 3a, not 3b)

3a is **additive to the UI/reader only**. It rewires the Access card and
`ProviderSetupReader` to the Roles Anywhere path and removes the *IAM-user setup
UI*, but it does **not** delete `AwsIamUserSetup.cs`, the legacy store methods
(`GetAsync`/`GetSecretAsync`/`SaveAsync`), the entity's access-key columns, the
`ck_provider_credentials_single_shape` constraint, or the `ConnapseAwsCredentials`
access-key branch. Those stay compiling (referenced by the runtime + tests) and are
deleted in **PR 3b** together with the live-AWS smoke test. After 3a the new UI is
RA-only; the legacy code is dead-but-present.

## Phases

### Phase 1/5 — Reader detection & traffic-light status
**Goal:** `ProviderSetupReader.Access()` becomes mode-aware (Ambient / RolesAnywhere / None) and returns green/yellow/red per spec §1.
- Check `GetRolesAnywhereAsync("aws")` **before** any legacy `GetAsync` (spec §1 precondition — a blank-`PublicId` RA row must not render as an access key).
- Mode: RA row present → `RolesAnywhere`; else if ambient identity resolves (WhoAmI succeeds) → `Ambient`; else → `None`.
- Status: identity resolves **and** preflight reads pass → `Satisfied` (green); identity resolves but a required read is denied → `Warning` (yellow) naming the denied action(s); nothing resolves → `NotConfigured` (red).
- Preflight = a small **declared required-actions list** (data, not hardcoded read-only): `sts:GetCallerIdentity` (WhoAmI) + `s3:ListAllMyBuckets` (ListBuckets) initially, structured so actions can be appended later (spec decision 5). Map each `AccessDenied` → its action name for the yellow detail.
- Surface the detected `mode` on the requirement/reader model so the card can drive content + reset visibility.
**Done when:** unit tests cover RA-row→RolesAnywhere, ambient-resolves→Ambient, nothing→None, and denied-read→Warning-with-action-name; reader no longer renders an RA row as a blank access key.

### Phase 2/5 — Access card: mode-aware rendering
**Goal:** the `<ProviderStepCard Id="access">` in `Providers.razor` renders per mode (spec §7, §8).
- **Ambient:** status-only. No easy/manual setup content. Reset control **hidden** (§8). Yellow links to the fix.
- **RolesAnywhere / None:** show the RA handshake (Phase 3) as `EasyContent` and RA manual entry (Phase 4) as `ManualContent`.
- Replace the IAM-user easy block (script/paste-back for access keys) — stop referencing `AwsIamUserSetup` from the card. Mirror the Identity Center card's paste-back structure (`Providers.razor` ~353–492).
**Done when:** page compiles and renders the correct affordances for each of the three modes; no `AwsIamUserSetup` reference remains in `Providers.razor`.

### Phase 3/5 — Roles Anywhere handshake (generate → script → paste-back → save)
**Goal:** wire `RolesAnywhereKeyGenerator` + `AwsRolesAnywhereSetup.GenerateScript` + `ParseResult` + `SaveRolesAnywhereAsync`.
- On first expand, generate the CA+leaf keypair **once** and cache it in component state (`RolesAnywhereKeyGenerator.Generate()`); the CA cert PEM feeds `GenerateScript(caPem, region)`. Regeneration = new CA = admin must re-run the script; document that in-UI.
- **Require a valid region before generating the script** (spec §3 region requirement — an empty/invalid region sanitises to empty → unparseable ARN block). Region input gates the Copy/CloudShell affordances.
- Paste-back textarea → `AwsRolesAnywhereSetup.ParseResult` (already validates ARN partition/service/type + account/region consistency). On a valid parse, "Use this identity" → save (Phase 3b safety).
- Store the **leaf** cert PEM + leaf private key with the parsed ARNs via `SaveRolesAnywhereAsync`.
**Done when:** a happy-path parse produces a `RolesAnywhereConfig` carrying the leaf cert + the three ARNs + region, ready to persist.

### Phase 3b (within Phase 3) — Save-safety preflight
**Goal:** never destroy the last working credential on a bad cert/ARN/typo (spec §3 save-safety).
- Add a Storage-side validator (`IRolesAnywhereSetupValidator` in `Connapse.Storage.CloudScope.RolesAnywhere`) that builds the cert from PEM + key and performs a **CreateSession** via the existing `RolesAnywhereClient` **without persisting**, returning `(ok, error?)`.
- UI flow: validate → only on success call `SaveRolesAnywhereAsync`. On failure show the AWS reason and leave the stored credential untouched (old cred survives because we never wrote).
- Register the validator in Storage DI.
**Done when:** a validation failure shows the reason and does not call `SaveRolesAnywhereAsync`; success persists then re-reads status.

### Phase 4/5 — Manual values (BYO)
**Goal:** standard manual-entry affordance (spec §4) — admin types own trust-anchor/profile/role ARNs + region and supplies own cert + private key.
- `ManualContent` inputs: 4 ARNs + region + cert PEM + private key PEM → same validator → `SaveRolesAnywhereAsync`.
- BYO cert must chain directly to the registered trust anchor (self-signed-as-anchor or leaf-under-registered-CA). **Intermediate-chain (`X-Amz-X509-Chain`) support is deferred** — noted in the spec's Delivery-shape §2; out of 3a scope, tracked for a follow-up.
**Done when:** manual entry round-trips through the validator and saves an RA credential.

### Phase 5/5 — Reset behavior + tests
**Goal:** off-AWS reset per spec §8; ambient reset hidden (done in Phase 2).
- Off-AWS reset (`ResetAccessIdentity` / `ProviderResetAction OnReset`): **wipe local first** (`DeleteAsync("aws")` clears cert/key/ARNs so this instance stops authenticating immediately), then attempt a direct `rolesanywhere:DeleteTrustAnchor` on this instance's trust anchor; on `AccessDenied` degrade to displaying a CloudShell cleanup snippet that deletes just that trust anchor. **Never** touch role/profile/policy (guaranteed by per-instance design, not by permissions).
- Tests: reader mode-selection + status mapping (Phase 1); validator success/failure ordering (save-safety); parse→save happy path.
**Done when:** `dotnet build` clean, full unit suite green, reset wipes-then-revokes with graceful `AccessDenied` degradation.

## Out of scope (→ PR 3b)
Delete `AwsIamUserSetup.cs` + its tests; remove legacy store methods + entity access-key columns + the CHECK constraint (drop-columns migration); remove the `ConnapseAwsCredentials` access-key branch; the **live-AWS smoke test** (the CA→leaf acceptance gate).

## Verification
`dotnet build`; `dotnet test --filter "Category=Unit"`. Then Codex adversarial review → receiving-code-review → Push + PR against the epic branch.
