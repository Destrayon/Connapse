using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>The site and service-account credential an administrator wants to save.</summary>
public sealed record AtlassianSiteTestRequest(string SiteUrl, string ClientId, string ClientSecret)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"SiteUrl = {SiteUrl}, ClientId = {ClientId}, ClientSecret = ***");
        return true;
    }
}

/// <summary>One scope the service account's credential needs, and what Connapse does with it.</summary>
/// <param name="Optional">Needed only by an option a source can leave off.</param>
public sealed record AtlassianScope(string Name, string Reason, bool Optional = false);

/// <summary>
/// The Confluence scopes a site's credential needs. Comments are always indexed with their page, so
/// their scope is required; attachments are a per-source option, so theirs is not, and the site test
/// never fails without it.
/// </summary>
public static class AtlassianSiteScopes
{
    public const string ContentPermission = "read:content.permission:confluence";

    public static IReadOnlyList<AtlassianScope> All { get; } =
    [
        new("read:space:confluence", "list the site's spaces so you can choose which to index"),
        new("read:page:confluence", "read page text so it can be searched"),
        new("read:comment:confluence", "read page comments, which are indexed with every page"),
        new("read:attachment:confluence", "read attached files; needed only if a source includes attachments", Optional: true),
        new("read:folder:confluence", "follow folders so each page keeps its place in the space"),
        new("read:content-details:confluence", "find what changed since the last sync and look up people"),
        new(ContentPermission, "check which pages each person may read, so search shows them only those"),
    ];

    /// <summary>The scopes a passing test exercises when Atlassian does not list what it granted.</summary>
    public static IReadOnlyList<string> ProvenByTest { get; } =
        ["read:space:confluence", "read:page:confluence", ContentPermission];
}

/// <summary>
/// The troubleshooting entries of the Atlassian setup guide that test failures link to. Each value
/// is the anchor of a heading under "Troubleshooting" in <c>docs/atlassian-setup.md</c>.
/// </summary>
public static class AtlassianSiteTroubleshooting
{
    public const string GuideUrl = "https://github.com/Destrayon/Connapse/blob/main/docs/atlassian-setup.md";

    public const string SiteNotFound = "site-not-found";
    public const string CredentialsRejected = "client-id-or-secret-rejected";
    public const string CantReadConfluence = "cant-read-confluence";
    public const string NoSpaces = "no-confluence-access-or-no-spaces";
    public const string NotAdministrator = "not-confluence-administrator";
    public const string MissingScope = "missing-scope";
    public const string NothingToCheckYet = "site-has-no-pages-or-no-other-users-yet";

    /// <summary>
    /// The guide's Troubleshooting section as a whole: for a timeout, a rate limit or an unreadable
    /// answer, which say nothing about the credential, so no single entry fits.
    /// </summary>
    public const string General = "troubleshooting";

    public static IReadOnlyList<string> All { get; } =
        [SiteNotFound, CredentialsRejected, CantReadConfluence, NoSpaces, NotAdministrator, MissingScope, NothingToCheckYet, General];

    public static string UrlOf(string anchor) => $"{GuideUrl}#{anchor}";
}

/// <summary>
/// Validates an Atlassian site's service account before it is saved. Each probe is one thing
/// Connapse needs later, and the failing one is named in <c>Details["step"]</c>: finding the site,
/// the token exchange, reading Confluence, seeing a space, and being Confluence Administrator
/// (needed to ask what another user may read). A failure also carries the troubleshooting entry
/// for it (<c>Details["troubleshoot"]</c>), how long its step took (<c>Details["stepMs"]</c>), and,
/// when Atlassian said why, its answer (<c>Details["detail"]</c>: a status and Atlassian's own code
/// or message, never a secret or token). A site that passes carries its <c>cloudId</c>,
/// <c>siteUrl</c> and confirmed <c>scopes</c> in <c>Details</c>; so does a warning, a check that
/// could not finish only because the site has no pages or no other user yet.
/// </summary>
/// <remarks>
/// Uses its own token cache, never the shared one: that cache is keyed by client id, so a candidate
/// secret could otherwise be "accepted" by a token an earlier, correct secret had minted.
/// </remarks>
public sealed class AtlassianConnectionTester(IHttpClientFactory httpClients, TimeProvider? time = null) : IConnectionTester
{
    public const string SiteStep = "site";
    public const string TokenStep = "token";
    public const string IdentityStep = "identity";
    public const string SpacesStep = "spaces";
    public const string AdminStep = "admin";

    /// <summary>Every step in the order the test runs them.</summary>
    public static IReadOnlyList<string> Steps { get; } = [SiteStep, TokenStep, IdentityStep, SpacesStep, AdminStep];

