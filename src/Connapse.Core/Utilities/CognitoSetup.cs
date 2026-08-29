namespace Connapse.Core.Utilities;

/// <summary>What the setup script needs to know before it can be written.</summary>
/// <param name="CallbackUrl">
/// Where Cognito returns a user. Registered on the app client, and must equal what Connapse sends
/// at connect time — see <see cref="CognitoRedirect"/>.
/// </param>
/// <param name="ActorArn">
/// The AWS identity Connapse itself authenticates as. It is named in the application's
/// authentication method as the principal permitted to exchange a token.
/// </param>
/// <param name="IdpMetadataUrl">
/// The SAML metadata URL of an identity provider the customer already runs, or null. Null gives a
/// pool that holds its own users — the only arrangement that needs no prerequisites.
/// </param>
/// <param name="NamePrefix">Prefix for every created resource, so they are obvious and findable.</param>
/// <param name="Region">
/// Where the Identity Center instance was found, which is where everything else must be created.
/// Null falls back to the CloudShell session's own region.
/// <para>
/// Worth passing whenever it is known. Identity Center lives in exactly one region per
/// organisation, and CloudShell opens in whichever region the console was last on — so the default
/// is right by coincidence rather than by construction, and being wrong reads as having no instance
/// at all rather than as looking in the wrong place.
/// </para>
/// </param>
public record CognitoSetupRequest(
    string CallbackUrl,
    string ActorArn,
    string? IdpMetadataUrl = null,
    string? NamePrefix = null,
    string? Region = null);

/// <summary>The settings the script printed.</summary>
public record CognitoSetupResult(
    string IssuerUrl,
    string Domain,
    string ClientId,
    string ClientSecret,
    string Region,
    string ApplicationArn,
    string IdentityProvider = "");

/// <summary>
/// Builds the command that provisions the AWS side of per-user permissions, and reads back what it
/// prints.
/// </summary>
/// <remarks>
/// The same shape as <see cref="AwsIamUserSetup"/>, and for the same reasons: shown in full so an
/// administrator can read what it creates before running it, run in CloudShell where their
/// credentials already are so none are ever pasted into Connapse, and answered with a delimited
/// block rather than a dozen fields copied by eye.
/// <para>
/// A CloudFormation template rather than a shell script, because only the first is reviewable by
/// reading it: a template declares what will exist, and CloudFormation lists it and asks for
/// acknowledgement before creating IAM resources. It covers the Cognito pool and the Access Grants
/// instance.
/// <para>
/// The <c>sso-admin</c> calls that used to follow it are gone with the token exchange. They created
/// a trusted token issuer, an application, its grant, its access scope and its authentication
/// method — all of which existed so Connapse could exchange a Cognito token for an identity
/// context, which it no longer does. See
/// <c>docs/research/aws-scoped-delegated-access-2026-08-29.md</c>.
/// </para>
/// <para>
/// <b>It creates no grant.</b> The Access Grants instance, its location and that location's role are
/// infrastructure and are created here. A grant names who may read what, and that is the
/// administrator's decision, authored in AWS, which Connapse reads and never writes. This is the
/// standing constraint of the whole feature and this file is the most tempting place to break it.
/// </para>
/// </remarks>
public static class CognitoSetup
{
    public const string BeginMarker = "----- BEGIN CONNAPSE COGNITO SETUP -----";

    public const string EndMarker = "----- END CONNAPSE COGNITO SETUP -----";

    /// <summary>Default prefix for created resources.</summary>
    public const string DefaultNamePrefix = "connapse";

