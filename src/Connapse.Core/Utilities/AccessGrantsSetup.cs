namespace Connapse.Core.Utilities;

/// <summary>What the setup script needs to know before it can be written.</summary>
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
public record AccessGrantsSetupRequest(string? NamePrefix = null, string? Region = null);

/// <summary>
/// Builds the command that creates the S3 Access Grants side of per-user permissions.
/// </summary>
/// <remarks>
/// The same shape as <see cref="AwsIamUserSetup"/>, and for the same reasons: shown in full so an
/// administrator can read what it creates before running it, and run in CloudShell where their
/// credentials already are, so none are ever pasted into Connapse.
/// <para>
/// A CloudFormation template rather than a shell script, because only the first is reviewable by
/// reading it: a template declares what will exist, and CloudFormation lists it and asks for
/// acknowledgement before creating IAM resources.
/// </para>
/// <para>
/// Nothing is read back. This used to provision a Cognito user pool and print a block of settings
/// to paste into Connapse; sign-in now goes straight to IAM Identity Center, so the only values
/// that travel back are the ones an administrator copies out of the SAML application, and those
/// come from the console rather than from here.
/// </para>
/// <para>
/// <b>It creates no grant.</b> The instance, its location and that location's role are
/// infrastructure and are created here. A grant names who may read what, and that is the
/// administrator's decision, authored in AWS, which Connapse reads and never writes. This is the
/// standing constraint of the whole feature and this file is the most tempting place to break it.
/// </para>
/// </remarks>
public static class AccessGrantsSetup
{
    /// <summary>Default prefix for created resources.</summary>
    public const string DefaultNamePrefix = "connapse";

    /// <summary>The permissions the script itself needs, for the page to state up front.</summary>
    /// <remarks>
    /// These are the administrator's own permissions, used once. Connapse never holds any of them —
    /// its own policy stays read-only.
    /// </remarks>
    public static readonly IReadOnlyList<string> RequiredPermissions =
    [
        "cloudformation:*", "sso:ListInstances",
        "s3:CreateAccessGrantsInstance", "s3:CreateAccessGrantsLocation",
        "iam:CreateRole", "iam:PutRolePolicy", "iam:PassRole"
    ];

    /// <summary>The CloudFormation template, for the administrator to read and upload.</summary>
    /// <remarks>
    /// A separate artifact rather than a heredoc inside the script. It is the reviewable part, and
    /// it is also what made an earlier version of this script unpasteable: a hundred lines of
    /// heredoc puts an interactive shell into continuation mode for the whole block, and CloudShell
    /// disconnected part-way through it twice. What is left is plain commands, one per line.
    /// </remarks>
    public static string GenerateTemplate() =>
        """
        AWSTemplateFormatVersion: '2010-09-09'
        Parameters:
          Prefix: { Type: String }
          InstanceArn: { Type: String }
        Resources:
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
          # Registers s3:// so grants may be written anywhere. Registering a location is not
          # granting access to it; it is declaring which data Access Grants may govern.
          Location:
            Type: AWS::S3::AccessGrantsLocation
            DependsOn: GrantsInstance
            Properties:
              LocationScope: 's3://'
              IamRoleArn: !GetAtt LocationRole.Arn
        """;

    /// <summary>The command to paste into AWS CloudShell.</summary>
    /// <remarks>
    /// Ordered so that nothing is created before the account has been checked. With an organization
    /// instance the Identity Center read only works from the management or delegated-admin account,
    /// and a script that discovers that at the last step has already left an Access Grants location
    /// behind that nobody asked for.
    /// </remarks>
    public static string GenerateScript(AccessGrantsSetupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string prefix = SanitisePrefix(request.NamePrefix);
        string region = request.Region?.Trim() ?? string.Empty;

        return FlattenContinuations($$"""
        # Sets up the S3 Access Grants side of per-user permissions for Connapse. Creates, in your
        # account: an S3 Access Grants instance, one location covering s3://, and that location's
        # role.
        #
        # It creates NO access grants. Who may read what stays yours to decide, in AWS.
        # Nothing existing is modified.

        # Set by any check below that fails, so a half-finished run says so rather than looking
        # like a clean one.
        FAILED=""
        PREFIX='{{prefix}}'
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

        # The template is a separate file you downloaded from Connapse and can read before
        # running any of this. Upload it here with Actions -> Upload file.
        TEMPLATE_FILE="${TEMPLATE_FILE:-connapse-permissions.yaml}"
        [ -f "$TEMPLATE_FILE" ] || {
          echo "Cannot find $TEMPLATE_FILE in $(pwd)."
          echo 'Download it from Connapse, then upload it here: Actions -> Upload file.'
          FAILED=1
        }

        if [ -z "$FAILED" ]; then
          aws cloudformation deploy --region "$REGION" \
            --stack-name "$STACK" --template-file "$TEMPLATE_FILE" \
            --capabilities CAPABILITY_NAMED_IAM \
            --parameter-overrides Prefix="$PREFIX" InstanceArn="$INSTANCE" || {
            echo 'The stack did not deploy. To see why:'
            echo "  aws cloudformation describe-stack-events --region $REGION --stack-name $STACK \\"
            echo "    --query \"StackEvents[?ResourceStatusReason!=null].[LogicalResourceId,ResourceStatusReason]\" --output text"
            FAILED=1
          }
        fi

        if [ -z "$FAILED" ]; then
          echo
          echo 'Done. S3 Access Grants is set up in' "$REGION."
          echo 'Now create the sign-in application in the Identity Center console, using the two'
          echo 'values Connapse shows you, and paste its metadata back into Connapse.'
        else
          echo
          echo 'Something above failed. Nothing was recorded in Connapse; fix it and run this again.'
        fi
        """);
    }

    /// <summary>
    /// Folds the script's line continuations away, so it pastes as plain one-line commands.
    /// </summary>
    /// <remarks>
    /// Written with continuations because the source is read by people, and pasted without them
    /// because an interactive shell in continuation mode is what CloudShell disconnected during.
    /// </remarks>
    private static string FlattenContinuations(string script) =>
        script.Replace(" \\\n", " ").Replace(" \\\r\n", " ");

    /// <summary>Keeps a prefix to what AWS resource names allow.</summary>
    public static string SanitisePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return DefaultNamePrefix;

        string cleaned = new(prefix.Trim().ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());

        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? DefaultNamePrefix : cleaned[..Math.Min(cleaned.Length, 32)];
    }
}
