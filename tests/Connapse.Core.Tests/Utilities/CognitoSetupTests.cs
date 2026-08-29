using Connapse.Core;
using Connapse.Core.Utilities;
using FluentAssertions;

namespace Connapse.Core.Tests.Utilities;

[Trait("Category", "Unit")]
public class CognitoSetupTests
{
    private const string Callback = "https://connapse.example.com/api/v1/auth/cloud/cognito/callback";
    private const string Actor = "arn:aws:iam::086015909943:user/connapse-reader";

    private static CognitoSetupRequest Request(string? idp = null, string? prefix = null) =>
        new(Callback, Actor, idp, prefix);

    // ── The script ───────────────────────────────────────────────────

    [Fact]
    public void GenerateScript_CarriesTheCallerSuppliedValues()
    {
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain(Callback);
        script.Should().Contain(Actor);
    }

    [Fact]
    public void GenerateScript_CreatesNoAccessGrant()
    {
        // The standing constraint of the whole feature, and this file is where it would break.
        // The instance, the location and its role are infrastructure; a grant is a permission.
        string script = CognitoSetup.GenerateScript(Request());

        // The resources moved to the template; the script must not reach around it and make one
        // by hand either.
        CognitoSetup.GenerateTemplate().Should().Contain("AWS::S3::AccessGrantsInstance");
        CognitoSetup.GenerateTemplate().Should().Contain("AWS::S3::AccessGrantsLocation");
        script.Should().NotContain("create-access-grant");
    }

    [Fact]
    public void GenerateScript_ChecksTheAccountBeforeCreatingAnything()
    {
        // Ordering, not presence. With an organization instance the sso-admin writes only work from
        // the management account, and finding that out at the last step leaves a half-built pool.
        string script = CognitoSetup.GenerateScript(Request());

        int check = script.IndexOf("list-instances", StringComparison.Ordinal);
        int create = script.IndexOf("cloudformation deploy", StringComparison.Ordinal);

        check.Should().BeGreaterThan(0);
        check.Should().BeLessThan(create);
    }

    [Fact]
    public void GenerateScript_MakesTheFourCallsCloudFormationCannot()
    {
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain("create-trusted-token-issuer");
        script.Should().Contain("put-application-grant");
        script.Should().Contain("put-application-access-scope");
        script.Should().Contain("put-application-authentication-method");
    }

    [Fact]
    public void GenerateScript_TurnsOffTheAssignmentRequirement()
    {
        // Its absence is the only failure in the chain that reports nothing an operator can act on.
        CognitoSetup.GenerateScript(Request())
            .Should().Contain("put-application-assignment-configuration");
    }

    [Fact]
    public void GenerateScript_WithNoIdentityProvider_KeepsUsersInThePool()
    {
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain("IDP_METADATA=''");
    }

