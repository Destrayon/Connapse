using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Web.Services;

/// <summary>
/// The manifest that registers Connapse's GitHub App: GitHub's one-click flow, where the browser
/// posts this JSON to github.com, the administrator confirms, and GitHub hands back a one-time code
/// that <c>ConnapseGitHubApp.ConvertManifestAsync</c> trades for the App's id and key. Nothing is
/// pasted.
/// </summary>
public static partial class GitHubAppManifest
{
    /// <summary>Where GitHub returns after the App is created, relative to Connapse's base URL.</summary>
    public const string CallbackPath = "api/v1/providers/github/manifest/callback";

    /// <summary>Where GitHub returns after the App is installed somewhere; it forwards to the Connections page.</summary>
    public const string InstalledPath = "api/v1/providers/github/installed";

    /// <summary>Reserved for signing users in through the App (the private-repository phase).</summary>
    public const string UserCallbackPath = "api/v1/auth/cloud/github/callback";

    /// <summary>GitHub's limit on an App's name.</summary>
    private const int MaxNameLength = 34;

    /// <summary>
    /// The manifest, for a Connapse reachable at <paramref name="baseUrl"/> — the address the
    /// administrator's browser is using, so the redirect lands where they are.
    /// </summary>
    /// <remarks>
    /// Read-only permissions, and only the ones the connector reads: metadata (required by GitHub),
    /// contents (docs over git), issues, and pull requests. No webhooks — a self-hosted instance is
    /// rarely reachable from GitHub, and polling is the design.
    /// <para>
    /// <paramref name="isPublic"/> decides who can install the App and sign in through it: a private
    /// App only its owning account and that account's members. An organisation's App can stay
    /// private; one on a personal account must be public, or nobody but its owner could sign in.
    /// </para>
    /// </remarks>
    public static string Build(string baseUrl, bool isPublic)
    {
        string root = baseUrl.TrimEnd('/') + "/";
        string host = new Uri(root).Host;

        var manifest = new JsonObject
        {
            ["name"] = Name(host),
            ["url"] = root,
            ["redirect_url"] = root + CallbackPath,
            ["callback_urls"] = new JsonArray(root + UserCallbackPath),
            ["setup_url"] = root + InstalledPath,
            ["setup_on_update"] = true,
            ["public"] = isPublic,
            ["hook_attributes"] = new JsonObject { ["url"] = root, ["active"] = false },
            ["default_permissions"] = new JsonObject
            {
                ["metadata"] = "read",
                ["contents"] = "read",
                ["issues"] = "read",
                ["pull_requests"] = "read",
            },
            ["default_events"] = new JsonArray(),
        };

        return manifest.ToJsonString();
    }

    /// <summary>
    /// The github.com address the manifest is posted to: the administrator's own account, or an
    /// organisation they name. Null when the organisation name is not a valid GitHub login.
    /// </summary>
    public static string? FormAction(string? organisation, string state)
    {
        string encodedState = Uri.EscapeDataString(state);
        if (string.IsNullOrWhiteSpace(organisation))
            return $"https://github.com/settings/apps/new?state={encodedState}";

        string org = organisation.Trim();
        return LoginPattern().IsMatch(org)
            ? $"https://github.com/organizations/{org}/settings/apps/new?state={encodedState}"
            : null;
    }

    /// <summary>
    /// "Connapse (host)", cut to GitHub's 34 characters. App names are unique across GitHub, so the
    /// host keeps two installations of Connapse from colliding; GitHub lets the name be edited on
    /// its confirmation page if it still does.
    /// </summary>
    internal static string Name(string host)
    {
        string name = $"Connapse ({host})";
        return name.Length <= MaxNameLength ? name : name[..(MaxNameLength - 1)] + ")";
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex LoginPattern();
}

/// <summary>
/// Manifest requests in flight, keyed by the random <c>state</c> sent to GitHub and returned on the
/// redirect. A state is honoured once, only for the administrator who started it, and only within
/// the hour GitHub's code is valid — the same guard the Azure sign-in link uses against a callback
/// planted by someone else.
/// </summary>
public sealed class GitHubManifestRequests(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(60);

    /// <summary>Makes read-and-remove one step, so two callbacks racing on a state cannot both claim it.</summary>
    private readonly Lock _claim = new();

    public string Start(Guid userId)
    {
        string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        cache.Set(Key(state), userId, Lifetime);
        return state;
    }

    /// <summary>True, once, when <paramref name="state"/> was started by <paramref name="userId"/>.</summary>
    public bool TryComplete(string? state, Guid userId)
    {
        if (string.IsNullOrWhiteSpace(state))
            return false;

        Guid startedBy;
        lock (_claim)
        {
            if (!cache.TryGetValue(Key(state), out startedBy))
                return false;

            cache.Remove(Key(state));
        }

        return startedBy == userId;
    }

    private static string Key(string state) => "github-manifest:" + state;
}
