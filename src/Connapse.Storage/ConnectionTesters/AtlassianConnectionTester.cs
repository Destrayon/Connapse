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
    public const string Comment = "read:comment:confluence";
    public const string Attachment = "read:attachment:confluence";
    public const string Folder = "read:folder:confluence";
    public const string ContentDetails = "read:content-details:confluence";

    public static IReadOnlyList<AtlassianScope> All { get; } =
    [
        new("read:space:confluence", "list the site's spaces so you can choose which to index"),
        new("read:page:confluence", "read page text so it can be searched"),
        new(Comment, "read page comments, which are indexed with every page"),
        new(Attachment, "read attached files; needed only if a source includes attachments", Optional: true),
        new(Folder, "follow folders so each page keeps its place in the space"),
        new(ContentDetails, "find what changed since the last sync and look up people"),
        new(ContentPermission, "check which pages each person may read, so search shows them only those"),
    ];

    /// <summary>
    /// The scopes every passing test exercises: all the required ones. The attachment scope is
    /// probed too, and confirmed only when that probe passes.
    /// </summary>
    public static IReadOnlyList<string> ProvenByTest { get; } =
        ["read:space:confluence", "read:page:confluence", Comment, Folder, ContentDetails, ContentPermission];

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

    /// <summary>A synced space answered 403 or 404: access was taken away, or the space was deleted.</summary>
    public const string SpaceUnreadable = "space-can-no-longer-be-read";

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
        [SiteNotFound, CredentialsRejected, CantReadConfluence, NoSpaces, NotAdministrator, MissingScope, NothingToCheckYet, General];

    /// <summary>The entries only sync, the spaces dialog and the linking app link to; the site test never fails with these.</summary>
    public static IReadOnlyList<string> Elsewhere { get; } = [SpaceUnreadable, Unreachable, AttachmentsSkipped];

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

    /// <summary>Every step in the order the test runs them.</summary>
    public static IReadOnlyList<string> Steps { get; } = [SiteStep, TokenStep, IdentityStep, SpacesStep, ContentStep, AdminStep];

    /// <summary>What a step does, in the words an admin sees.</summary>
    public static string StepLabel(string? step) => step switch
    {
        SiteStep => "Finding the site",
        TokenStep => "Signing in as the service account",
        IdentityStep => "Reading Confluence",
        SpacesStep => "Listing spaces",
        ContentStep => "Reading comments, folders and changes",
        AdminStep => "Checking access permissions",
        _ => "Checking the connection",
    };

    private static string DefaultTroubleshooting(string step) => step switch
    {
        SiteStep => AtlassianSiteTroubleshooting.SiteNotFound,
        TokenStep => AtlassianSiteTroubleshooting.CredentialsRejected,
        IdentityStep => AtlassianSiteTroubleshooting.CantReadConfluence,
        SpacesStep => AtlassianSiteTroubleshooting.NoSpaces,
        ContentStep => AtlassianSiteTroubleshooting.MissingScope,
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

            var results = new List<ProductProbeResult>();
            foreach (IAtlassianProductProbe probe in ProductProbes())
                results.Add(await probe.RunAsync(site, request.ClientSecret, tokens, run, linked));

            return Combine(results, run, siteUrl, cloudId);
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

    /// <summary>The products a site test probes once the token works, in the order they are reported.</summary>
    private IReadOnlyList<IAtlassianProductProbe> ProductProbes() => [new ConfluenceSiteProbe(httpClients, time)];

    /// <summary>
    /// The test passes if any product passed, warns if none passed but one warned, and otherwise fails
    /// with the first product's failure. <c>Details["products"]</c> names the products that passed,
    /// <c>Details["scopes"]</c> is the union of their confirmed scopes, and <c>Details["product:{name}"]</c>
    /// holds the message of each product that didn't pass.
    /// </summary>
    private static ConnectionTestResult Combine(IReadOnlyList<ProductProbeResult> results, Run run, string siteUrl, string cloudId)
    {
        List<ProductProbeResult> passed = results.Where(r => r.Passed).ToList();
        ConnectionTestResult combined;
        if (passed.Count > 0)
        {
            string[] scopes = passed.SelectMany(r => r.ConfirmedScopes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            combined = ConnectionTestResult.CreateSuccess(
                $"Connected to {siteUrl}: found the site, signed in as the service account, "
                + $"{string.Join("; ", passed.Select(r => r.Message))}.",
                new Dictionary<string, object>
                {
                    ["cloudId"] = cloudId,
                    ["siteUrl"] = siteUrl,
                    ["scopes"] = scopes,
                    ["products"] = string.Join(",", passed.Select(r => r.Product)),
                },
                run.Elapsed);
        }
        else if (results.FirstOrDefault(r => r.Warning) is { } warned)
            combined = run.Warn(warned, siteUrl, cloudId);
        else
            combined = run.Fail(results[0]);

        foreach (ProductProbeResult result in results.Where(r => !r.Passed && r.Message is not null))
            combined.Details!["product:" + result.Product] = result.Message!;
        return combined;
    }

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
