using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Storage.Connectors.Atlassian;

namespace Connapse.Storage.ConnectionTesters;

/// <summary>The site and service-account credential an administrator wants to save.</summary>
/// <param name="Product">The product the connection reads; the test runs that product's probes only.</param>
public sealed record AtlassianSiteTestRequest(
    string SiteUrl, string ClientId, string ClientSecret, AtlassianProduct Product = AtlassianProduct.Confluence)
{
    // The generated ToString would print the secret into any log line or message it reached.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"SiteUrl = {SiteUrl}, ClientId = {ClientId}, ClientSecret = ***, Product = {Product}");
        return true;
    }
}

/// <summary>One scope the service account's credential needs, and what Connapse does with it.</summary>
/// <param name="Product">The product the scope belongs to: <c>confluence</c> or <c>jira</c>.</param>
/// <param name="Optional">Needed only by an option a source can leave off.</param>
public sealed record AtlassianScope(string Name, string Reason, string Product, bool Optional = false);

/// <summary>
/// The scopes a site's credential needs, per product. Comments are always indexed with their page or
/// issue, so their scope is required; attachments are a per-source option, so theirs is not, and the
/// site test never fails without it.
/// </summary>
public static class AtlassianSiteScopes
{
    public const string ConfluenceProduct = "confluence";
    public const string JiraProduct = "jira";

    public const string ContentPermission = "read:content.permission:confluence";
    public const string Comment = "read:comment:confluence";
    public const string Attachment = "read:attachment:confluence";
    public const string Folder = "read:folder:confluence";
    public const string ContentDetails = "read:content-details:confluence";

    public const string JiraUser = "read:user:jira";
    public const string JiraProject = "read:project:jira";
    public const string JiraIssueDetails = "read:issue-details:jira";
    public const string JiraIssue = "read:issue:jira";
    public const string JiraComment = "read:comment:jira";
    public const string JiraAttachment = "read:attachment:jira";
    public const string JiraPermission = "read:permission:jira";
    public const string JiraGroup = "read:group:jira";
    public const string JiraProjectRole = "read:project-role:jira";

    public static IReadOnlyList<AtlassianScope> Confluence { get; } =
    [
        new("read:space:confluence", "list the site's spaces so you can choose which to index", ConfluenceProduct),
        new("read:page:confluence", "read page text so it can be searched", ConfluenceProduct),
        new(Comment, "read page comments, which are indexed with every page", ConfluenceProduct),
        new(Attachment, "read attached files; needed only if a source includes attachments", ConfluenceProduct, Optional: true),
        new(Folder, "follow folders so each page keeps its place in the space", ConfluenceProduct),
        new(ContentDetails, "find what changed since the last sync and look up people", ConfluenceProduct),
        new(ContentPermission, "check which pages each person may read, so search shows them only those", ConfluenceProduct),
    ];

    /// <summary>
    /// The Jira scopes: every granular scope Atlassian lists for the REST v3 calls Connapse makes
    /// (OpenAPI spec of 2026-10-12). Many are needed only because Jira returns that data with an
    /// issue or project, and a call is refused unless the token has every scope it lists.
    /// </summary>
    public static IReadOnlyList<AtlassianScope> Jira { get; } =
    [
        new(JiraProject, "list the site's projects so you can choose which to index", JiraProduct),
        new(JiraIssue, "read issue text so it can be searched", JiraProduct),
        new(JiraIssueDetails, "search issues and find what changed since the last sync", JiraProduct),
        new(JiraComment, "read issue comments, which are indexed with every issue", JiraProduct),
        new(JiraAttachment, "read attached files; needed only if a source includes attachments", JiraProduct, Optional: true),
        new(JiraPermission, "check which issues each person may read, so search shows them only those", JiraProduct),
        new(JiraGroup, "read group membership, to check who may read a comment restricted to a group", JiraProduct),
        new(JiraProjectRole, "read project role membership, to check who may read a comment restricted to a role", JiraProduct),
        new("read:issue-security-level:jira", "read each issue's security level, to check who may read it", JiraProduct),
        new(JiraUser, "look up people, and the service account itself", JiraProduct),
        new("read:application-role:jira", "read people, which Jira returns with the products they can use", JiraProduct),
        new("read:avatar:jira", "read people and projects, which Jira returns with their avatars", JiraProduct),
        new("read:audit-log:jira", "search issues, which Jira requires it for", JiraProduct),
        new("read:comment.property:jira", "read comments, which Jira returns with their properties", JiraProduct),
        new("read:field:jira", "read issue fields so their text can be searched", JiraProduct),
        new("read:field-configuration:jira", "read which fields each issue has", JiraProduct),
        new("read:issue-meta:jira", "read issues, which Jira returns with their metadata", JiraProduct),
        new("read:issue-type:jira", "read issue types, which Jira returns with each issue and project", JiraProduct),
        new("read:issue-type-hierarchy:jira", "read issue parents, so each issue keeps its place", JiraProduct),
        new("read:issue.changelog:jira", "read issues, which Jira returns with their history", JiraProduct),
        new("read:issue.vote:jira", "read issues, which Jira returns with their votes", JiraProduct),
        new("read:project-category:jira", "read projects, which Jira returns with their category", JiraProduct),
        new("read:project-version:jira", "read projects and issues, which Jira returns with their versions", JiraProduct),
        new("read:project.component:jira", "read projects and issues, which Jira returns with their components", JiraProduct),
        new("read:project.property:jira", "read projects, which Jira returns with their properties", JiraProduct),
        new("read:status:jira", "read each issue's status", JiraProduct),
    ];