    /// <summary>What a step does, in the words an admin sees.</summary>
    public static string StepLabel(string? step) => step switch
    {
        SiteStep => "Finding the site",
        TokenStep => "Signing in as the service account",
        IdentityStep => "Reading Confluence",
        SpacesStep => "Listing spaces",
        AdminStep => "Checking access permissions",
        _ => "Checking the connection",
    };

    private static string DefaultTroubleshooting(string step) => step switch
    {
        SiteStep => AtlassianSiteTroubleshooting.SiteNotFound,
        TokenStep => AtlassianSiteTroubleshooting.CredentialsRejected,
        IdentityStep => AtlassianSiteTroubleshooting.CantReadConfluence,
        SpacesStep => AtlassianSiteTroubleshooting.NoSpaces,
        _ => AtlassianSiteTroubleshooting.NotAdministrator,
    };

    private const string UnverifiedMessage =
        "Couldn't verify Confluence Administrator access because Atlassian didn't answer the check properly. "
        + "Wait a minute and test again.";

    private const string AdminMessage =
        "The service account isn't Confluence Administrator, so Connapse can't check what other people may read. "
        + "In Atlassian Administration, go to Directory > Service accounts, set its Confluence role to the admin role, then test again.";

    private const string MissingPermissionScopeMessage =
        "The credential is missing the " + AtlassianSiteScopes.ContentPermission + " scope, so Connapse can't check what "
        + "other people may read. Create a new OAuth 2.0 credential on the service account with every scope listed in the "
        + "setup steps, then enter it here.";

    private const string CredentialsRejectedMessage =
        "Atlassian rejected the client ID or secret. Copy both again from the service account's OAuth 2.0 credential in "
        + "Atlassian Administration. If the secret is lost, create a new credential.";

    private const string CantReadConfluenceMessage =
        "The credential can't read Confluence on this site. Check that the service account's Confluence role isn't None "
        + "and that its credential has every scope listed in the setup steps, then test again.";

    private const string NoSpacesMessage =
        "The service account can't see any Confluence space. Give it the Viewer role in at least one space "
        + "(Space settings > Users), then test again.";

    // The probes read what Atlassian returned; any of these means this probe failed, not that the test crashed.
    private static bool IsProbeFailure(Exception ex) =>
        ex is HttpRequestException or AtlassianAuthException or JsonException or InvalidOperationException;

