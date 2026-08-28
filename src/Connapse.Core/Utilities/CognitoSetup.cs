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
/// <param name="ExistingPoolId">
/// A Cognito user pool the customer already runs, to add Connapse to rather than duplicate. Null
/// creates a new pool.
/// </param>
/// <param name="ExistingDomainPrefix">
/// The hosted domain that pool already has, when adopting one that has a domain. Null lets the
/// script create one, which is additive to the pool and safe.
/// </param>
public record CognitoSetupRequest(
    string CallbackUrl,
    string ActorArn,
    string? IdpMetadataUrl = null,
    string? NamePrefix = null,
    string? ExistingPoolId = null,
    string? ExistingDomainPrefix = null);

/// <summary>One Cognito user pool the discovery script found.</summary>
/// <param name="PoolId">The pool's id, which is also what its issuer URL is built from.</param>
/// <param name="Name">What it is called, for a human choosing between them.</param>
/// <param name="DomainPrefix">Its hosted domain prefix, or null when it has none yet.</param>
/// <param name="VerifiesEmail">
/// Whether the pool verifies email. A pool that does not cannot be matched to an Identity Center
/// user, because the trusted token issuer joins on the email claim.
/// </param>
public record CognitoPoolSummary(
    string PoolId,
    string Name,
    string? DomainPrefix,
    bool VerifiesEmail)
{
    /// <summary>Whether Connapse can be added to this pool at all.</summary>
    public bool IsUsable => VerifiesEmail;
}

