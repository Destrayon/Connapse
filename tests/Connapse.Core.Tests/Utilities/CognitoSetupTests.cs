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

    [Fact]
    public void GenerateDiscoveryScript_OnlyReads()
    {
        // An administrator is being asked to run this before they have decided anything, so it
        // must not be able to change their account.
        string script = CognitoSetup.GenerateDiscoveryScript();

        script.Should().Contain("list-user-pools");
        script.Should().Contain("describe-user-pool");
        script.Should().NotContain("create-");
        script.Should().NotContain("put-");
        script.Should().NotContain("delete-");
    }

    private static string PoolBlock(params string[] rows) =>
        CognitoSetup.PoolsBeginMarker + "\n"
        + string.Join("\n", rows) + "\n"
        + CognitoSetup.PoolsEndMarker;

    [Fact]
    public void ParsePools_ReadsEachPool()
    {
        var pools = CognitoSetup.ParsePools(PoolBlock(
            "pool=us-east-1_aaa\tWorkforce\tacme-login\temail",
            "pool=us-east-1_bbb\tCustomers\t-\t-"));

        pools.Should().HaveCount(2);

        pools[0].PoolId.Should().Be("us-east-1_aaa");
        pools[0].Name.Should().Be("Workforce");
        pools[0].DomainPrefix.Should().Be("acme-login");
        pools[0].VerifiesEmail.Should().BeTrue();
        pools[0].IsUsable.Should().BeTrue();

        pools[1].DomainPrefix.Should().BeNull();
        pools[1].VerifiesEmail.Should().BeFalse();
    }

    [Fact]
    public void ParsePools_WithoutAVerifiedEmail_IsNotUsable()
    {
        // The trusted token issuer joins to an Identity Center user on the email claim. A pool that
        // does not verify email cannot make that join, and saying so here beats finding out after
        // the whole chain is wired up.
        var pools = CognitoSetup.ParsePools(PoolBlock("pool=us-east-1_bbb\tCustomers\t-\t-"));

        pools.Single().IsUsable.Should().BeFalse();
    }

    [Fact]
    public void GenerateDiscoveryScript_SeparatesFieldsWithAPipe()
    {
        // A terminal copies what it drew, and CloudShell draws a tab as a run of spaces. The
        // tab-separated row therefore arrived as one field, every pool was dropped, and the page
        // said the account had none.
        string script = CognitoSetup.GenerateDiscoveryScript();

        script.Should().Contain("pool=%s|%s|%s|%s");
        script.Should().NotContain(@"pool=%s\t");
    }

    [Fact]
    public void ParsePools_ReadsAPipeSeparatedRow()
    {
        var pools = CognitoSetup.ParsePools(
            CognitoSetup.PoolsBeginMarker + "\n"
            + "pool=us-west-1_brg6XHhA9|connapse-pool|connapse-086015909943-us-west-1|email\n"
            + CognitoSetup.PoolsEndMarker);

        pools.Should().ContainSingle();
        pools[0].PoolId.Should().Be("us-west-1_brg6XHhA9");
        pools[0].Name.Should().Be("connapse-pool");
        pools[0].DomainPrefix.Should().Be("connapse-086015909943-us-west-1");
        pools[0].IsUsable.Should().BeTrue();
    }

    [Fact]
    public void ParsePools_StillReadsATabSeparatedRow()
    {
        // A block copied before the emitter changed should not silently become "no pools found",
        // which is the one answer this must never give wrongly.
        var pools = CognitoSetup.ParsePools(
            CognitoSetup.PoolsBeginMarker + "\n"
            + "pool=us-west-1_aaa\tWorkforce\tacme-login\temail\n"
            + CognitoSetup.PoolsEndMarker);

        pools.Should().ContainSingle().Which.Name.Should().Be("Workforce");
    }

    [Fact]
    public void ParsePools_WithAPoolNameContainingSpaces_KeepsItWhole()
    {
        // Why the separator is a pipe and not "a run of spaces". AWS allows whitespace in a pool
        // name, so splitting on it would cut the name in half and shift every later field.
        var pools = CognitoSetup.ParsePools(
            CognitoSetup.PoolsBeginMarker + "\n"
            + "pool=us-west-1_aaa|Acme Staff Directory|acme-login|email\n"
            + CognitoSetup.PoolsEndMarker);

        pools.Should().ContainSingle().Which.Name.Should().Be("Acme Staff Directory");
        pools[0].DomainPrefix.Should().Be("acme-login");
    }

    [Fact]
    public void ParsePools_TakesTheLastBlock()
    {
        string pasted = PoolBlock("pool=us-east-1_old\tEchoed\t-\temail")
                        + "\n" + PoolBlock("pool=us-east-1_new\tReal\t-\temail");

        CognitoSetup.ParsePools(pasted).Single().Name.Should().Be("Real");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no block here")]
    public void ParsePools_WithNoBlock_IsEmpty(string? pasted) =>
        CognitoSetup.ParsePools(pasted).Should().BeEmpty();

    [Fact]
    public void ParsePools_SkipsMalformedRows() =>
        CognitoSetup.ParsePools(PoolBlock(
            "pool=us-east-1_aaa\tGood\t-\temail",
            "pool=",
            "pool=us-east-1_ccc\ttoo few columns",
            "not a pool line"))
            .Should().ContainSingle().Which.PoolId.Should().Be("us-east-1_aaa");

    // ── Adopting one ─────────────────────────────────────────────────

    [Fact]
    public void GenerateScript_AdoptingAPool_DoesNotCreateOne()
    {
        string script = CognitoSetup.GenerateScript(
            new CognitoSetupRequest(Callback, Actor, ExistingPoolId: "us-east-1_aaa"));

        script.Should().Contain("EXISTING_POOL='us-east-1_aaa'");

        // The pool resource is conditional, and the condition is false when a pool is named.
        CognitoSetup.GenerateTemplate().Should().Contain("Condition: CreatePool");
        CognitoSetup.GenerateTemplate()
            .Should().Contain("CreatePool: !Equals [ !Ref ExistingPoolId, '' ]");
    }

    [Fact]
    public void GenerateScript_AdoptingAPool_AddsAClientRatherThanEditingOne()
    {
        // A pool holds many app clients. Changing an existing client's callback URLs would break
        // whatever already signs in through it, which is the one thing adoption must not do.
        string script = CognitoSetup.GenerateScript(
            new CognitoSetupRequest(Callback, Actor, ExistingPoolId: "us-east-1_aaa"));

        CognitoSetup.GenerateTemplate().Should().Contain("AWS::Cognito::UserPoolClient");
        script.Should().NotContain("update-user-pool-client");
    }

    [Fact]
    public void GenerateScript_AdoptingAPoolWithADomain_KeepsIt()
    {
        // The sign-in page belongs to the pool, not to Connapse. A second domain would change
        // where that pool's other clients send people.
        string script = CognitoSetup.GenerateScript(new CognitoSetupRequest(
            Callback, Actor, ExistingPoolId: "us-east-1_aaa", ExistingDomainPrefix: "acme-login"));

        script.Should().Contain("EXISTING_DOMAIN='acme-login'");
        CognitoSetup.GenerateTemplate()
            .Should().Contain("CreateDomain: !Equals [ !Ref ExistingDomainPrefix, '' ]");
    }

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
    public void GenerateDiscoveryScript_AsksForEachFactSeparately()
    {
        // Regression. Asking for both in one --query puts them on separate lines, because
        // AutoVerifiedAttributes is a list and --output text gives a list its own row. Reading them
        // positionally then landed the attribute inside the domain and produced a row with three
        // fields instead of four, which ParsePools drops — so a perfectly good pool came back as
        // "no pools found".
        string script = CognitoSetup.GenerateDiscoveryScript();

        script.Should().Contain("--query 'UserPool.Domain'");
        script.Should().Contain("--query 'UserPool.AutoVerifiedAttributes'");
        script.Should().NotContain("UserPool.[Domain,AutoVerifiedAttributes]");
    }

    [Fact]
    public void GenerateDiscoveryScript_PrintsTheBlockInOnePiece()
    {
        // An interactive shell echoes a pasted multi-line command, so printing the markers as it
        // goes puts that echo between them. Collected first, printed after — the same shape
        // AwsIamUserSetup uses.
        CognitoSetup.GenerateDiscoveryScript().Should().Contain("BLOCK=$(");
    }

    [Fact]
    public void ParsePools_IgnoresTheShellEchoingTheCommandIntoTheBlock()
    {
        // Taken from a real CloudShell paste: the prompt echoes the pipeline source between the
        // markers, and one of those lines contains the literal text "pool=".
        string pasted = CognitoSetup.PoolsBeginMarker + "\n"
            + "~ $ aws cognito-idp list-user-pools --region \"$REGION\" --max-results 60 \\\n"
            + ">   --query 'UserPools[].[Id,Name]' --output text | while read -r ID NAME; do\n"
            + ">   printf 'pool=%s\\t%s\\t%s\\t%s\\n' \"$ID\" \"$NAME\" \\\n"
            + "> done\n"
            + "pool=us-west-1_faPljPr3c\tconnapse-pool\tconnapse-086015909943-us-west-1\temail\n"
            + CognitoSetup.PoolsEndMarker;

        var pools = CognitoSetup.ParsePools(pasted);

        pools.Should().ContainSingle();
        pools[0].PoolId.Should().Be("us-west-1_faPljPr3c");
        pools[0].DomainPrefix.Should().Be("connapse-086015909943-us-west-1");
        pools[0].IsUsable.Should().BeTrue();
    }

    [Fact]
    public void ParsePools_WithAThreeFieldRow_FindsNothingRatherThanGuessing()
    {
        // What the broken script produced. Dropping the row is the right failure: the fourth field
        // decides whether the pool can be used at all, and defaulting it either way would either
        // hide a usable pool or offer one that cannot work.
        string pasted = CognitoSetup.PoolsBeginMarker + "\n"
            + "pool=us-west-1_faPljPr3c\tconnapse-pool\tconnapse-086015909943-us-west-1\n"
            + "email\t-\n"
            + CognitoSetup.PoolsEndMarker;

        CognitoSetup.ParsePools(pasted).Should().BeEmpty();
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