    /// <summary>The permissions the script itself needs, for the page to state up front.</summary>
    /// <remarks>
    /// Broad, and worth showing rather than discovering: these are the administrator's own
    /// permissions, used once. Connapse never holds any of them — its own policy stays read-only.
    /// </remarks>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "cloudformation:*", "cognito-idp:*", "sso:*", "sso-directory:Describe*",
        "identitystore:List*", "s3:CreateAccessGrantsInstance", "s3:CreateAccessGrantsLocation",
        "iam:CreateRole", "iam:PutRolePolicy", "iam:PassRole"
    ];

    /// <summary>
    /// Where Identity Center posts its SAML assertion, for a pool at <paramref name="domain"/>.
    /// </summary>
    /// <remarks>
    /// Fixed by Cognito's contract — the pool domain plus <c>/saml2/idpresponse</c> — but derived
    /// rather than left to be assembled by hand, because getting it wrong fails at sign-in rather
    /// than at setup. Null when there is no pool yet, which is what makes federation a second pass.
    /// </remarks>
    public static string? SamlAcsUrl(string? domain) =>
        string.IsNullOrWhiteSpace(domain) ? null : $"{domain.TrimEnd('/')}/saml2/idpresponse";

    /// <summary>
    /// The audience Identity Center must claim, for a pool whose issuer is
    /// <paramref name="issuerUrl"/>.
    /// </summary>
    /// <remarks>
    /// Cognito derives this from the pool id, and the pool id is the last segment of the issuer, so
    /// it is read from there rather than stored a second time.
    /// </remarks>
    public static string? SamlAudience(string? issuerUrl) =>
        string.IsNullOrWhiteSpace(issuerUrl)
            ? null
            : $"urn:amazon:cognito:sp:{issuerUrl.TrimEnd('/').Split('/')[^1]}";

    /// <summary>The CloudFormation template, for the administrator to read and upload.</summary>
    /// <remarks>
    /// A separate artifact rather than a heredoc inside the script, for two reasons that happen to
    /// point the same way.
    /// <para>
    /// It is the reviewable part. Every comparable product — Rapid7's AWS onboarding is typical —
    /// hands the customer a template to download and inspect, then deploys it through
    /// CloudFormation's own console or CLI, where AWS itself lists what will be created and asks
    /// for acknowledgement before creating IAM resources. A template is a declaration of what will
    /// exist; a shell script is an instruction to do something. Only the first can be reviewed by
    /// reading it.
    /// </para>
    /// <para>
    /// It is also what made the script unpasteable. A hundred and fifteen lines of heredoc puts an
    /// interactive shell into continuation mode for the whole block, and CloudShell disconnected
    /// part-way through it twice. What is left is plain commands, one per line.
    /// </para>
    /// </remarks>
    public static string GenerateTemplate() =>
        """
        AWSTemplateFormatVersion: '2010-09-09'
        Parameters:
          Prefix: { Type: String }
          DomainPrefix: { Type: String }
          CallbackUrl: { Type: String }
          InstanceArn: { Type: String }
          IdpMetadataUrl: { Type: String, Default: '' }
        Conditions:
          Federated: !Not [ !Equals [ !Ref IdpMetadataUrl, '' ] ]
        Resources:
          Pool:
            Type: AWS::Cognito::UserPool
            Properties:
              UserPoolName: !Sub '${Prefix}-pool'
              # Managed login — the modern sign-in page set below — is only served on the
              # Essentials and Plus plans; the Lite plan can serve the classic hosted UI and
              # nothing else. Essentials is already the default for a new pool, so naming it here
              # costs nothing extra. It is written down so the branding version below cannot end up
              # silently ignored, which is how this fails: the pool still works and just serves the
              # old page.
              UserPoolTier: ESSENTIALS
              # Admin-created only, and email verified. The trusted token issuer matches this pool's
              # email claim against an Identity Center user, so an unverified address that happened
              # to match would be an identity confusion rather than a sign-in problem.
              AdminCreateUserConfig: { AllowAdminCreateUserOnly: true }
              AutoVerifiedAttributes: [ email ]
              UsernameAttributes: [ email ]
              Schema:
                - Name: email
                  Required: true
                  Mutable: true
          Domain:
            Type: AWS::Cognito::UserPoolDomain
            Properties:
              Domain: !Ref DomainPrefix
              UserPoolId: !Ref Pool
              # Version 1 is the classic hosted UI, version 2 is managed login. This is set on the
              # domain rather than the client, so it applies to every app client served here.
              ManagedLoginVersion: 2
          Idp:
            Type: AWS::Cognito::UserPoolIdentityProvider
            Condition: Federated
            Properties:
              UserPoolId: !Ref Pool
              ProviderName: Workforce
              ProviderType: SAML
              ProviderDetails: { MetadataURL: !Ref IdpMetadataUrl }
              # `userName` is the join key: the trusted token issuer resolves it against the
              # Identity Center directory. It is carried as preferred_username because a trusted
              # token issuer matches one claim against one of exactly three identity-store
              # attributes — user name, email or external ID — and of those three this is the only
              # one that always holds a value. External ID is populated by SCIM sync alone, and a
              # federated user's email is unverified by construction: Cognito marks a mapped
              # address unverified and cannot verify it with a one-time code.
              #
              # `email` stays mapped for display. Nothing authorizes from it.
              AttributeMapping: { email: email, preferred_username: userName }
          Client:
            Type: AWS::Cognito::UserPoolClient
            Properties:
              ClientName: !Sub '${Prefix}-client'
              UserPoolId: !Ref Pool
              GenerateSecret: true
              AllowedOAuthFlows: [ code ]
              AllowedOAuthScopes: [ openid, email, profile ]
              AllowedOAuthFlowsUserPoolClient: true
              CallbackURLs: [ !Ref CallbackUrl ]
              # !Ref rather than the name, which reads the same and is not the same: Ref on an
              # identity provider returns its ProviderName, and referencing it makes CloudFormation
              # create the provider before this client. Naming it as a literal left the two
              # unordered, and the client was built first: "The provider Workforce does not exist
              # for User Pool us-west-1_...".
              SupportedIdentityProviders: !If
                - Federated
                - [ !Ref Idp ]
                - [ COGNITO ]
          # Despite the resource name, this applies no branding of ours. UseCognitoProvidedValues
          # means "use Cognito's own default style" and the property requires that Settings and
          # Assets be omitted entirely, so there is nowhere for a Connapse logo or colour to be put
          # even by accident. The sign-in page stays plain AWS.
          #
          # It cannot simply be left out. Creating an app client in the console attaches a style to
          # it automatically; creating one any other way — CloudFormation included — does not, and
          # AWS will not serve managed login to a client with no style record. Omit this and the
          # two settings above are still accepted, the stack still reaches CREATE_COMPLETE, and
          # sign-in still serves the old classic page with nothing anywhere reporting a problem.
          LoginStyle:
            Type: AWS::Cognito::ManagedLoginBranding
            Properties:
              UserPoolId: !Ref Pool
              ClientId: !Ref Client
              UseCognitoProvidedValues: true
          # Registers s3:// so grants may be written anywhere. Registering a location is not
          # granting access to it; it is declaring which data Access Grants may govern.
          LocationRole:
            Type: AWS::IAM::Role
            Properties:
              RoleName: !Sub '${Prefix}-access-grants-location'
              AssumeRolePolicyDocument:
                Version: '2012-10-17'
                Statement:
                  - Effect: Allow
                    Principal: { Service: access-grants.s3.amazonaws.com }
                    Action: [ 'sts:AssumeRole', 'sts:SetSourceIdentity' ]
                  # Without sts:SetContext the identity never reaches S3 and every lookup comes
                  # back empty, with nothing anywhere saying why.
                  - Effect: Allow
                    Principal: { Service: access-grants.s3.amazonaws.com }
                    Action: 'sts:SetContext'
                    Condition:
                      'ForAllValues:ArnEquals':
                        'sts:RequestContextProviders':
                          - arn:aws:iam::aws:contextProvider/IdentityCenter
              Policies:
                - PolicyName: ReadRegisteredData
                  PolicyDocument:
                    Version: '2012-10-17'
                    Statement:
                      - Effect: Allow
                        Action: [ 's3:GetObject', 's3:ListBucket' ]
                        Resource: [ '*' ]
          GrantsInstance:
            Type: AWS::S3::AccessGrantsInstance
            Properties:
              IdentityCenterArn: !Ref InstanceArn
          Location:
            Type: AWS::S3::AccessGrantsLocation
            DependsOn: GrantsInstance
            Properties:
              LocationScope: 's3://'
              IamRoleArn: !GetAtt LocationRole.Arn
        Outputs:
          PoolId: { Value: !Ref Pool }
          ClientId: { Value: !Ref Client }
        """;

    /// <summary>
    /// The command to paste into AWS CloudShell.
    /// </summary>
    /// <remarks>
    /// Ordered so that nothing is created before the account has been checked. With an organization
    /// instance the <c>sso-admin</c> writes only work from the management or delegated-admin
    /// account, and a script that discovers that at the last step has already left a Cognito pool
    /// and an Access Grants location behind that nobody asked for.
    /// </remarks>
    public static string GenerateScript(CognitoSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CallbackUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActorArn);

        string prefix = SanitisePrefix(request.NamePrefix);
        string idp = request.IdpMetadataUrl?.Trim() ?? string.Empty;
        string region = request.Region?.Trim() ?? string.Empty;

        return FlattenContinuations($$"""
        # Sets up per-user AWS permissions for Connapse. Creates, in your account:
        #   a Cognito user pool, its domain and one app client
        #   an S3 Access Grants instance, one location covering s3://, and that location's role
        #
        # It creates NO access grants. Who may read what stays yours to decide, in AWS.
        # Nothing existing is modified.

        # Set by any check below that fails. The settings block at the end is withheld when it
        # is set, so nothing from a half-finished run is pasted back into Connapse.
        FAILED=""
        PREFIX='{{prefix}}'
        CALLBACK='{{request.CallbackUrl}}'
        IDP_METADATA='{{idp}}'
        STACK="$PREFIX-permissions"

        # Pinned to where Connapse found the Identity Center instance, when it has. Falling back
        # to the session's region is a guess: CloudShell opens wherever the console last was.
        REGION="{{region}}"
        [ -n "$REGION" ] || REGION="${AWS_REGION:-$(aws configure get region)}"
        [ -n "$REGION" ] || { echo 'No region set. Run: export AWS_REGION=us-east-1'; FAILED=1; }
        ACCOUNT=$(aws sts get-caller-identity --query Account --output text)

        # Checked before anything is created. A missing instance, or the right instance seen from
        # the wrong account, is the difference between this working and it half-working.
        INSTANCE=$(aws sso-admin list-instances --region "$REGION" \
                     --query 'Instances[0].InstanceArn' --output text 2>/dev/null || true)
        if [ -z "$INSTANCE" ] || [ "$INSTANCE" = 'None' ]; then
          echo "No IAM Identity Center instance is visible in $REGION from account $ACCOUNT."
          echo 'With an organization instance, run this in the management or delegated-admin account.'
          FAILED=1
        fi
        IDENTITY_STORE=$(aws sso-admin list-instances --region "$REGION" \
                           --query 'Instances[0].IdentityStoreId' --output text)

        # Globally unique, and stable for a given account and region so re-running is idempotent.
        DOMAIN_PREFIX="$PREFIX-$ACCOUNT-$REGION"

        # The template is a separate file you downloaded from Connapse and can read before
        # running any of this. Upload it here with Actions -> Upload file.
        TEMPLATE_FILE="${TEMPLATE_FILE:-connapse-permissions.yaml}"
        [ -f "$TEMPLATE_FILE" ] || {
          echo "Cannot find $TEMPLATE_FILE in $(pwd)."
          echo 'Download it from Connapse, then upload it here: Actions -> Upload file.'
          FAILED=1
        }


        aws cloudformation deploy --region "$REGION" \
          --stack-name "$STACK" --template-file "$TEMPLATE_FILE" \
          --capabilities CAPABILITY_NAMED_IAM \
          --parameter-overrides Prefix="$PREFIX" DomainPrefix="$DOMAIN_PREFIX" \
            CallbackUrl="$CALLBACK" InstanceArn="$INSTANCE" IdpMetadataUrl="$IDP_METADATA" || {
          echo 'The stack did not deploy. Everything below reads values from it, so they describe'
          echo 'whatever the stack rolled back to rather than what you asked for. The reason is in'
          echo 'the output above; this names the resource that failed:'
          echo "  aws cloudformation describe-stack-events --region $REGION --stack-name $STACK \\"
          echo "    --query \"StackEvents[?ResourceStatusReason!=null].[LogicalResourceId,ResourceStatusReason]\" --output text"
          FAILED=1
        }

        out() { aws cloudformation describe-stacks --region "$REGION" --stack-name "$STACK" \
                  --query "Stacks[0].Outputs[?OutputKey=='$1'].OutputValue" --output text; }
        POOL_ID=$(out PoolId); CLIENT_ID=$(out ClientId)

        # The stack can be "up to date" and still describe a pool that no longer exists: deleting
        # one in the console does not tell CloudFormation, and deploy reports no changes because
        # the template has not changed. Without this the next call fails with
        # ResourceNotFoundException naming a pool id that came from the stack itself, which reads
        # like a bug in the script rather than drift in the account.
        aws cognito-idp describe-user-pool --region "$REGION" --user-pool-id "$POOL_ID"           >/dev/null 2>&1 || {
          echo "Stack $STACK refers to pool $POOL_ID, which no longer exists."
          echo "Something removed it outside CloudFormation. Delete the stack and run this again:"
          echo "  aws cloudformation delete-stack --region $REGION --stack-name $STACK"
          FAILED=1
        }

        ISSUER="https://cognito-idp.$REGION.amazonaws.com/$POOL_ID"
        DOMAIN="https://$DOMAIN_PREFIX.auth.$REGION.amazoncognito.com"

        # CloudFormation cannot return a secret, so it is read back rather than output.
        SECRET=$(aws cognito-idp describe-user-pool-client --region "$REGION" \
                   --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" \
                   --query 'UserPoolClient.ClientSecret' --output text)

        # The provider Connapse sends people straight to. Matches the template's ProviderName.
        if [ -n "$IDP_METADATA" ]; then IDP_NAME='Workforce'; else IDP_NAME=''; fi

        # One printf rather than one per value: an interactive shell echoes each pasted command
        # as it runs, which would put a prompt line between every line of the block.
        if [ -n "$FAILED" ]; then
          echo 'Setup did not finish. Fix the error above and run this again.'
        else
        printf '\n%s\nissuerUrl=%s\ndomain=%s\nclientId=%s\nclientSecret=%s\nregion=%s\nidentityProvider=%s\n%s\n\n' '{{BeginMarker}}' "$ISSUER" "$DOMAIN" "$CLIENT_ID" "$SECRET" "$REGION" "$IDP_NAME" '{{EndMarker}}'
        echo "Copy the block above into Connapse."
        echo "Then, in AWS, write access grants saying who may read what. Identity store: $IDENTITY_STORE"
        fi
        """);
    }

    /// <summary>
    /// Joins every backslash-continued line into one, so the shell never continues.
    /// </summary>
    /// <remarks>
    /// The source below keeps its continuations because a wrapped <c>aws</c> call is far easier to
    /// read and to diff. What an administrator pastes should not have them: a continuation puts an
    /// interactive shell into a secondary prompt, and buffering a long run of those is what
    /// disconnected AWS CloudShell part-way through the old heredoc, and what still echoed out of
    /// order at 148 lines afterwards. Flattening is done on the way out so the readable form and
    /// the pasted form stay the same text — the page displays the result of this, not the source.
    /// <para>
    /// Comments are left alone. They never end in a backslash, and they are the part worth reading.
    /// </para>
    /// </remarks>
    private static string FlattenContinuations(string script)
    {
        var flattened = new List<string>();

        foreach (string line in script.ReplaceLineEndings("\n").Split('\n'))
        {
            bool continuing = flattened.Count > 0
                              && flattened[^1].EndsWith('\\');

            if (continuing)
            {
                // Trim the backslash and the continuation's own indentation; one space is what the
                // shell would have seen anyway.
                flattened[^1] = flattened[^1][..^1].TrimEnd() + " " + line.TrimStart();
                continue;
            }

            flattened.Add(line);
        }

        return string.Join("\n", flattened);
    }

    /// <summary>
    /// Reads the settings block the script printed. Returns null when the text has no usable one.
    /// </summary>
    /// <remarks>
    /// Anchored on the <b>last</b> marker pair, because the script contains both markers in its own
    /// text — printing them is its job — so a pasted terminal buffer holds each twice and the first
    /// pair selects the echoed source rather than the output.
    /// </remarks>
    public static CognitoSetupResult? ParseResult(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        int end = pasted.LastIndexOf(EndMarker, StringComparison.Ordinal);
        int start = end < 0 ? -1 : pasted.LastIndexOf(BeginMarker, end, StringComparison.Ordinal);

        if (start < 0 || end <= start)
            return null;

        string? issuer = null, domain = null, clientId = null;
        string? secret = null, region = null, appArn = null, idpName = null;

        foreach (string raw in pasted[(start + BeginMarker.Length)..end]
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int split = line.IndexOf('=');
            if (split <= 0) continue;

            string value = line[(split + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (line[..split].Trim())
            {
                case "issuerUrl": issuer = value; break;
                case "domain": domain = value; break;
                case "clientId": clientId = value; break;
                case "clientSecret": secret = value; break;
                case "region": region = value; break;
                case "applicationArn": appArn = value; break;
                // Optional by design. A pool with local users prints an empty value here, and the
                // loop skips empty values, so this stays null for them rather than failing the
                // all-or-nothing check below — which would reject a perfectly good paste.
                case "identityProvider": idpName = value; break;
            }
        }

        // All six or nothing. A partial block would save settings that pass IsConfigured while
        // being unable to complete a connection, which fails later and somewhere else.
        return issuer is null || domain is null || clientId is null
               || secret is null || region is null || appArn is null
            ? null
            : new CognitoSetupResult(issuer, domain, clientId, secret, region, appArn,
                idpName ?? string.Empty);
    }

    /// <summary>
    /// Coerces a prefix to what AWS resource names and a Cognito domain both accept.
    /// </summary>
    /// <remarks>
    /// Lower-case letters, digits and hyphens. The domain prefix is the strictest of the several
    /// naming rules this feeds, and a value that breaks it fails deep inside a CloudFormation
    /// rollback rather than here.
    /// </remarks>
    public static string SanitisePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return DefaultNamePrefix;

        string cleaned = new(prefix.Trim().ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());

        cleaned = cleaned.Trim('-');

        // Cognito domain prefixes may not begin with a digit, and the script appends an account id
        // and region, so the room left for the prefix itself is small.
        if (cleaned.Length == 0 || char.IsAsciiDigit(cleaned[0]))
            return DefaultNamePrefix;

        return cleaned.Length > 20 ? cleaned[..20].TrimEnd('-') : cleaned;
    }
}