/// <summary>The settings the script printed.</summary>
public record CognitoSetupResult(
    string IssuerUrl,
    string Domain,
    string ClientId,
    string ClientSecret,
    string Region,
    string ApplicationArn);

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
/// Two artifacts, because the AWS surface is split and because only one of them is reviewable by
/// reading it. A CloudFormation template covers the
/// Cognito pool, the Identity Center application and the Access Grants instance; four
/// <c>sso-admin</c> calls follow it because the trusted token issuer, the application's grant, its
/// access scope and its authentication method have no CloudFormation resource type and never have.
/// See <c>docs/research/cognito-setup-automation-2026-08-28.md</c>.
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

    public const string PoolsBeginMarker = "----- BEGIN CONNAPSE COGNITO POOLS -----";

    public const string PoolsEndMarker = "----- END CONNAPSE COGNITO POOLS -----";

    /// <summary>
    /// The command that lists the Cognito pools already in this account, so an administrator can
    /// add Connapse to one rather than be given a second.
    /// </summary>
    /// <remarks>
    /// Reports the two facts that decide whether a pool can be used, rather than every fact about
    /// it. A pool needs a hosted domain for the browser sign-in — absent is fine, the setup script
    /// adds one — and it must verify email, because the trusted token issuer joins to an Identity
    /// Center user on the email claim and nothing else this deployment controls.
    /// </remarks>
    public static string GenerateDiscoveryScript()
    {
        return $$"""
        # Lists the Amazon Cognito user pools in this account and region. Reads only; creates
        # and changes nothing.

        set -e
        REGION="${AWS_REGION:-$(aws configure get region)}"
        [ -n "$REGION" ] || { echo 'No region set. Run: export AWS_REGION=us-east-1'; exit 1; }

        # Built up and printed at the end, not as it goes. An interactive shell echoes a pasted
        # multi-line command, and printing the markers inline puts that echo between them.
        BLOCK=$(
          printf '%s\n' '{{PoolsBeginMarker}}'
          aws cognito-idp list-user-pools --region "$REGION" --max-results 60 \
            --query 'UserPools[].[Id,Name]' --output text | while IFS=$(printf '\t') read -r ID NAME; do
            [ -z "$ID" ] && continue

            # One query each. Asking for both in a single --query puts them on separate lines,
            # because AutoVerifiedAttributes is a list and --output text gives a list its own row —
            # so reading them positionally landed the attribute in the domain and left the pool
            # looking like it does not verify email when it does.
            DOMAIN=$(aws cognito-idp describe-user-pool --region "$REGION" --user-pool-id "$ID" \
                       --query 'UserPool.Domain' --output text 2>/dev/null || true)
            VERIFIED=$(aws cognito-idp describe-user-pool --region "$REGION" --user-pool-id "$ID" \
                         --query 'UserPool.AutoVerifiedAttributes' --output text 2>/dev/null || true)

            printf 'pool=%s\t%s\t%s\t%s\n' "$ID" "$NAME" \
              "$([ -z "$DOMAIN" ] || [ "$DOMAIN" = 'None' ] && printf -- '-' || printf '%s' "$DOMAIN")" \
              "$(printf '%s' "$VERIFIED" | grep -q email && printf 'email' || printf -- '-')"
          done
          printf '%s\n' '{{PoolsEndMarker}}'
        )

        printf '\n%s\n\n' "$BLOCK"
        echo 'Copy the block above into Connapse.'
        """;
    }

    /// <summary>
    /// Reads the pool list the discovery script printed. Empty when the text has no usable block —
    /// which is not the same as an account with no pools, and the caller should say so.
    /// </summary>
    public static IReadOnlyList<CognitoPoolSummary> ParsePools(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return [];

        int end = pasted.LastIndexOf(PoolsEndMarker, StringComparison.Ordinal);
        int start = end < 0 ? -1 : pasted.LastIndexOf(PoolsBeginMarker, end, StringComparison.Ordinal);

        if (start < 0 || end <= start)
            return [];

        var pools = new List<CognitoPoolSummary>();

        foreach (string raw in pasted[(start + PoolsBeginMarker.Length)..end]
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (!line.StartsWith("pool=", StringComparison.Ordinal)) continue;

            string[] parts = line[5..].Split('\t');
            if (parts.Length < 4) continue;

            string id = parts[0].Trim();
            if (id.Length == 0) continue;

            string domain = parts[2].Trim();

            pools.Add(new CognitoPoolSummary(
                id,
                parts[1].Trim(),
                domain is "-" or "" ? null : domain,
                parts[3].Trim() == "email"));
        }

        return pools;
    }

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
          ExistingPoolId: { Type: String, Default: '' }
          ExistingDomainPrefix: { Type: String, Default: '' }
        Conditions:
          Federated: !Not [ !Equals [ !Ref IdpMetadataUrl, '' ] ]
          CreatePool: !Equals [ !Ref ExistingPoolId, '' ]
          CreateDomain: !Equals [ !Ref ExistingDomainPrefix, '' ]
        Resources:
          Pool:
            Type: AWS::Cognito::UserPool
            Condition: CreatePool
            Properties:
              UserPoolName: !Sub '${Prefix}-pool'
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
            Condition: CreateDomain
            Properties:
              Domain: !Ref DomainPrefix
              UserPoolId: !If [ CreatePool, !Ref Pool, !Ref ExistingPoolId ]
          Idp:
            Type: AWS::Cognito::UserPoolIdentityProvider
            Condition: Federated
            Properties:
              UserPoolId: !If [ CreatePool, !Ref Pool, !Ref ExistingPoolId ]
              ProviderName: Workforce
              ProviderType: SAML
              ProviderDetails: { MetadataURL: !Ref IdpMetadataUrl }
              AttributeMapping: { email: email }
          # Added to the pool, never editing one that is there. A pool holds many clients, so this
          # is invisible to whatever already uses an adopted pool; changing an existing client's
          # callbacks would not be.
          Client:
            Type: AWS::Cognito::UserPoolClient
            Properties:
              ClientName: !Sub '${Prefix}-client'
              UserPoolId: !If [ CreatePool, !Ref Pool, !Ref ExistingPoolId ]
              GenerateSecret: true
              AllowedOAuthFlows: [ code ]
              AllowedOAuthScopes: [ openid, email, profile ]
              AllowedOAuthFlowsUserPoolClient: true
              CallbackURLs: [ !Ref CallbackUrl ]
              SupportedIdentityProviders: !If
                - Federated
                - [ Workforce ]
                - [ COGNITO ]
          Application:
            Type: AWS::SSO::Application
            Properties:
              Name: !Sub '${Prefix}-search'
              Description: Per-user search permissions for Connapse
              InstanceArn: !Ref InstanceArn
              ApplicationProviderArn: arn:aws:sso::aws:applicationProvider/custom
              Status: ENABLED
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
          PoolId:
            Value: !If [ CreatePool, !Ref Pool, !Ref ExistingPoolId ]
          ClientId: { Value: !Ref Client }
          ApplicationArn: { Value: !GetAtt Application.ApplicationArn }
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
        string existingPool = request.ExistingPoolId?.Trim() ?? string.Empty;
        string existingDomain = request.ExistingDomainPrefix?.Trim() ?? string.Empty;

        // JSON, not the CLI's shorthand. ActorPolicy is a document type, and shorthand cannot
        // express one: the call fails with "Shorthand syntax does not support document types"
        // before it ever reaches AWS.
        //
        // A printf format rather than a heredoc into a temp file. The file version worked on
        // Linux and failed everywhere else, because file:// takes a literal path that nothing
        // translates — and a setup script has no business caring which shell it is read in.
        // printf's %s placeholders also keep the quoting flat: no nested double quotes inside an
        // argument that is itself double-quoted.
        //
        // Built out here rather than inside the script literal because it ends in three closing
        // braces, which a raw interpolated string reads as the end of an interpolation hole.
        const string authMethodFormat =
            "{\"Iam\":{\"ActorPolicy\":{\"Version\":\"2012-10-17\",\"Statement\":"
            + "[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":\"%s\"},"
            + "\"Action\":\"sso-oauth:CreateTokenWithIAM\",\"Resource\":\"%s\"}]}}}";

        return FlattenContinuations($$"""
        # Sets up per-user AWS permissions for Connapse. Creates, in your account:
        #   a Cognito user pool, its domain and one app client
        #   an IAM Identity Center application, plus its trusted token issuer, grant,
        #     access scope and authentication method
        #   an S3 Access Grants instance, one location covering s3://, and that location's role
        #
        # It creates NO access grants. Who may read what stays yours to decide, in AWS.
        # Nothing existing is modified.

        set -e
        PREFIX='{{prefix}}'
        CALLBACK='{{request.CallbackUrl}}'
        ACTOR='{{request.ActorArn}}'
        IDP_METADATA='{{idp}}'
        EXISTING_POOL='{{existingPool}}'
        EXISTING_DOMAIN='{{existingDomain}}'
        STACK="$PREFIX-cognito"

        REGION="${AWS_REGION:-$(aws configure get region)}"
        [ -n "$REGION" ] || { echo 'No region set. Run: export AWS_REGION=us-east-1'; exit 1; }
        ACCOUNT=$(aws sts get-caller-identity --query Account --output text)

        # Checked before anything is created. A missing instance, or the right instance seen from
        # the wrong account, is the difference between this working and it half-working.
        INSTANCE=$(aws sso-admin list-instances --region "$REGION" \
                     --query 'Instances[0].InstanceArn' --output text 2>/dev/null || true)
        if [ -z "$INSTANCE" ] || [ "$INSTANCE" = 'None' ]; then
          echo "No IAM Identity Center instance is visible in $REGION from account $ACCOUNT."
          echo 'With an organization instance, run this in the management or delegated-admin account.'
          exit 1
        fi
        IDENTITY_STORE=$(aws sso-admin list-instances --region "$REGION" \
                           --query 'Instances[0].IdentityStoreId' --output text)

        # Globally unique, and stable for a given account and region so re-running is idempotent.
        # An adopted pool that already has a domain keeps it: the sign-in page belongs to the pool,
        # not to Connapse, and a second domain on it would change where its other clients send
        # people.
        if [ -n "$EXISTING_DOMAIN" ]; then
          DOMAIN_PREFIX="$EXISTING_DOMAIN"
        else
          DOMAIN_PREFIX="$PREFIX-$ACCOUNT-$REGION"
        fi

        # The template is a separate file you downloaded from Connapse and can read before
        # running any of this. Upload it here with Actions -> Upload file.
        TEMPLATE_FILE="${TEMPLATE_FILE:-connapse-cognito.yaml}"
        [ -f "$TEMPLATE_FILE" ] || {
          echo "Cannot find $TEMPLATE_FILE in $(pwd)."
          echo 'Download it from Connapse, then upload it here: Actions -> Upload file.'
          exit 1
        }


        aws cloudformation deploy --region "$REGION" \
          --stack-name "$STACK" --template-file "$TEMPLATE_FILE" \
          --capabilities CAPABILITY_NAMED_IAM \
          --parameter-overrides Prefix="$PREFIX" DomainPrefix="$DOMAIN_PREFIX" \
            CallbackUrl="$CALLBACK" InstanceArn="$INSTANCE" IdpMetadataUrl="$IDP_METADATA" \
            ExistingPoolId="$EXISTING_POOL" ExistingDomainPrefix="$EXISTING_DOMAIN"

        out() { aws cloudformation describe-stacks --region "$REGION" --stack-name "$STACK" \
                  --query "Stacks[0].Outputs[?OutputKey=='$1'].OutputValue" --output text; }
        POOL_ID=$(out PoolId); CLIENT_ID=$(out ClientId); APP_ARN=$(out ApplicationArn)

        # The stack can be "up to date" and still describe a pool that no longer exists: deleting
        # one in the console does not tell CloudFormation, and deploy reports no changes because
        # the template has not changed. Without this the next call fails with
        # ResourceNotFoundException naming a pool id that came from the stack itself, which reads
        # like a bug in the script rather than drift in the account.
        aws cognito-idp describe-user-pool --region "$REGION" --user-pool-id "$POOL_ID"           >/dev/null 2>&1 || {
          echo "Stack $STACK refers to pool $POOL_ID, which no longer exists."
          echo "Something removed it outside CloudFormation. Delete the stack and run this again:"
          echo "  aws cloudformation delete-stack --region $REGION --stack-name $STACK"
          exit 1
        }

        ISSUER="https://cognito-idp.$REGION.amazonaws.com/$POOL_ID"
        DOMAIN="https://$DOMAIN_PREFIX.auth.$REGION.amazoncognito.com"

        # CloudFormation cannot return a secret, so it is read back rather than output.
        SECRET=$(aws cognito-idp describe-user-pool-client --region "$REGION" \
                   --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" \
                   --query 'UserPoolClient.ClientSecret' --output text)

        # ---- The four calls with no CloudFormation resource type ----

        # Matched on the issuer URL rather than on the name. Identity Center will not hold two
        # issuers for one URL, and an administrator who registered this pool earlier under another
        # name would otherwise hit a duplicate they cannot see from here.
        TTI=''
        for ARN in $(aws sso-admin list-trusted-token-issuers --region "$REGION" \
                       --instance-arn "$INSTANCE" \
                       --query 'TrustedTokenIssuers[].TrustedTokenIssuerArn' --output text); do
          NAME=$(aws sso-admin describe-trusted-token-issuer --region "$REGION" \
                   --trusted-token-issuer-arn "$ARN" --query 'Name' --output text 2>/dev/null || true)
          URL=$(aws sso-admin describe-trusted-token-issuer --region "$REGION" \
                  --trusted-token-issuer-arn "$ARN" \
                  --query 'TrustedTokenIssuerConfiguration.OidcJwtConfiguration.IssuerUrl' \
                  --output text 2>/dev/null || true)
          [ "$URL" = "$ISSUER" ] && TTI="$ARN" && break

          # An issuer of ours whose pool has been deleted can never authenticate anybody, and
          # nothing else will ever remove it: it is not in the stack, so deleting the stack leaves
          # it behind. Without this, every delete-and-recreate cycle leaves one more.
          #
          # Narrowed to our own name deliberately. Matching on the URL alone would also match a
          # pool in another account, where describe-user-pool fails for want of permission rather
          # than because the pool is gone — and that would delete somebody else's working issuer.
          if [ "$NAME" = "$PREFIX" ]; then
            OLD_POOL=${URL##*/}
            aws cognito-idp describe-user-pool --region "$REGION" --user-pool-id "$OLD_POOL" \
              >/dev/null 2>&1 || {
              echo "Removing a leftover trusted token issuer for deleted pool $OLD_POOL."
              aws sso-admin delete-trusted-token-issuer --region "$REGION" \
                --trusted-token-issuer-arn "$ARN" >/dev/null 2>&1 || true
            }
          fi
        done
        if [ -z "$TTI" ] || [ "$TTI" = 'None' ]; then
          # email to emails.value: Identity Center allows the join key to be user name, email or
          # external id only, so the OIDC subject cannot be used however natural it would be.
          TTI=$(aws sso-admin create-trusted-token-issuer --region "$REGION" \
                  --instance-arn "$INSTANCE" --name "$PREFIX" \
                  --trusted-token-issuer-type OIDC_JWT \
                  --trusted-token-issuer-configuration \
                    "OidcJwtConfiguration={IssuerUrl=$ISSUER,ClaimAttributePath=email,IdentityStoreAttributePath=emails.value,JwksRetrievalOption=OPEN_ID_DISCOVERY}" \
                  --query TrustedTokenIssuerArn --output text)
        fi

        aws sso-admin put-application-grant --region "$REGION" \
          --application-arn "$APP_ARN" --grant-type urn:ietf:params:oauth:grant-type:jwt-bearer \
          --grant "JwtBearer={AuthorizedTokenIssuers=[{TrustedTokenIssuerArn=$TTI,AuthorizedAudiences=[$CLIENT_ID]}]}"

        aws sso-admin put-application-access-scope --region "$REGION" \
          --application-arn "$APP_ARN" --scope s3:access_grants:read_write

        AUTH_METHOD=$(printf '{{authMethodFormat}}' "$ACTOR" "$APP_ARN")

        aws sso-admin put-application-authentication-method --region "$REGION" \
          --application-arn "$APP_ARN" --authentication-method-type IAM \
          --authentication-method "$AUTH_METHOD"

        # Read back rather than trusted. This call and the one below are the last two steps, and
        # the whole chain fails at token exchange with a bare AccessDeniedException if either is
        # missing — an error that names neither the call nor the reason.
        aws sso-admin list-application-authentication-methods --region "$REGION" \
          --application-arn "$APP_ARN" \
          --query 'AuthenticationMethods[0].AuthenticationMethodType' --output text \
          | grep -q IAM || { echo 'The authentication method did not take. Setup is incomplete.'; exit 1; }

        # Assignment is required by default with nobody assigned, and the token exchange then fails
        # with a bare AccessDeniedException naming neither the user nor the application. It is the
        # one failure in this chain that reports nothing useful. Turning it off does not widen who
        # can read data: Access Grants still decides that, per person.
        aws sso-admin put-application-assignment-configuration --region "$REGION" \
          --application-arn "$APP_ARN" --no-assignment-required

        # One printf, not eight. An interactive shell echoes each pasted command as it runs, so
        # eight of them put a "$ printf ..." line between every value and left the block impossible
        # to select in one go. Printed as a single command, the block comes out contiguous.
        printf '\n%s\nissuerUrl=%s\ndomain=%s\nclientId=%s\nclientSecret=%s\nregion=%s\napplicationArn=%s\n%s\n\n' '{{BeginMarker}}' "$ISSUER" "$DOMAIN" "$CLIENT_ID" "$SECRET" "$REGION" "$APP_ARN" '{{EndMarker}}'
        echo "Copy the block above into Connapse."
        echo "Then, in AWS, write access grants saying who may read what. Identity store: $IDENTITY_STORE"
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
        string? secret = null, region = null, appArn = null;

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
            }
        }

        // All six or nothing. A partial block would save settings that pass IsConfigured while
        // being unable to complete a connection, which fails later and somewhere else.
        return issuer is null || domain is null || clientId is null
               || secret is null || region is null || appArn is null
            ? null
            : new CognitoSetupResult(issuer, domain, clientId, secret, region, appArn);
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