    private static bool IsRefusal(Exception ex) =>
        ex is AtlassianAuthException
        || ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized };

    public async Task<ConnectionTestResult> TestConnectionAsync(
        object settings, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (settings is not AtlassianSiteTestRequest request)
            return ConnectionTestResult.CreateFailure("Invalid settings: expected AtlassianSiteTestRequest.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        CancellationToken linked = cts.Token;

        var run = new Run();

        string? siteUrl = AtlassianSiteResolver.NormalizeSiteUrl(request.SiteUrl);
        if (siteUrl is null)
            return run.Fail(
                "The site address must look like https://your-site.atlassian.net. Copy it from your browser's address bar "
                + "while Confluence is open.");

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            run.Enter(TokenStep);
            return run.Fail("Enter both the client ID and the client secret of the service account's OAuth 2.0 credential.");
        }

        string? cloudId = null;
        try
        {
            using var lookup = httpClients.CreateClient(AtlassianSiteResolver.HttpClientName);
            cloudId = await AtlassianSiteResolver.ResolveCloudIdAsync(lookup, siteUrl, linked);
            if (cloudId is null)
                return run.Fail(
                    $"Couldn't find an Atlassian Cloud site at {siteUrl}. Check the spelling: it is the address in your "
                    + "browser when you open Confluence.");

            var site = new AtlassianSite(siteUrl, cloudId, request.ClientId.Trim());
            var tokens = new AtlassianTokenSource(httpClients, time ?? TimeProvider.System);

            run.Enter(TokenStep);
            try
            {
                await tokens.GetTokenAsync(site, request.ClientSecret, linked);
            }
            catch (AtlassianAuthException ex)
            {
                var rejected = run.Fail(CredentialsRejectedMessage, detail: ex.Message);
                rejected.Details!["rejected"] = true;
                return rejected;
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException)
            {
                // An unreadable token answer or a failed exchange; rate limits and cancellation pass through.
                return run.Fail("Atlassian's sign-in service didn't answer properly. Wait a minute and test again.",
                    AtlassianSiteTroubleshooting.General, Describe(ex));
            }

            IReadOnlyList<string>? granted = tokens.GrantedScopes(site);

            using var http = httpClients.CreateClient(AtlassianApiClient.HttpClientName);
            var api = new AtlassianApiClient(http, tokens, site, request.ClientSecret, time);

            run.Enter(IdentityStep);
            string? ownAccountId;
            try
            {
                using var current = await api.GetJsonAsync<JsonDocument>("rest/api/user/current", linked);
                ownAccountId = Text(current.RootElement, "accountId");
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return run.Fail(CantReadConfluenceMessage, detail: Describe(ex));
            }

            // Without the service account's own id the "other user" below could be the account itself,
            // and checking yourself needs no admin rights: fail closed.
            if (string.IsNullOrWhiteSpace(ownAccountId))
                return run.Fail(CantReadConfluenceMessage, detail: "Atlassian didn't say which account the credential belongs to.");

            run.Enter(SpacesStep);
            try
            {
                using var spaces = await api.GetJsonAsync<JsonDocument>("api/v2/spaces?limit=1", linked);
                if (FirstId(spaces.RootElement) is null)
                    return run.Fail(NoSpacesMessage, detail: "Atlassian listed no spaces for this account.");
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return run.Fail(NoSpacesMessage, detail: Describe(ex));
            }

            string? pageId;
            try
            {
                using var pages = await api.GetJsonAsync<JsonDocument>("api/v2/pages?limit=1", linked);
                pageId = FirstId(pages.RootElement);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return run.Fail(
                    "The service account can't list pages. Check that its credential has the read:page:confluence scope, "
                    + "then test again.",
                    AtlassianSiteTroubleshooting.MissingScope, Describe(ex));
            }

            run.Enter(AdminStep);
            if (pageId is null)
                return run.Warn(
                    "This site has no pages yet, so Connapse can't confirm the service account is Confluence Administrator. "
                    + "You can save anyway; the check runs again when you next test. Or create a page and test again.",
                    siteUrl, cloudId);

            string? otherUser;
            try
            {
                using var users = await api.GetJsonAsync<JsonDocument>("rest/api/search/user?cql=type=user&limit=25", linked);
                otherUser = FirstOtherAccount(users.RootElement, ownAccountId);
            }
            catch (Exception ex) when (IsRefusal(ex))
            {
                return run.Fail(AdminMessage, detail: Describe(ex));
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return run.Fail(UnverifiedMessage, detail: Describe(ex));
            }

            if (otherUser is null)
                return run.Warn(
                    "The site has no user other than the service account, so Connapse can't confirm Confluence Administrator "
                    + "access. You can save anyway; the check runs again when you next test. Or add another user and test again.",
                    siteUrl, cloudId);

            // A missing scope and a missing role both stop the check; Atlassian names the scope when
            // it is the cause (and, when it lists the token's scopes, the scope can be seen missing).
            bool scopeMissing = granted is not null
                && !granted.Contains(AtlassianSiteScopes.ContentPermission, StringComparer.OrdinalIgnoreCase);

            HttpStatusCode checkStatus;
            string? checkDetail = null;
            bool checkAnswered = false;
            try
            {
                using var check = await api.PostAsync(
                    $"rest/api/content/{Uri.EscapeDataString(pageId)}/permission/check",
                    new { subject = new { type = "user", identifier = otherUser }, operation = "read" }, linked);
                checkStatus = check.StatusCode;
                string body = await check.Content.ReadAsStringAsync(linked);

                // A 200 proves nothing by itself (a login page or proxy error can be a 200): only a
                // PermissionCheckResponse carrying a boolean hasPermission shows the check ran. Either
                // value will do, since answering at all for another user needs administrator rights.
                if (checkStatus == HttpStatusCode.OK)
                {
                    using var answer = JsonDocument.Parse(body);
                    checkAnswered = answer.RootElement.ValueKind == JsonValueKind.Object
                        && answer.RootElement.TryGetProperty("hasPermission", out var has)
                        && has.ValueKind is JsonValueKind.True or JsonValueKind.False;
                }
                else
                {
                    checkDetail = DescribeAnswer(checkStatus, body);
                    if (body.Contains("scope", StringComparison.OrdinalIgnoreCase))
                        scopeMissing = true;
                }
            }
            catch (AtlassianAuthException ex)
            {
                // Two 401s in a row after the same token read Confluence above: Atlassian refuses a
                // token on one endpoint like this when its scopes don't cover that endpoint.
                return run.Fail(MissingPermissionScopeMessage, AtlassianSiteTroubleshooting.MissingScope, ex.Message);
            }
            catch (Exception ex) when (IsProbeFailure(ex))
            {
                return run.Fail(UnverifiedMessage, detail: Describe(ex));
            }

            if (checkStatus is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return scopeMissing
                    ? run.Fail(MissingPermissionScopeMessage, AtlassianSiteTroubleshooting.MissingScope, checkDetail)
                    : run.Fail(AdminMessage, detail: checkDetail);
            if (checkStatus != HttpStatusCode.OK)
                return run.Fail($"{UnverifiedMessage} Confluence answered HTTP {(int)checkStatus}.", detail: checkDetail);
            if (!checkAnswered)
                return run.Fail(UnverifiedMessage, detail: "Confluence answered the check without saying whether the user may read the page.");

            IReadOnlyList<string> confirmed = granted is null
                ? AtlassianSiteScopes.ProvenByTest
                : AtlassianSiteScopes.All.Select(s => s.Name).Where(n => granted.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

            return ConnectionTestResult.CreateSuccess(
                $"Connected to {siteUrl}: found the site, signed in as the service account, read Confluence, listed spaces, "
                + "and confirmed it can check what other people may read.",
                new Dictionary<string, object> { ["cloudId"] = cloudId, ["siteUrl"] = siteUrl, ["scopes"] = confirmed.ToArray() },
                run.Elapsed);
        }
        // None of these is about the credential, so none may point at the step's usual entry (at the
        // sign-in step, "Client ID or secret rejected").
        catch (AtlassianRateLimitedException ex)
        {
            return run.Fail($"{ex.Message} Atlassian is limiting requests; wait, then test again.",
                AtlassianSiteTroubleshooting.General);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // This test's own time limit, or the HTTP client's.
            return run.Fail(
                "Atlassian didn't answer in time. Wait a minute and test again; if it keeps happening, check that this server "
                + "can reach api.atlassian.com and auth.atlassian.com over HTTPS.",
                AtlassianSiteTroubleshooting.General, "No answer within the time limit.");
        }
        catch (HttpRequestException ex)
        {
            return run.Fail(
                "Couldn't reach Atlassian from this server. Check that it can reach api.atlassian.com, auth.atlassian.com and "
                + "your site's address over HTTPS, then test again.",
                AtlassianSiteTroubleshooting.General, Describe(ex));
        }
    }

    /// <summary>The step being run, and the clocks for the whole test and that step.</summary>
    private sealed class Run
    {
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Stopwatch _step = Stopwatch.StartNew();

        public string Step { get; private set; } = SiteStep;

        public TimeSpan Elapsed => _total.Elapsed;

        public void Enter(string step)
        {
            Step = step;
            _step.Restart();
        }

        public ConnectionTestResult Fail(string message, string? troubleshoot = null, string? detail = null) =>
            ConnectionTestResult.CreateFailure(message, Details(troubleshoot, detail), _total.Elapsed);

        public ConnectionTestResult Warn(string message, string siteUrl, string cloudId)
        {
            var details = Details(AtlassianSiteTroubleshooting.NothingToCheckYet, null);
            details["warning"] = true;
            details["siteUrl"] = siteUrl;
            details["cloudId"] = cloudId;
            return ConnectionTestResult.CreateFailure(message, details, _total.Elapsed);
        }

        private Dictionary<string, object> Details(string? troubleshoot, string? detail)
        {
            var details = new Dictionary<string, object>
            {
                ["step"] = Step,
                ["troubleshoot"] = troubleshoot ?? DefaultTroubleshooting(Step),
                ["stepMs"] = (long)_step.Elapsed.TotalMilliseconds,
            };
            if (!string.IsNullOrWhiteSpace(detail))
                details["detail"] = detail;
            return details;
        }
    }

    // What Atlassian answered, for the "Show details" disclosure: a status and the exception text
    // Connapse wrote, never a request (which is where a secret or token would be).
    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status} ({status})",
        HttpRequestException => "No HTTP answer: the connection failed.",
        AtlassianAuthException => ex.Message,
        JsonException => "Atlassian's answer wasn't valid JSON.",
        _ => ex.Message,
    };

    private const int MaxDetailLength = 300;

    /// <summary>
    /// A refused permission check as "HTTP 403: message", from Atlassian's error fields only
    /// (<c>message</c>, <c>code</c>, <c>reason</c>), never the whole body.
    /// </summary>
    private static string DescribeAnswer(HttpStatusCode status, string body)
    {
        string prefix = $"HTTP {(int)status} ({status})";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return prefix;

            var parts = new List<string>();
            foreach (string name in new[] { "code", "reason", "message" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    && value.ToString() is { Length: > 0 } text)
                    parts.Add(text);
            }

            string described = parts.Count == 0 ? prefix : $"{prefix}: {string.Join(" ", parts)}";
            return described.Length <= MaxDetailLength ? described : described[..MaxDetailLength] + "…";
        }
        catch (JsonException)
        {
            return prefix;
        }
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id))
            {
                string? text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    // search/user results look like { results: [ { user: { accountId } } ] }; a flat shape is accepted too.
    private static string? FirstOtherAccount(JsonElement root, string? ownAccountId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in results.EnumerateArray())
        {
            string? accountId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("user", out var user)
                ? Text(user, "accountId")
                : Text(item, "accountId");

            if (!string.IsNullOrWhiteSpace(accountId) && accountId != ownAccountId)
                return accountId;
        }

        return null;
    }
}
