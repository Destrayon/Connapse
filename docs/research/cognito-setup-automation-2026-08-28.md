# Automating the Cognito setup — research

**Date:** 2026-08-28
**Question:** How much of the AWS side of per-user permissions can Connapse set up for an
administrator, and what shape should that take?
**Relates to:** #436 (epic), component 4 of
`docs/superpowers/specs/2026-08-27-per-user-aws-search-permissions-design.md`.

Builds on `docs/research/per-user-search-permission-design-2026-08-27.md`, which settled *what* has
to exist and *how to detect it*. This asks only how to create it.

---

## Summary

**The setup can be one paste into AWS CloudShell, and it should be** — the same shape as
`AwsIamUserSetup`, which already gives Connapse its own AWS identity that way. A single script can
deploy a CloudFormation stack for everything CloudFormation covers and then make the four
`sso-admin` calls that have no CloudFormation resource type, ending by printing a delimited block
Connapse reads back. No hosted template, no Lambda, no public S3 bucket.

**The one thing that would have made this impossible turns out not to be required.** The existing
design has the Cognito pool federating back to IAM Identity Center over SAML so people keep their
existing credentials. That federation needs a customer-managed SAML 2.0 application, and AWS
documents that one as creatable **in the console only** — no API, no CLI, no CloudFormation. It
would have forced setup into script → console → script, with a metadata file carried by hand
between the halves.

