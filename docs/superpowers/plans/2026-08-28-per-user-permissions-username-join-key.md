# Per-user permissions: join Identity Center on user name, not email

## Why

The trusted token issuer resolves a Cognito token to one Identity Center user by matching a claim
against **one of three** identity-store attributes: user name, email, or external ID. We chose email,
and that choice is what made `email_verified` load-bearing — `CognitoIdTokenValidator` refuses a token
whose email the pool has not proven, because the address is the join key.

Cognito marks a SAML-federated user's mapped email **unverified by default** and cannot verify it with
a one-time code. So the moment the pool federates to Identity Center — the configuration we have
settled on — every sign-in is rejected by our own validator.

Relaxing the check would weaken a control. Changing the join key removes the need for it: with the
Identity Center **user name** as the key, the email stops being load-bearing and becomes display data.
External ID is the third option but is only populated by SCIM sync, so it is empty for manually
created users and cannot be the default.

Federated users only. A pool-local Cognito user has no Identity Center user name to carry, which is a
further reason that path is not the one to build on.

## Phases

### Phase 1 — Validator stops requiring a verified email
Goal: `CognitoIdTokenValidator` returns the Identity Center user name from the token and no longer
rejects on `email_verified`.
Done when: the validator returns both the user name and the (unverified) email, rejects a token with
no user name claim, and its tests cover both; no caller reads a verified-email guarantee any more.

### Phase 2 — Storage keeps the join key
Goal: the identity link records the user name that a later exchange is matched on.
Done when: `UserAwsIdentityLinkEntity` has a `DirectoryUserName` column, the DTO and service carry it,
an EF migration exists against `ConnapseIdentityDbContext`, and existing rows survive the migration
with an empty user name rather than a wrong one.

### Phase 3 — AWS setup maps and matches the user name
Goal: the generated script and template wire the claim end to end.
Done when: the trusted token issuer is created with `ClaimAttributePath=preferred_username` and
`IdentityStoreAttributePath=userName`, the template's SAML provider maps `preferred_username`, and the
setup page tells the administrator which attribute the Identity Center application must emit.

### Phase 4 — Wire-up, display and green suite
Goal: the callback stores the new key and the integrations page shows something a person recognises.
Done when: the callback persists the user name, the card renders it, `dotnet build` is clean and the
Core, Identity and Integration suites pass.

## Log

- Phase 1 not started.
- Phase 1 done. `CognitoIdTokenValidator` reads `preferred_username`, rejects `no_directory_user`,
  and no longer inspects `email_verified`; `CognitoIdTokenResult` carries both identifiers.
  Files: `src/Connapse.Identity/Services/CognitoIdTokenValidator.cs`,
  `tests/Connapse.Identity.Tests/CognitoIdTokenValidatorTests.cs`. Verified: 20 tests pass.
  Next: Phase 2, add `DirectoryUserName` to the link entity plus an EF migration.
- Phase 2 done. `DirectoryUserName` added to `UserAwsIdentityLinkEntity`, `AwsIdentityLinkDto`,
  `AwsIdentityLinkStore.SaveAsync` and the service; migration
  `20260829032626_AddDirectoryUserNameToAwsIdentityLink` adds the column non-null defaulting to ''.
  The callback call site was updated in this phase too, to keep the solution building.
  Files: entity, `ConnapseIdentityDbContext`, `AwsIdentityLinkStore`, `AuthModels`,
  `AwsIdentityLinkService`, `CloudIdentityEndpoints`, 12 test call sites.
  Verified: solution builds clean; 108 Identity + 16 Integration tests pass.
  Next: Phase 3, TTI mapping strings and the template's SAML attribute mapping.
- Phase 3 done. Script creates the TTI with ClaimAttributePath=preferred_username and
  IdentityStoreAttributePath=userName; template maps `preferred_username: userName`; the setup page
  tells the administrator to add a `userName` application attribute mapped to
  ${user:preferredUsername}. Files: `CognitoSetup.cs`, `Providers.razor`, `CognitoSetupTests.cs`.
  Verified: template parses as CloudFormation YAML; 48 Core tests pass.
  Next: Phase 4, the integrations card display and the stale email_not_verified message.
- Phase 4 done. The integrations card shows the directory user name with the email beside it, and
  warns on a pre-migration row that it must be reconnected; `email_not_verified` replaced by
  `no_directory_user` with a message naming the fix. Files: `ProfileIntegrations.razor`.
  Verified: solution builds clean; 980 Core + 108 Identity + 410 Integration tests pass.
  All four phases complete.
