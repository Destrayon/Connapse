namespace Connapse.Storage.Connectors.GitHub;

/// <summary>Someone who has commented in a repository's issues or pull requests, as synced.</summary>
public sealed record GitHubCommentAuthor(string Login, bool IsBot, int Comments);

/// <summary>
/// Whose comments go into an issue's indexed text: everyone except bot accounts, unless a source
/// names an author to include (a bot whose comments matter) or exclude (a machine user GitHub
/// reports as a person). Logins compare case-insensitively, as GitHub treats them.
/// </summary>
public sealed class GitHubCommentPolicy(IEnumerable<string> include, IEnumerable<string> exclude)
{
    public static readonly GitHubCommentPolicy Default = new([], []);

    private readonly HashSet<string> _include = new(include, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exclude = new(exclude, StringComparer.OrdinalIgnoreCase);

    public bool Allows(string login, bool isBot) =>
        !_exclude.Contains(login) && (_include.Contains(login) || !isBot);

    internal bool Allows(GitHubStoredComment comment) => Allows(comment.Login, comment.AuthorIsBot);
}
