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
public record CognitoSetupRequest(
    string CallbackUrl,
    string ActorArn,
    string? IdpMetadataUrl = null,
    string? NamePrefix = null);

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
/// Two artifacts in one script because the AWS surface is split. A CloudFormation stack covers the
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

        // Built out here rather than inline below. The CLI's shorthand for this one argument ends
        // in two closing braces, and a raw interpolated string reads those as the end of an
        // interpolation hole. $ACTOR and $APP_ARN are shell variables, not C# ones.
        const string authMethod =
            "Iam={ActorPolicy={Version=2012-10-17,Statement=[{Effect=Allow,"
            + "Principal={AWS=$ACTOR},Action=sso-oauth:CreateTokenWithIAM,Resource=$APP_ARN}]}}";

        return $$"""
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
        DOMAIN_PREFIX="$PREFIX-$ACCOUNT-$REGION"

        cat > /tmp/$STACK.yaml <<'TEMPLATE'
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
          Idp:
            Type: AWS::Cognito::UserPoolIdentityProvider
            Condition: Federated
            Properties:
              UserPoolId: !Ref Pool
              ProviderName: Workforce
              ProviderType: SAML
              ProviderDetails: { MetadataURL: !Ref IdpMetadataUrl }
              AttributeMapping: { email: email }
          Client:
            Type: AWS::Cognito::UserPoolClient
            DependsOn: Domain
            Properties:
              ClientName: !Sub '${Prefix}-client'
              UserPoolId: !Ref Pool
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
          PoolId: { Value: !Ref Pool }
          ClientId: { Value: !Ref Client }
          ApplicationArn: { Value: !GetAtt Application.ApplicationArn }
        TEMPLATE

        aws cloudformation deploy --region "$REGION" \
          --stack-name "$STACK" --template-file /tmp/$STACK.yaml \
          --capabilities CAPABILITY_NAMED_IAM \
          --parameter-overrides Prefix="$PREFIX" DomainPrefix="$DOMAIN_PREFIX" \
            CallbackUrl="$CALLBACK" InstanceArn="$INSTANCE" IdpMetadataUrl="$IDP_METADATA"

        out() { aws cloudformation describe-stacks --region "$REGION" --stack-name "$STACK" \
                  --query "Stacks[0].Outputs[?OutputKey=='$1'].OutputValue" --output text; }
        POOL_ID=$(out PoolId); CLIENT_ID=$(out ClientId); APP_ARN=$(out ApplicationArn)

        ISSUER="https://cognito-idp.$REGION.amazonaws.com/$POOL_ID"
        DOMAIN="https://$DOMAIN_PREFIX.auth.$REGION.amazoncognito.com"

        # CloudFormation cannot return a secret, so it is read back rather than output.
        SECRET=$(aws cognito-idp describe-user-pool-client --region "$REGION" \
                   --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" \
                   --query 'UserPoolClient.ClientSecret' --output text)

        # ---- The four calls with no CloudFormation resource type ----

        TTI=$(aws sso-admin list-trusted-token-issuers --region "$REGION" \
                --instance-arn "$INSTANCE" \
                --query "TrustedTokenIssuers[?Name=='$PREFIX'].TrustedTokenIssuerArn" --output text)
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

        aws sso-admin put-application-authentication-method --region "$REGION" \
          --application-arn "$APP_ARN" --authentication-method-type IAM \
          --authentication-method '{{authMethod}}'

        # Assignment is required by default with nobody assigned, and the token exchange then fails
        # with a bare AccessDeniedException naming neither the user nor the application. It is the
        # one failure in this chain that reports nothing useful. Turning it off does not widen who
        # can read data: Access Grants still decides that, per person.
        aws sso-admin put-application-assignment-configuration --region "$REGION" \
          --application-arn "$APP_ARN" --no-assignment-required

        printf '\n%s\n' '{{BeginMarker}}'
        printf 'issuerUrl=%s\n' "$ISSUER"
        printf 'domain=%s\n' "$DOMAIN"
        printf 'clientId=%s\n' "$CLIENT_ID"
        printf 'clientSecret=%s\n' "$SECRET"
        printf 'region=%s\n' "$REGION"
        printf 'applicationArn=%s\n' "$APP_ARN"
        printf '%s\n\n' '{{EndMarker}}'
        echo "Copy the block above into Connapse."
        echo "Then, in AWS, write access grants saying who may read what. Identity store: $IDENTITY_STORE"
        """;
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