    /// <summary>Every product's scopes, Confluence first.</summary>
    public static IReadOnlyList<AtlassianScope> All { get; } = [.. Confluence, .. Jira];

    /// <summary>
    /// The scopes every passing test exercises: all the required ones. The attachment scope is
    /// probed too, and confirmed only when that probe passes.
    /// </summary>
    public static IReadOnlyList<string> ProvenByTest { get; } =
        ["read:space:confluence", "read:page:confluence", Comment, Folder, ContentDetails, ContentPermission];

    /// <summary>
    /// The Jira scopes a passing Jira probe names: one per call it makes. The others are confirmed
    /// only when Atlassian lists them as granted.
    /// </summary>
    public static IReadOnlyList<string> JiraProvenByTest { get; } =
        [JiraUser, JiraProject, JiraIssueDetails, JiraIssue, JiraComment, JiraPermission, JiraGroup, JiraProjectRole];

    /// <summary>A product's scopes, in the order the page lists them.</summary>
    public static IReadOnlyList<AtlassianScope> Of(string product) =>
        product == JiraProduct ? Jira : Confluence;

    /// <summary>A product's name as the page shows it.</summary>
    public static string DisplayName(string product) => product switch
    {
        ConfluenceProduct => "Confluence",
        JiraProduct => "Jira",
        _ => product,
    };

    /// <summary>The scope's reason, as the list above words it.</summary>
    public static string ReasonOf(string scope) =>
        All.FirstOrDefault(s => string.Equals(s.Name, scope, StringComparison.OrdinalIgnoreCase))?.Reason ?? "sync this site";
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
    public const string CantReadJira = "cant-read-jira";
    public const string JiraProjectMissing = "jira-project-missing";
    public const string NotJiraAdministrator = "not-jira-administrator";
    public const string CantReadJiraMembership = "cant-read-jira-membership";

    /// <summary>A synced space answered 403 or 404: access was taken away, or the space was deleted.</summary>
    public const string SpaceUnreadable = "space-can-no-longer-be-read";

    /// <summary>A synced Jira project answered 403 or 404: access was taken away, or the project was deleted.</summary>
    public const string ProjectUnreadable = "project-can-no-longer-be-read";

    /// <summary>Atlassian could not be reached, or answered in a way that says nothing about the credential.</summary>
    public const string Unreachable = "couldnt-reach-atlassian";

    /// <summary>A sync left out attachments over the size cap or of a type Connapse can't read.</summary>
    public const string AttachmentsSkipped = "attachments-skipped";

    /// <summary>
    /// The guide's Troubleshooting section as a whole: for a timeout, a rate limit or an unreadable
    /// answer, which say nothing about the credential, so no single entry fits.
    /// </summary>
    public const string General = "troubleshooting";

    public static IReadOnlyList<string> All { get; } =
        [SiteNotFound, CredentialsRejected, CantReadConfluence, NoSpaces, NotAdministrator, MissingScope, NothingToCheckYet,
         CantReadJira, NotJiraAdministrator, CantReadJiraMembership, General];

    /// <summary>The entries only sync, the spaces dialog and the linking app link to; the site test never fails with these.</summary>
    public static IReadOnlyList<string> Elsewhere { get; } = [SpaceUnreadable, ProjectUnreadable, JiraProjectMissing, Unreachable, AttachmentsSkipped];

    public static string UrlOf(string anchor) => $"{GuideUrl}#{anchor}";
}

