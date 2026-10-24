using System.Text.Json;
using System.Text.Json.Nodes;
using Connapse.Core;
using CsCheck;
using FluentAssertions;

namespace Connapse.Core.Tests.Sources;

/// <summary>
/// Proof tests for #859: every provider's scope is typed, a typed scope survives being stored and sent,
/// and rows stored before the type existed load as they are, with no migration.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Size", "Small")]
public class SourceScopeTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web);

    /// <summary>Rows in the shapes the dialogs, the API and earlier versions stored, and what each one means.</summary>
    public static TheoryData<ConnectionProvider, string, SourceScope> LegacyRows => new()
    {
        { ConnectionProvider.S3, """{"bucketName":"docs","prefix":"handbook/"}""", new S3BucketScope("docs", "handbook/") },
        { ConnectionProvider.S3, """{"bucketName":"docs"}""", new S3BucketScope("docs") },
        { ConnectionProvider.AzureBlob, """{"containerName":"docs","prefix":"a/"}""", new AzureBlobContainerScope("docs", "a/") },
        { ConnectionProvider.Filesystem, "{}", new FilesystemFolderScope() },
        { ConnectionProvider.Filesystem, """{"subPath":"notes","includePatterns":["*.md"],"excludePatterns":["drafts/**"]}""",
            new FilesystemFolderScope("notes", ["*.md"], ["drafts/**"]) },
        { ConnectionProvider.Sftp, """{"subPath":"exports","includePatterns":["*.pdf",3]}""", new SftpFolderScope("exports", ["*.pdf"]) },
        { ConnectionProvider.GitHub, """{"owner":"acme","repo":"handbook"}""", new GitHubRepositoryScope("acme", "handbook", GitHubContentKind.Docs) },
        { ConnectionProvider.GitHub, """{"owner":"acme","repo":"handbook","kind":"Docs","repoId":123,"includePatterns":["docs/**"],"host":"github.com"}""",
            new GitHubRepositoryScope("acme", "handbook", GitHubContentKind.Docs, RepoId: 123, IncludePatterns: ["docs/**"]) },
        { ConnectionProvider.GitHub,
            """{"owner":"acme","repo":"api","kind":"issuesandpullrequests","repoId":5,"private":true,"includeComments":false,"includeCommentAuthors":["dependabot[bot]"],"excludeCommentAuthors":["octo"]}""",
            new GitHubRepositoryScope("acme", "api", GitHubContentKind.IssuesAndPullRequests, RepoId: 5, Private: true,
                IncludeComments: false, IncludeCommentAuthors: ["dependabot[bot]"], ExcludeCommentAuthors: ["octo"]) },
        { ConnectionProvider.Atlassian,
            """{"kind":"jira-project","projectId":"10000","projectKey":"ENG","projectName":"Engineering","includeAttachments":true,"maxAttachmentMb":25}""",
            new JiraProjectScope("10000", "ENG", "Engineering", true, 25) },
        { ConnectionProvider.Atlassian, """{"kind":"jira-project","projectId":10000,"projectKey":"ENG"}""", new JiraProjectScope("10000", "ENG") },
        { ConnectionProvider.Atlassian,
            """{"kind":"confluence-space","spaceId":"98306","spaceKey":"DOCS","spaceName":"Docs","includeAttachments":false,"maxAttachmentMb":10}""",
            new ConfluenceSpaceScope("98306", "DOCS", "Docs", false, 10) },
        { ConnectionProvider.Custom, """{"baseUrl":"https://cs.example.com","scopeId":"team-a"}""", new CustomSourceScope("https://cs.example.com", "team-a") },
        { ConnectionProvider.S3, """{"bucketName":"docs","legacyFlag":true,"notes":{"by":"admin"}}""", new S3BucketScope("docs") },
    };

    [Theory]
    [MemberData(nameof(LegacyRows))]
    public void LegacyScopeRows_LoadUnchanged(ConnectionProvider provider, string stored, SourceScope meaning)
    {
        SourceScope? read = SourceScopeCodec.Read(provider, stored);

        read.Should().BeEquivalentTo(meaning, o => o.PreferringRuntimeMemberTypes(), "a stored row means what the connectors read from it");
        JsonNode.DeepEquals(JsonNode.Parse(SourceScopeCodec.Write(read!, stored)), JsonNode.Parse(stored))
            .Should().BeTrue("writing an unchanged scope back leaves the stored row exactly as it was, unmodeled keys included");
    }

    [Theory]
    [InlineData(ConnectionProvider.S3, "not json")]
    [InlineData(ConnectionProvider.S3, "[]")]
    [InlineData(ConnectionProvider.S3, """{"prefix":"a/"}""")]
    [InlineData(ConnectionProvider.AzureBlob, """{"containerName":7}""")]
    [InlineData(ConnectionProvider.GitHub, """{"owner":"acme"}""")]
    [InlineData(ConnectionProvider.GitHub, """{"owner":"acme","repo":"r","kind":"Wiki"}""")]
    [InlineData(ConnectionProvider.Atlassian, """{"kind":"jira-project"}""")]
    [InlineData(ConnectionProvider.Atlassian, """{"kind":"trello-board","boardId":"1"}""")]
    [InlineData(ConnectionProvider.Custom, """{"baseUrl":"https://cs.example.com"}""")]
    public void Read_RowNotNamingAScope_IsNullRatherThanThrowing(ConnectionProvider provider, string stored)
    {
        SourceScopeCodec.Read(provider, stored).Should().BeNull();
    }

    [Fact]
    public void SourceScope_RoundTripsForEveryProvider()
    {
        AnyScope.Sample(scope =>
        {
            ConnectionProvider provider = SourceScopeCodec.ProviderOf(scope);

            SourceScopeCodec.Read(provider, SourceScopeCodec.Write(scope))
                .Should().BeEquivalentTo(scope, o => o.PreferringRuntimeMemberTypes(), "a scope reads back as written");

            string sent = JsonSerializer.Serialize<SourceScope>(scope, Api);
            JsonSerializer.Deserialize<SourceScope>(sent, Api)
                .Should().BeEquivalentTo(scope, o => o.PreferringRuntimeMemberTypes(), "a scope sent over the API arrives as sent");
        });
    }

    [Fact]
    public void SourceScope_EveryKindIsGenerated()
    {
        // Guards the property test above against covering only some kinds.
        var kinds = typeof(SourceScope).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SourceScope))).ToHashSet();
        var generated = new HashSet<Type>();
        AnyScope.Sample(scope => { generated.Add(scope.GetType()); }, iter: 2000);

        generated.Should().BeEquivalentTo(kinds);
    }

    private static readonly Gen<string> Text = Gen.String[Gen.Char.AlphaNumeric, 1, 12];
    private static readonly Gen<string?> MaybeText = Gen.Frequency((1, Gen.Const((string?)null)), (3, Text.Select(s => (string?)s)));
    private static readonly Gen<string> Digits = Gen.Long[1, long.MaxValue].Select(l => l.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static readonly Gen<IReadOnlyList<string>?> MaybeList = Gen.Frequency(
        (1, Gen.Const((IReadOnlyList<string>?)null)),
        (3, Text.Array[0, 4].Select(a => (IReadOnlyList<string>?)a)));
    private static readonly Gen<bool?> MaybeBool = Gen.Frequency((1, Gen.Const((bool?)null)), (2, Gen.Bool.Select(b => (bool?)b)));
    private static readonly Gen<int?> MaybeMb = Gen.Frequency((1, Gen.Const((int?)null)), (2, Gen.Int.Select(i => (int?)i)));

    private static readonly Gen<SourceScope> AnyScope = Gen.OneOf<SourceScope>(
        Gen.Select(Text, MaybeText, (b, p) => (SourceScope)new S3BucketScope(b, p)),
        Gen.Select(Text, MaybeText, (c, p) => (SourceScope)new AzureBlobContainerScope(c, p)),
        Gen.Select(MaybeText, MaybeList, MaybeList, (s, i, e) => (SourceScope)new FilesystemFolderScope(s, i, e)),
        Gen.Select(MaybeText, MaybeList, MaybeList, (s, i, e) => (SourceScope)new SftpFolderScope(s, i, e)),
        Gen.Select(Text, Text, Gen.Enum<GitHubContentKind>(), Gen.Long.Nullable(), Gen.Bool, MaybeList, MaybeList,
            Gen.Select(MaybeBool, MaybeList, MaybeList),
            (o, r, k, id, priv, inc, exc, comments) => (SourceScope)new GitHubRepositoryScope(
                o, r, k, id, priv, inc, exc, comments.Item1, comments.Item2, comments.Item3)),
        Gen.Select(Digits, MaybeText, MaybeText, MaybeBool, MaybeMb, (id, key, name, a, mb) => (SourceScope)new JiraProjectScope(id, key, name, a, mb)),
        Gen.Select(Digits, MaybeText, MaybeText, MaybeBool, MaybeMb, (id, key, name, a, mb) => (SourceScope)new ConfluenceSpaceScope(id, key, name, a, mb)),
        Gen.Select(Text, Text, (u, s) => (SourceScope)new CustomSourceScope($"https://{u}.example.com", s)));
}
