namespace Connapse.Core.Utilities;

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
    /// <summary>Prefix for every created resource, so they are obvious and findable.</summary>
    /// <remarks>
    /// A constant rather than a parameter. It was configurable, and nothing ever configured it —
    /// the page had no field for it, so the only value it ever held was this one.
    /// </remarks>
    public const string NamePrefix = "connapse";

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
                  # Pass the Identity Center context to S3.
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
          # Register the S3 root without granting access to it.
          Location:
            Type: AWS::S3::AccessGrantsLocation
            DependsOn: GrantsInstance
            Properties:
              LocationScope: 's3://'
              IamRoleArn: !GetAtt LocationRole.Arn
        """;

    /// <summary>The command to paste into AWS CloudShell.</summary>
    /// <param name="region">
    /// Where the Identity Center instance was found, which is where everything else must be
    /// created.
    /// </param>
    /// <remarks>
    /// Ordered so that nothing is created before the account has been checked. With an organization
    /// instance the Identity Center read only works from the management or delegated-admin account,
    /// and a script that discovers that at the last step has already left an Access Grants location
    /// behind that nobody asked for.
    /// </remarks>
    public static string GenerateScript(string? region)
    {
        string pinnedRegion = SanitiseRegion(region);

        return FlattenContinuations($$"""
        # Creates the Access Grants instance, s3:// location, and location role.
        # Does not create access grants.
        FAILED=""
        PREFIX='{{NamePrefix}}'
        STACK="$PREFIX-permissions"

        # Use the Identity Center region rather than CloudShell's current region.
        REGION="{{pinnedRegion}}"
        [ -n "$REGION" ] || { echo 'No region. Locate your Identity Center instance first.'; FAILED=1; }
        ACCOUNT=$(aws sts get-caller-identity --query Account --output text)

        # Verify the instance before creating resources.
        INSTANCE=$(aws sso-admin list-instances --region "$REGION" \
                     --query 'Instances[0].InstanceArn' --output text 2>/dev/null || true)
        if [ -z "$INSTANCE" ] || [ "$INSTANCE" = 'None' ]; then
          echo "No IAM Identity Center instance is visible in $REGION from account $ACCOUNT."
          echo 'With an organization instance, run this in the management or delegated-admin account.'
          FAILED=1
        fi

        # Deploy the template downloaded from Connapse.
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
    /// Returns <paramref name="region"/> if it looks like an AWS region name, and an empty string
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// An allowlist rather than an escape, because the set of legal region names is small and
    /// regular — lowercase letters, digits and hyphens — and anything outside it is not a region
    /// this script could use anyway.
    /// <para>
    /// The value reaches here from a block the administrator pasted, and is interpolated into a
    /// double-quoted assignment. A stray quote in it would end that string early and leave the rest
    /// of the script parsing as something else entirely — the same class of defect as the trailing
    /// quote that once made a paste hang with no error. Rejecting rather than escaping means the
    /// failure lands on the script's existing "no region" branch, which already says what to do.
    /// </para>
    /// </remarks>
    public static string SanitiseRegion(string? region)
    {
        string trimmed = region?.Trim() ?? string.Empty;

        // Bounded so a pasted paragraph cannot arrive here as one very long "region".
        if (trimmed.Length is 0 or > 32)
            return string.Empty;

        return trimmed.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            ? trimmed
            : string.Empty;
    }

    /// <summary>
    /// Folds the script's line continuations away and forces its line endings to LF.
    /// </summary>
    /// <remarks>
    /// Written with continuations because the source is read by people, and emitted without them
    /// because an interactive shell in continuation mode is what CloudShell disconnected during.
    /// <para>
    /// Newlines are normalised because a raw string literal inherits the line endings of the C#
    /// file it is written in, and on Windows that is CRLF. Pasting into a terminal survives that;
    /// saving the script and running it does not, because a real Linux bash reads the carriage
    /// return as part of the token and `fi` becomes a word it has never heard of. Git Bash
    /// tolerates it, which is exactly why this went unnoticed until a lint ran under WSL.
    /// </para>
    /// </remarks>
    private static string FlattenContinuations(string script) =>
        script.Replace("\r\n", "\n").Replace(" \\\n", " ");
}