    [Fact]
    public void GenerateScript_WithAnIdentityProvider_FederatesToIt()
    {
        string script = CognitoSetup.GenerateScript(
            Request(idp: "https://idp.example.com/metadata.xml"));

        script.Should().Contain("IDP_METADATA='https://idp.example.com/metadata.xml'");
        CognitoSetup.GenerateTemplate().Should().Contain("AWS::Cognito::UserPoolIdentityProvider");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GenerateScript_WithoutACallback_Throws(string? callback) =>
        FluentActions.Invoking(() => CognitoSetup.GenerateScript(new CognitoSetupRequest(callback!, Actor)))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void GenerateScript_WithoutAnActor_Throws() =>
        FluentActions.Invoking(() => CognitoSetup.GenerateScript(new CognitoSetupRequest(Callback, "")))
            .Should().Throw<ArgumentException>();

    // ── Reading the result ───────────────────────────────────────────

    private static string Block(
        string issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_abc",
        string domain = "https://connapse-1-us-east-1.auth.us-east-1.amazoncognito.com",
        string clientId = "client-id",
        string secret = "client-secret",
        string region = "us-east-1",
        string appArn = "arn:aws:sso::086015909943:application/ssoins-1/apl-1") =>
        $"""
        {CognitoSetup.BeginMarker}
        issuerUrl={issuer}
        domain={domain}
        clientId={clientId}
        clientSecret={secret}
        region={region}
        applicationArn={appArn}
        {CognitoSetup.EndMarker}
        """;

    [Fact]
    public void ParseResult_ReadsEveryField()
    {
        var result = CognitoSetup.ParseResult(Block());

        result.Should().NotBeNull();
        result!.IssuerUrl.Should().Be("https://cognito-idp.us-east-1.amazonaws.com/us-east-1_abc");
        result.ClientSecret.Should().Be("client-secret");
        result.ApplicationArn.Should().Be("arn:aws:sso::086015909943:application/ssoins-1/apl-1");
    }

    [Fact]
    public void ParseResult_TakesTheLastBlock()
    {
        // A pasted terminal buffer holds the markers twice: once in the script's own text, once in
        // its output. The first pair is the source being echoed, not the result.
        string pasted = $"printf '%s' '{CognitoSetup.BeginMarker}'\n"
                        + $"printf '%s' '{CognitoSetup.EndMarker}'\n"
                        + Block(clientId: "the-real-one");

        CognitoSetup.ParseResult(pasted)!.ClientId.Should().Be("the-real-one");
    }

    [Fact]
    public void ParseResult_WithAMissingField_IsNull()
    {
        // All six or nothing. A partial block saves settings that pass IsConfigured but cannot
        // complete a connection, which then fails later and somewhere else.
        // Filtered rather than string-replaced: the block is a raw literal, so its line
        // endings are the source file's, and an exact "\n" match silently removes nothing.
        string partial = string.Join("\n",
            Block().Split('\n').Where(l => !l.Contains("clientSecret")));

        CognitoSetup.ParseResult(partial).Should().BeNull();
    }

    [Fact]
    public void ParseResult_WithAnEmptyValue_IsNull() =>
        CognitoSetup.ParseResult(Block(secret: "")).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing like a block")]
    public void ParseResult_WithNoBlock_IsNull(string? pasted) =>
        CognitoSetup.ParseResult(pasted).Should().BeNull();

    [Fact]
    public void ParseResult_ToleratesSurroundingShellNoise() =>
        CognitoSetup.ParseResult($"$ ./setup.sh\nsome output\n{Block()}\nCopy the block above.")
            .Should().NotBeNull();

    // ── The prefix ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "connapse")]
    [InlineData("", "connapse")]
    [InlineData("  ", "connapse")]
    [InlineData("Acme Corp", "acmecorp")]
    [InlineData("ACME-corp", "acme-corp")]
    [InlineData("-acme-", "acme")]
    // A Cognito domain prefix may not start with a digit, and the script appends more to it.
    [InlineData("1acme", "connapse")]
    [InlineData("!!!", "connapse")]
    public void SanitisePrefix_CoercesToWhatAwsAccepts(string? input, string expected) =>
        CognitoSetup.SanitisePrefix(input).Should().Be(expected);

    [Fact]
    public void SanitisePrefix_TruncatesWithoutLeavingATrailingHyphen() =>
        CognitoSetup.SanitisePrefix("abcdefghijklmnopqrs-tuvwxyz")
            .Should().Be("abcdefghijklmnopqrs");

    // ── Finding pools that already exist ─────────────────────────────

    // ── Adopting one ─────────────────────────────────────────────────

    [Fact]
    public void GenerateScript_FindsTheTrustedTokenIssuerByUrlNotName()
    {
        // Identity Center will not hold two issuers for one URL, so a pool registered earlier under
        // a different name has to be found, or setup fails on a duplicate nobody can see from here.
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain("describe-trusted-token-issuer");
        script.Should().NotContain("TrustedTokenIssuers[?Name==");
    }

    [Fact]
    public void GenerateScript_SendsTheAuthenticationMethodAsJsonNotShorthand()
    {
        // Regression, found by running it. ActorPolicy is a document type, and the CLI rejects
        // shorthand for those with "Shorthand syntax does not support document types" before the
        // request reaches AWS. The script ran with set -e, so it stopped there — leaving an
        // application with a grant and a scope but no authentication method, and the assignment
        // configuration never applied.
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain("\"ActorPolicy\"");
        script.Should().NotContain("Iam={ActorPolicy=");

        // Inline, not a temp file. file:// takes a literal path that nothing translates, so the
        // heredoc version worked on Linux and failed everywhere else — and a setup script has no
        // business caring which shell reads it.
        script.Should().NotContain("file:///tmp/");
    }

    [Fact]
    public void GenerateScript_ChecksTheAuthenticationMethodTookEffect()
    {
        // Both remaining steps fail the whole chain at token exchange with a bare
        // AccessDeniedException naming neither the call nor the reason, so the script confirms
        // rather than assumes.
        CognitoSetup.GenerateScript(Request())
            .Should().Contain("list-application-authentication-methods");
    }

    // ── The template is its own artifact ─────────────────────────────

    [Fact]
    public void GenerateTemplate_IsTheCloudFormationTemplate()
    {
        string template = CognitoSetup.GenerateTemplate();

        template.Should().StartWith("AWSTemplateFormatVersion");
        template.Should().Contain("AWS::Cognito::UserPool");
        template.Should().Contain("AWS::SSO::Application");
        template.Should().Contain("AWS::S3::AccessGrantsInstance");
    }

    [Fact]
    public void GenerateScript_ReportsTheProviderItFederatedTo()
    {
        // The name has to match the template's ProviderName exactly, because Connapse passes it
        // through to /oauth2/authorize as identity_provider and Cognito rejects one it does not
        // know. Two literals in two files that must agree, so both are asserted here.
        string script = CognitoSetup.GenerateScript(
            new CognitoSetupRequest("https://x/cb", "arn:aws:iam::1:user/connapse"));

        script.Should().Contain("IDP_NAME='Workforce'");
        script.Should().Contain("identityProvider=%s");
        CognitoSetup.GenerateTemplate().Should().Contain("ProviderName: Workforce");
    }

    [Fact]
    public void ParseResult_WithAFederatedPool_CarriesTheProviderThrough()
    {
        string block = CognitoSetup.BeginMarker + "\n"
            + "issuerUrl=https://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc\n"
            + "domain=https://d.auth.us-west-1.amazoncognito.com\n"
            + "clientId=cid\nclientSecret=secret\nregion=us-west-1\n"
            + "applicationArn=arn:aws:sso::1:application/ssoins-1/apl-1\n"
            + "identityProvider=Workforce\n"
            + CognitoSetup.EndMarker;

        CognitoSetup.ParseResult(block)!.IdentityProvider.Should().Be("Workforce");
    }

    [Fact]
    public void ParseResult_WithAPoolLocalSetup_StillParsesAndNamesNoProvider()
    {
        // The script prints the key with an empty value for a pool that has its own users. Treating
        // that as a missing required field would reject a paste that is completely valid, so the
        // provider is the one optional member of the block.
        string block = CognitoSetup.BeginMarker + "\n"
            + "issuerUrl=https://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc\n"
            + "domain=https://d.auth.us-west-1.amazoncognito.com\n"
            + "clientId=cid\nclientSecret=secret\nregion=us-west-1\n"
            + "applicationArn=arn:aws:sso::1:application/ssoins-1/apl-1\n"
            + "identityProvider=\n"
            + CognitoSetup.EndMarker;

        var result = CognitoSetup.ParseResult(block);

        result.Should().NotBeNull();
        result!.IdentityProvider.Should().BeEmpty();
    }

    [Fact]
    public void ParseResult_WithABlockPredatingTheProviderField_StillParses()
    {
        // Pasted from a script generated before federation existed. It names a working pool and
        // must keep working; the only thing it cannot do is skip Cognito's own page.
        string block = CognitoSetup.BeginMarker + "\n"
            + "issuerUrl=https://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc\n"
            + "domain=https://d.auth.us-west-1.amazoncognito.com\n"
            + "clientId=cid\nclientSecret=secret\nregion=us-west-1\n"
            + "applicationArn=arn:aws:sso::1:application/ssoins-1/apl-1\n"
            + CognitoSetup.EndMarker;

        CognitoSetup.ParseResult(block).Should().NotBeNull();
    }

    [Fact]
    public void GenerateScript_MatchesTheDirectoryOnUserNameRatherThanEmail()
    {
        // A trusted token issuer resolves one claim against one of exactly three identity-store
        // attributes. Email was the original choice and is the one that cannot work for a federated
        // pool: Cognito marks a mapped address unverified and cannot verify it with a one-time
        // code, so the address proves nothing about who signed in.
        string script = CognitoSetup.GenerateScript(
            new CognitoSetupRequest("https://x/cb", "arn:aws:iam::1:user/connapse"));

        script.Should().Contain("ClaimAttributePath=preferred_username");
        script.Should().Contain("IdentityStoreAttributePath=userName");
        script.Should().NotContain("IdentityStoreAttributePath=emails.value");
    }

    [Fact]
    public void GenerateTemplate_CarriesTheDirectoryUserNameIntoTheToken()
    {
        // The two halves have to agree: the pool must put the directory user name in the claim that
        // the trusted token issuer above reads, or every sign-in resolves to nobody while both
        // sides look individually correct.
        CognitoSetup.GenerateTemplate()
            .Should().Contain("AttributeMapping: { email: email, preferred_username: userName }");
    }

    [Fact]
    public void SamlValues_AreDerivedFromAPoolThatIsNotYetFinished()
    {
        // The shape of a real deadlock. Federation is part of being configured, and the field that
        // configures it needs these two values — so deriving them from a *configured* pool meant the
        // last step of the setup waited on the setup being complete, and it could never be finished.
        //
        // They depend on the pool existing and nothing else: a domain, and an issuer to read the
        // pool id off.
        var halfway = new CognitoSettings
        {
            IssuerUrl = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_QqEHl4dCR",
            Domain = "https://connapse-1-us-west-1.auth.us-west-1.amazoncognito.com",
        };

        halfway.IsConfigured.Should().BeFalse("federation and the application ARN are still missing");

        CognitoSetup.SamlAcsUrl(halfway.Domain).Should()
            .Be("https://connapse-1-us-west-1.auth.us-west-1.amazoncognito.com/saml2/idpresponse");
        CognitoSetup.SamlAudience(halfway.IssuerUrl).Should()
            .Be("urn:amazon:cognito:sp:us-west-1_QqEHl4dCR");
    }

    [Fact]
    public void SamlValues_WithNoPool_AreNull()
    {
        // Before the first run there is nothing to tell Identity Center, and the setup page uses
        // this to keep a question with only one answer off the screen.
        CognitoSetup.SamlAcsUrl(null).Should().BeNull();
        CognitoSetup.SamlAcsUrl("  ").Should().BeNull();
        CognitoSetup.SamlAudience(null).Should().BeNull();
        CognitoSetup.SamlAudience("  ").Should().BeNull();
    }

    [Fact]
    public void SamlAcsUrl_DoesNotDoubleTheSlash()
    {
        // A domain pasted with a trailing slash is common, and Cognito matches this URL literally
        // against what the SAML application posts to.
        CognitoSetup.SamlAcsUrl("https://pool.auth.us-west-1.amazoncognito.com/").Should()
            .Be("https://pool.auth.us-west-1.amazoncognito.com/saml2/idpresponse");
    }

    [Fact]
    public void GenerateTemplate_ServesManagedLoginRatherThanTheClassicHostedUi()
    {
        // All three are needed together and none of them reports its own absence. The tier gates
        // whether managed login can be served at all, the domain version selects it, and without a
        // style record AWS refuses it to this client specifically — see the test below.
        string template = CognitoSetup.GenerateTemplate();

        template.Should().Contain("UserPoolTier: ESSENTIALS",
            "the Lite plan can only serve the classic hosted UI");
        template.Should().Contain("ManagedLoginVersion: 2",
            "version 1 is the classic hosted UI");
        template.Should().Contain("AWS::Cognito::ManagedLoginBranding");
    }

    [Fact]
    public void GenerateTemplate_TakesAwsDefaultStylingAndAppliesNoBrandingOfOurs()
    {
        // The style record exists only because AWS attaches one automatically to a console-created
        // app client and to nothing else, so a CloudFormation-created client without it silently
        // falls back to the old page. It must stay AWS's own default look: this is an AWS
        // integration, and the sign-in page is AWS's, not Connapse's.
        //
        // UseCognitoProvidedValues also requires Settings and Assets to be absent, so a later edit
        // that adds a logo or a colour would break the stack rather than quietly restyle the page.
        // Assert their absence anyway, because that is the intent rather than a side effect.
        string template = CognitoSetup.GenerateTemplate();

        template.Should().Contain("UseCognitoProvidedValues: true");
        template.Should().NotContain("Assets:", "supplying assets would mean branding of our own");
        template.Should().NotContain("Settings:", "supplying settings would mean styling of our own");
    }

    [Fact]
    public void GenerateTemplate_CreatesNoAccessGrant()
    {
        // The constraint moved here with the resources. Instance, location and role are
        // infrastructure; a grant is a permission, and those stay the administrator's.
        CognitoSetup.GenerateTemplate().Should().NotContain("AWS::S3::AccessGrant\n");
    }

    [Fact]
    public void GenerateScript_DoesNotCarryTheTemplate()
    {
        // Two things at once. The template is the reviewable artifact and belongs in a file an
        // administrator reads, not buried in a shell script; and a hundred-line heredoc is what
        // put CloudShell into continuation mode until it disconnected, twice.
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().NotContain("AWSTemplateFormatVersion");
        script.Should().NotContain("<<");
    }

    [Fact]
    public void GenerateScript_SaysWhereToGetTheTemplateWhenItIsMissing()
    {
        // The one new way this can fail. Without the file the deploy fails inside the AWS CLI with
        // a message about a path, which says nothing about the step that was skipped.
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain("connapse-cognito.yaml");
        script.Should().Contain("Upload file");
    }

    [Fact]
    public void GenerateScript_PutsTheShellIntoNoContinuation()
    {
        // What the echo interleaving came down to. A trailing backslash makes an interactive shell
        // print a secondary prompt and buffer, and a long run of those is what disconnected
        // CloudShell in the heredoc version and still echoed out of order at 148 lines. The source
        // keeps its continuations for readability; they are flattened on the way out, so the text
        // shown and the text pasted are the same either way.
        string script = CognitoSetup.GenerateScript(Request());

        script.Split('\n').Should().NotContain(l => l.EndsWith('\\'));
    }

    [Fact]
    public void GenerateScript_PrintsTheSettingsBlockAsOneCommand()
    {
        // An interactive shell echoes each pasted command as it runs, so eight printfs put a
        // "$ printf ..." line between every value and left the block impossible to select in one
        // go. One command means one echo and a contiguous block.
        string script = CognitoSetup.GenerateScript(Request());

        string[] outputLines = script.Split('\n')
            .Where(l => l.TrimStart().StartsWith("printf", StringComparison.Ordinal))
            .ToArray();

        outputLines.Should().ContainSingle();
        outputLines[0].Should().Contain("issuerUrl=%s").And.Contain("applicationArn=%s");
    }

    [Fact]
    public void GenerateScript_RemovesItsOwnLeftoverIssuers()
    {
        // Not in the stack, so deleting the stack leaves it behind, and every recreate cycle adds
        // another. Narrowed to our own name so it cannot delete an issuer belonging to a pool in
        // another account, where the existence check fails for want of permission.
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().Contain("Removing a leftover trusted token issuer");
        script.Should().Contain("[ \"$NAME\" = \"$PREFIX\" ]");
    }

    [Fact]
    public void GenerateScript_KeepsItsComments()
    {
        // Flattening joins commands, not commentary. The comments are why an administrator can
        // read this before running it, which is the whole basis for asking them to.
        CognitoSetup.GenerateScript(Request())
            .Should().Contain("# It creates NO access grants.");
    }

    [Fact]
    public void GenerateScript_EncodesNothing()
    {
        // Deliberate, and worth a test because the alternative was shipped briefly. Piping base64
        // into a shell is a documented malware signature — Google Cloud raises a threat finding on
        // it — and it hides from the reader exactly what a setup script must show them.
        string script = CognitoSetup.GenerateScript(Request());

        script.Should().NotContain("base64");
    }

    [Fact]
    public void GenerateScript_ChecksTheStackStillOwnsItsPool()
    {
        // Deleting a pool in the console does not tell CloudFormation, so deploy reports no
        // changes and hands back a pool id for something that is gone. Without this the next call
        // fails with ResourceNotFoundException naming an id that came from the stack itself.
        CognitoSetup.GenerateScript(Request())
            .Should().Contain("no longer exists");
    }
}