/// <summary>One product's probes against a site whose token already works.</summary>
internal interface IAtlassianProductProbe
{
    string Product { get; }                                     // "confluence" | "jira"

    /// <param name="steps">The test's run: the probe enters its steps on it, so a timeout or rate limit names them.</param>
    Task<ProductProbeResult> RunAsync(
        AtlassianSite site, string secret, AtlassianTokenSource tokens, AtlassianConnectionTester.Run steps, CancellationToken ct);
}

/// <summary>How one product's probe ended; <paramref name="StepMs"/> is how long its last step took.</summary>
internal sealed record ProductProbeResult(
    string Product, bool Passed, bool Warning, string? Step, string? Message, string? Troubleshoot, string? Detail,
    IReadOnlyList<string> ConfirmedScopes, long StepMs = 0);

/// <summary>
/// Validates an Atlassian site's service account before it is saved. Each probe is one thing
/// Connapse needs later, and the failing one is named in <c>Details["step"]</c>: finding the site,
/// the token exchange, reading Confluence, seeing a space, reading comments, folders and changes
/// (one probe per scope a sync needs), and being Confluence Administrator
/// (needed to ask what another user may read). A failure also carries the troubleshooting entry
/// for it (<c>Details["troubleshoot"]</c>), how long its step took (<c>Details["stepMs"]</c>), and,
/// when Atlassian said why, its answer (<c>Details["detail"]</c>: a status and Atlassian's own code
/// or message, never a secret or token). A site that passes carries its <c>cloudId</c>,
/// <c>siteUrl</c> and confirmed <c>scopes</c> in <c>Details</c>; so does a warning, a check that
/// could not finish only because the site has no pages or no other user yet.
/// A Jira connection runs Jira's probes in place of Confluence's, with the same keys.
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
    public const string ContentStep = "content";
    public const string AdminStep = "admin";
    public const string JiraIdentityStep = "jira-identity";
    public const string JiraProjectsStep = "jira-projects";
    public const string JiraContentStep = "jira-content";
    public const string JiraAdminStep = "jira-admin";
    public const string JiraMembershipStep = "jira-membership";

    /// <summary>Every step in the order the test runs them.</summary>
    public static IReadOnlyList<string> Steps { get; } =
    [
        SiteStep, TokenStep, IdentityStep, SpacesStep, ContentStep, AdminStep,
        JiraIdentityStep, JiraProjectsStep, JiraContentStep, JiraAdminStep, JiraMembershipStep,
    ];

    /// <summary>What a step does, in the words an admin sees.</summary>
    public static string StepLabel(string? step) => step switch
    {
        SiteStep => "Finding the site",
        TokenStep => "Signing in as the service account",
        IdentityStep => "Reading Confluence",
        SpacesStep => "Listing spaces",
        ContentStep => "Reading comments, folders and changes",
        AdminStep => "Checking access permissions",
        JiraIdentityStep => "Reading Jira",
        JiraProjectsStep => "Listing Jira projects",
        JiraContentStep => "Reading issues and comments",
        JiraAdminStep => "Checking Jira access permissions",
        JiraMembershipStep => "Reading Jira groups and project roles",
        _ => "Checking the connection",
    };

    private static string DefaultTroubleshooting(string step) => step switch
    {
        SiteStep => AtlassianSiteTroubleshooting.SiteNotFound,
        TokenStep => AtlassianSiteTroubleshooting.CredentialsRejected,
        IdentityStep => AtlassianSiteTroubleshooting.CantReadConfluence,
        SpacesStep => AtlassianSiteTroubleshooting.NoSpaces,
        ContentStep or JiraContentStep => AtlassianSiteTroubleshooting.MissingScope,
        JiraIdentityStep or JiraProjectsStep => AtlassianSiteTroubleshooting.CantReadJira,
        JiraAdminStep => AtlassianSiteTroubleshooting.NotJiraAdministrator,
        JiraMembershipStep => AtlassianSiteTroubleshooting.CantReadJiraMembership,
        _ => AtlassianSiteTroubleshooting.NotAdministrator,
    };

    /// <summary>A probe's credential lacks <paramref name="scope"/>: what Connapse then can't do, and the fix.</summary>
    public static string MissingScopeMessage(string scope) =>
        $"The credential is missing the {scope} scope, which Connapse needs to {AtlassianSiteScopes.ReasonOf(scope)}. "
        + "Create a new OAuth 2.0 credential on the service account with every scope listed in the setup steps, then enter it here.";

    internal enum Probe { Granted, Missing, Unanswered }

    private const string CredentialsRejectedMessage =
        "Atlassian rejected the client ID or secret. Copy both again from the service account's OAuth 2.0 credential in "
        + "Atlassian Administration. If the secret is lost, create a new credential.";

    private const string SignInUnansweredMessage =
        "Atlassian's sign-in service didn't answer properly. Wait a minute and test again.";

    // The probes read what Atlassian returned; any of these means this probe failed, not that the test crashed.
    internal static bool IsProbeFailure(Exception ex) =>
        ex is HttpRequestException or AtlassianAuthException or JsonException or InvalidOperationException;

    internal static bool IsRefusal(Exception ex) =>
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

        string product = request.Product == AtlassianProduct.Jira ? "Jira" : "Confluence";
        string? siteUrl = AtlassianSiteResolver.NormalizeSiteUrl(request.SiteUrl);
        if (siteUrl is null)
            return run.Fail(
                "The site address must look like https://your-site.atlassian.net. Copy it from your browser's address bar "
                + $"while {product} is open.");

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
                    + $"browser when you open {product}.");

            var site = new AtlassianSite(siteUrl, cloudId, request.ClientId.Trim(), request.Product);
            var tokens = new AtlassianTokenSource(httpClients, time ?? TimeProvider.System);

            run.Enter(TokenStep);
            try
            {
                await tokens.GetTokenAsync(site, request.ClientSecret, linked);
            }
            catch (AtlassianAuthException ex) when (AtlassianSyncErrors.IsCredentialRefused(ex))
            {
                var rejected = run.Fail(CredentialsRejectedMessage, detail: ex.Detail ?? ex.Message);
                rejected.Details!["rejected"] = true;
                return rejected;
            }
            catch (AtlassianAuthException ex)
            {
                // A redirect, or a token answer without a token: nothing about the credential itself.
                return run.Fail(SignInUnansweredMessage, AtlassianSiteTroubleshooting.General, ex.Detail ?? ex.Message);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException)
            {
                // An unreadable token answer or a failed exchange; rate limits and cancellation pass through.
                return run.Fail(SignInUnansweredMessage, AtlassianSiteTroubleshooting.General, Describe(ex));
            }

            IAtlassianProductProbe probe = request.Product == AtlassianProduct.Jira
                ? new JiraSiteProbe(httpClients, time)
                : new ConfluenceSiteProbe(httpClients, time);
            ProductProbeResult result = await probe.RunAsync(site, request.ClientSecret, tokens, run, linked);

            if (result.Passed)
                return ConnectionTestResult.CreateSuccess(
                    $"Connected to {siteUrl}: found the site, signed in as the service account, {result.Message}.",
                    new Dictionary<string, object>
                    {
                        ["cloudId"] = cloudId,
                        ["siteUrl"] = siteUrl,
                        ["scopes"] = result.ConfirmedScopes.ToArray(),
                    },
                    run.Elapsed);
            return result.Warning ? run.Warn(result, siteUrl, cloudId) : run.Fail(result);
        }
        catch (Exception ex) when (Unanswered(ex, ct) is { } failure)
        {
            return run.Fail(failure.Message, failure.Troubleshoot, failure.Detail);
        }
    }

    /// <summary>
    /// A rate limit, a timeout or a connection that failed, in the words a failure shows; null for
    /// any other exception.
    /// </summary>
    private static (string Message, string Troubleshoot, string? Detail)? Unanswered(Exception ex, CancellationToken ct) => ex switch
    {
        // None of these is about the credential, so none may point at the step's usual entry (at the
        // sign-in step, "Client ID or secret rejected").
        AtlassianRateLimitedException =>
            ($"{ex.Message} Atlassian is limiting requests; wait, then test again.", AtlassianSiteTroubleshooting.General, null),
        // This test's own time limit, or the HTTP client's.
        OperationCanceledException when !ct.IsCancellationRequested =>
            ("Atlassian didn't answer in time. Wait a minute and test again; if it keeps happening, check that this server "
                + "can reach api.atlassian.com and auth.atlassian.com over HTTPS.",
                AtlassianSiteTroubleshooting.General, "No answer within the time limit."),
        HttpRequestException =>
            ("Couldn't reach Atlassian from this server. Check that it can reach api.atlassian.com, auth.atlassian.com and "
                + "your site's address over HTTPS, then test again.",
                AtlassianSiteTroubleshooting.General, Describe(ex)),
        _ => null,
    };

    /// <summary>The step being run, and the clocks for the whole test and that step.</summary>
    internal sealed class Run
    {
        private readonly Stopwatch _total = Stopwatch.StartNew();
        private readonly Stopwatch _step = Stopwatch.StartNew();

        public string Step { get; private set; } = SiteStep;

        public TimeSpan Elapsed => _total.Elapsed;

        public long StepMs => (long)_step.Elapsed.TotalMilliseconds;

        public void Enter(string step)
        {
            Step = step;
            _step.Restart();
        }

        /// <summary>The steps of one product's probe, entered on this run so a timeout mid-probe still names them.</summary>
        public ProductSteps For(string product) => new(this, product);

        public ConnectionTestResult Fail(string message, string? troubleshoot = null, string? detail = null) =>
            ConnectionTestResult.CreateFailure(message, Details(Step, StepMs, troubleshoot, detail), _total.Elapsed);

        /// <summary>A product's failure, at the step and with the duration that product recorded.</summary>
        public ConnectionTestResult Fail(ProductProbeResult product) =>
            ConnectionTestResult.CreateFailure(product.Message ?? "",
                Details(product.Step ?? Step, product.StepMs, product.Troubleshoot, product.Detail), _total.Elapsed);

        public ConnectionTestResult Warn(ProductProbeResult product, string siteUrl, string cloudId)
        {
            var details = Details(product.Step ?? Step, product.StepMs, AtlassianSiteTroubleshooting.NothingToCheckYet, null);
            details["warning"] = true;
            details["siteUrl"] = siteUrl;
            details["cloudId"] = cloudId;
            return ConnectionTestResult.CreateFailure(product.Message ?? "", details, _total.Elapsed);
        }

        private static Dictionary<string, object> Details(string step, long stepMs, string? troubleshoot, string? detail)
        {
            var details = new Dictionary<string, object>
            {
                ["step"] = step,
                ["troubleshoot"] = troubleshoot ?? DefaultTroubleshooting(step),
                ["stepMs"] = stepMs,
            };
            if (!string.IsNullOrWhiteSpace(detail))
                details["detail"] = detail;
            return details;
        }
    }

    /// <summary>One product's view of the run: it enters steps and reports how its probe ended.</summary>
    internal sealed class ProductSteps(Run run, string product)
    {
        public void Enter(string step) => run.Enter(step);

        public ProductProbeResult Fail(string message, string? troubleshoot = null, string? detail = null) =>
            new(product, Passed: false, Warning: false, run.Step, message, troubleshoot ?? DefaultTroubleshooting(run.Step), detail, [],
                run.StepMs);

        public ProductProbeResult Warn(string message) =>
            new(product, Passed: false, Warning: true, run.Step, message, AtlassianSiteTroubleshooting.NothingToCheckYet, null, [],
                run.StepMs);

        public ProductProbeResult Pass(string message, IReadOnlyList<string> confirmedScopes) =>
            new(product, Passed: true, Warning: false, null, message, null, null, confirmedScopes, run.StepMs);
    }

    // What Atlassian answered, for the "Details" disclosure: a status and the exception text
    // Connapse wrote, never a request (which is where a secret or token would be).
    internal static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status} ({status})",
        HttpRequestException => "No HTTP answer: the connection failed.",
        AtlassianAuthException auth => auth.Detail ?? auth.Message,
        JsonException => "Atlassian's answer wasn't valid JSON.",
        _ => ex.Message,
    };

    private const int MaxDetailLength = 300;

    /// <summary>
    /// A refused permission check as "HTTP 403: message", from Atlassian's error fields only
    /// (<c>message</c>, <c>code</c>, <c>reason</c>), never the whole body.
    /// </summary>
    internal static string DescribeAnswer(HttpStatusCode status, string body)
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

    internal static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static string? FirstId(JsonElement root)
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

    /// <summary>
    /// One GET read only for its status. Success, or a 404/400 (Atlassian checks a token's scopes
    /// before it looks anything up), shows the scope is granted; a 401 or 403 that it is missing.
    /// </summary>
    internal static async Task<(Probe Outcome, string? Detail)> ProbeAsync(AtlassianApiClient api, string path, CancellationToken ct)
    {
        try
        {
            using var response = await api.GetAsync(path, ct);
            if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return (Probe.Granted, null);

            string detail = DescribeAnswer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
            return response.StatusCode == HttpStatusCode.Forbidden ? (Probe.Missing, detail) : (Probe.Unanswered, detail);
        }
        catch (AtlassianAuthException ex)
        {
            // Two 401s in a row after the same token read Confluence: the token's scopes don't cover this call.
            return (Probe.Missing, ex.Detail ?? ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return (Probe.Unanswered, Describe(ex));
        }
    }
}
