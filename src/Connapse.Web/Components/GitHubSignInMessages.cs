using Connapse.Application.Links;

namespace Connapse.Web.Components;

/// <summary>What the Profile page tells someone who can't sign in to GitHub: what's wrong, and who fixes it.</summary>
public static class GitHubSignInMessages
{
    public static string For(GitHubSignInProblem problem) => problem switch
    {
        GitHubSignInProblem.KeyUnreadable =>
            "GitHub sign-in is not available: this deployment's GitHub App key can't be read. Ask an administrator to enter it again under Providers → GitHub.",
        GitHubSignInProblem.SecretRevoked =>
            "GitHub sign-in is not available: GitHub no longer accepts this deployment's App secret. Ask an administrator to replace it under Providers → GitHub.",
        GitHubSignInProblem.NoClientSecret =>
            "GitHub sign-in is not available: this deployment's GitHub App can't sign people in yet. Ask an administrator to add its client secret under Providers → GitHub.",
        _ => "GitHub sign-in is not available yet. An administrator sets up the GitHub App under Providers → GitHub first.",
    };
}
