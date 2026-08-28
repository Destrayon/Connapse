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

        script.Should().Contain("AWS::S3::AccessGrantsInstance");
        script.Should().Contain("AWS::S3::AccessGrantsLocation");
        script.Should().NotContain("AWS::S3::AccessGrant\n");
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
        script.Should().Contain("AWS::Cognito::UserPoolIdentityProvider");
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
        string partial = Block().Replace("clientSecret=client-secret\n", "");

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
}