It is not required. AWS states that trusted token issuers are "completely independent from the
authentication feature of IAM Identity Center and [do not] need to be the same identity provider as
is used for authenticating into IAM Identity Center"
([trusted token issuers](https://aws.amazon.com/blogs/security/simplify-workforce-identity-management-using-iam-identity-center-and-trusted-token-issuers/)).
The only hard requirement is that the Cognito token's `email` claim match exactly one Identity
Center user. **How that Cognito user came to exist is AWS's business, not Connapse's.**

That turns one setup into three, and only the third has a console step.

---

## What has to exist, and what can create it

Verified against the CloudFormation template reference on 2026-08-28.

| # | Resource | CloudFormation | Notes |
|---|---|---|---|
| 1 | Cognito user pool | `AWS::Cognito::UserPool` | |
| 2 | User pool domain | `AWS::Cognito::UserPoolDomain` | Needed for the browser sign-in, not for the token exchange |
| 3 | App client (with secret) | `AWS::Cognito::UserPoolClient` | Callback URL is a stack parameter |
| 4 | Cognito identity provider | `AWS::Cognito::UserPoolIdentityProvider` | SAML or OIDC, by `MetadataURL`. Only for tiers 2 and 3 |
| 5 | Identity Center OAuth application | `AWS::SSO::Application` | Customer-managed, `applicationProvider/custom` |
| 6 | Application assignment | `AWS::SSO::ApplicationAssignment` | Per user or group |
| 7 | S3 Access Grants instance | `AWS::S3::AccessGrantsInstance` | Its `IdentityCenterArn` property also performs the association, which the earlier research listed as a separate step |
| 8 | Access Grants location + role | `AWS::S3::AccessGrantsLocation`, `AWS::IAM::Role` | |
| 9 | Access grants | `AWS::S3::AccessGrant` | |
| 10 | **Trusted token issuer** | **none** | `sso-admin:CreateTrustedTokenIssuer` |
| 11 | **Application grant** (jwt-bearer) | **none** | `sso-admin:PutApplicationGrant` |
| 12 | **Application access scope** | **none** | `sso-admin:PutApplicationAccessScope` |
| 13 | **Application authentication method** | **none** | `sso-admin:PutApplicationAuthenticationMethod` |
| 14 | **Assignment configuration** | **none** | `sso-admin:PutApplicationAssignmentConfiguration`, if setting `AssignmentRequired: false` instead of using row 6 |
| — | Customer-managed SAML 2.0 application | **console only** | Tier 3 only. Not creatable by any API |

`AWS::SSO::*` offers exactly six resource types — `Application`, `ApplicationAssignment`,
`Assignment`, `Instance`, `InstanceAccessControlAttributeConfiguration`, `PermissionSet`
([AWS::SSO resource list](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/AWS_SSO.html)).
The absence of a trusted-token-issuer type is not an oversight in the docs; it has never existed.

Terraform and Pulumi *do* expose `ssoadmin_trusted_token_issuer` and friends, which is easy to
misread as CloudFormation coverage. They call the `sso-admin` API directly and are not evidence of a
CloudFormation resource type. Connapse should not adopt a Terraform dependency to get four API
calls it can make with the CLI.

---

## The finding that changes the shape

The earlier research assumed the Cognito pool federates to Identity Center over SAML, so that users
authenticate with the credentials they already have and Cognito provisions profiles automatically.
That is a good property, and it carries a cost nothing else in this design carries: the SAML
application it needs on the Identity Center side cannot be created by any API.

`AWS::SSO::Application`'s own documentation says so plainly: "This API does not support creating
SAML 2.0 customer managed applications... You can create a SAML 2.0 customer managed application in
the AWS Management Console only"
([AWS::SSO::Application](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-sso-application.html)).

It is also circular. Cognito's SAML identity provider needs the Identity Center application's
metadata; the Identity Center SAML application needs Cognito's ACS URL
(`https://<domain>/saml2/idpresponse`) and entity ID (`urn:amazon:cognito:sp:<poolId>`), which do
not exist until the pool and domain do. So even with an API it would be two phases.

Since the trusted token issuer is independent of how Identity Center authenticates anyone, three
setups are available, and the difficulty is entirely in the choice:

**Tier 1 — Cognito's own users.** Fully automatable, one script, no console. The administrator (or
Connapse) creates a Cognito user whose email matches the person's Identity Center email. The cost is
a second credential: the user sets a Cognito password and signs in with it once when connecting.
Since connecting is a one-time act per user and the refresh token is durable, "once" is closer to
literal here than it sounds.

**Tier 2 — Cognito federated to the customer's existing IdP.** Fully automatable, one script, no
console — *if* the customer's Identity Center is fed by an external IdP over SCIM, which is the
common enterprise arrangement. Cognito federates to that same IdP rather than to Identity Center,
using `AWS::Cognito::UserPoolIdentityProvider` with the IdP's metadata URL, which the customer
already has. Same credentials, same directory, no console step, no circularity. **This is the best
outcome available and it should be the recommended path.**

**Tier 3 — Cognito federated to Identity Center over SAML.** The originally-designed path. Needed
only when Identity Center's users live in its own built-in directory with no external IdP behind it.
Requires the console step and a two-phase script.

Tier 1 is what a solo self-hoster wants. Tier 2 is what an enterprise wants. Tier 3 is the fallback
for a mid-sized deployment on the built-in directory, and is the only one that cannot be one action.

---

## Recommended shape

**One CloudShell script, following `AwsIamUserSetup`.** That precedent already solves the problems
this shares: it is shown in full so the administrator can read what it creates before running it, it
runs where credentials already exist so nothing is pasted into Connapse that shouldn't be, and it
prints a delimited block (`----- BEGIN CONNAPSE AWS KEY -----`) that Connapse parses back.

The script writes a CloudFormation template to a heredoc, `aws cloudformation deploy`s it, then
makes the four `sso-admin` calls with the stack outputs, then prints the settings block. Rows 1–9
above are the stack; rows 10–14 are the calls after it.

**Why not a quick-create "Launch Stack" link.** Quick-create links require the template to live in
an S3 bucket the console can read — "the location for an Amazon S3 bucket must start with
`https://`", and S3 static website URLs are not supported
([quick-create links](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/cfn-console-create-stacks-quick-create-links.html)).
A self-hosted Connapse cannot serve its own template to the AWS console, so this would mean
publishing a public bucket per release — new infrastructure for the project, and it still leaves
rows 10–14 undone.

**Why not a Lambda-backed custom resource** to close the CloudFormation gap and make it one stack.
It is the textbook fix and it is worse here. Inline `ZipFile` code is capped at 4096 bytes and the
custom resource response body has the same cap
([cfn-response](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/cfn-lambda-function-code-cfnresponsemodule.html)),
so the handler would be squeezed or would need a packaged artifact — which is the hosting problem
again. It also puts a Lambda, a role and a log group into the customer's account permanently, to
perform four one-time calls, and turns every setup failure into a CloudFormation rollback with the
real error inside CloudWatch rather than on the administrator's screen.

**What Connapse reads back.** Beyond the five fields `CognitoSettings` already holds, the exchange
needs the **Identity Center application ARN** — `sso-oauth:CreateTokenWithIAM` takes it as
`clientId`. `CognitoSettings` has no field for it today. That is a gap in the shipped model, not
just in the setup script, and 5b-ii or 5c has to add it.

---

## Two decisions that belong to the administrator

Both were raised after the first draft, and both have consequences past the script itself.

### The setup runs on the administrator's permissions, never on Connapse's

The script executes in CloudShell as whoever is signed in. Connapse issues the text and reads the
result; it never provisions anything, and it never needs a credential that could. Three things
follow.

**Connapse's own policy stays read-only, and grows only for detection.** The `connapse-reader` user
that `AwsIamUserSetup` creates has S3 read access and nothing else. Detection needs a little more —
`sso-admin:List*`/`Describe*`/`Get*`, `s3control:GetAccessGrantsInstance`/`ListAccessGrants*`, and
`identitystore:ListUsers` — all read-only. No write permission enters that policy at any point,
which is the property that makes the whole provisioning surface safe to hand an administrator: the
worst a compromised Connapse can do with it is read its own setup.

**Remediation is always a new script, never a repair.** Because Connapse cannot write, drift cannot
be corrected in place. Detection reports; the administrator re-runs. That is more honest than a
self-healing setup would be, and it means the detection page must name the specific missing piece
well enough to act on — which is what makes the `sso-admin` Get operations worth calling
individually rather than stopping at the single strong health check.

**The script must verify the administrator before it starts, not during.** The permissions it needs
are broad and split across services, and the account may be wrong outright — with an organization
instance, the `sso-admin` writes only work from the management or delegated-admin account. A script
that discovers this at step nine has already created a Cognito pool and an Access Grants location
that nobody asked for. It should check the caller's account against `ListInstances` and fail with
one sentence before creating anything.

### The host is the administrator's to declare, before the script is generated

Connapse cannot infer its own external address. Behind a reverse proxy the request host is the
internal one; in Docker it is a container name; on a laptop it is `localhost`. So the AWS provider
page needs a base-URL field the administrator fills in, and the generated script bakes the callback
`{base}/api/v1/auth/cloud/cognito/callback` into the app client.

**What Cognito accepts.** HTTPS is required except for `http://localhost`, `http://127.0.0.1` and
`http://[::1]`, all of which may use plain HTTP and may carry a custom port
([app client settings](https://docs.aws.amazon.com/cognito/latest/developerguide/user-pool-settings-client-apps.html)).
The field should validate exactly that set and say why, since the alternative is an error from AWS
at the end of a long setup.

**This has to become a stored setting, not just a script input.** `CloudIdentityEndpoints` builds
the redirect URI from `Request.Scheme` and `Request.Host` at
[CloudIdentityEndpoints.cs:440](src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs#L440). Once a
script has pre-registered a callback the administrator chose, a request-derived value can disagree
with it — and Cognito rejects a `redirect_uri` that does not match the registered one exactly. The
declared host must be what the runtime sends, or setup can be entirely correct and connecting still
fails with an error that points at neither.

**Changing the host later means re-running the script**, because the callback lives on the app
client. Worth saying on the form rather than discovering: it makes the field feel consequential,
which it is.

## What the script cannot do

**It cannot run in the wrong account.** With an organization instance the Identity Center instance
"must always reside in the management account", so rows 10–14 must run in the management or
delegated-admin account — which is usually not the account the administrator is already in.

**It cannot fix an unassigned application silently.** A new customer-managed application has
`AssignmentRequired: true` and no assignees, and the exchange then fails with a bare
`AccessDeniedException` naming nothing — the single worst error surface in this chain, established
by the spike. The script must either create assignments (row 6) or set `AssignmentRequired: false`
(row 14), and the detection page must check it explicitly and say so in words.

**It cannot choose the tier.** Which of the three applies depends on how the customer's Identity
Center gets its users, which Connapse can partly detect — `identitystore:ListUsers` plus
`sso-admin:ListInstances` shows whether an external IdP is configured — but should confirm rather
than assume.

---

## Open questions, in the order they would bite

**Can Connapse's IAM *user* be the actor principal?** `PutApplicationAuthenticationMethod` takes an
`ActorPolicy` naming the principal permitted to call `CreateTokenWithIAM`. The spike used a role ARN
and found that a wrong ARN yields `Invalid actor policy provided`. Connapse authenticates as an IAM
user (`connapse-reader`, created by `AwsIamUserSetup`), not a role. Whether an IAM user ARN is
accepted there is untested and is the single assumption most likely to invalidate the setup design —
it should be spiked before 5c is planned, and it is a ten-minute check against the live account.

**Does tier 1 actually satisfy the email match?** A Cognito-native user's `email` claim is only
verified if the pool is configured to require it, and the trusted token issuer matches on
`emails.value` in the identity store. An unverified email that happens to match would be an identity
confusion; the pool must be created with email verification required and admin-only user creation.

**Does `ListCallerAccessGrants` complete the chain?** Still untested — the spike account had no
Access Grants instance. It is the last unproven link and everything upstream of it is now evidence
rather than reasoning.

---

## Sources

- [AWS::SSO resource type list](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/AWS_SSO.html)
- [AWS::SSO::Application](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-sso-application.html)
- [AWS::S3::AccessGrantsInstance](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-s3-accessgrantsinstance.html)
- [AWS::S3::AccessGrantsLocation](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-s3-accessgrantslocation.html)
- [AWS::S3::AccessGrant](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-s3-accessgrant.html)
- [AWS::Cognito::UserPoolIdentityProvider](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/aws-resource-cognito-userpoolidentityprovider.html)
- [Simplify workforce identity management using IAM Identity Center and trusted token issuers](https://aws.amazon.com/blogs/security/simplify-workforce-identity-management-using-iam-identity-center-and-trusted-token-issuers/)
- [Setting up a trusted token issuer](https://docs.aws.amazon.com/singlesignon/latest/userguide/setuptrustedtokenissuer.html)
- [Setting up customer managed SAML 2.0 applications](https://docs.aws.amazon.com/singlesignon/latest/userguide/customermanagedapps-saml2-setup.html)
- [Using SAML identity providers with a user pool](https://docs.aws.amazon.com/cognito/latest/developerguide/cognito-user-pools-saml-idp.html)
- [Use quick-create links to create CloudFormation stacks](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/cfn-console-create-stacks-quick-create-links.html)
- [cfn-response module](https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/cfn-lambda-function-code-cfnresponsemodule.html)
- [How to implement trusted identity propagation for applications protected by Amazon Cognito](https://aws.amazon.com/blogs/security/how-to-implement-trusted-identity-propagation-for-applications-protected-by-amazon-cognito/)
