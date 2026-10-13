using System.Net;
using System.Text.Json;
using Connapse.Storage.Connectors.Atlassian;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Connapse.Storage.CloudScope;

/// <summary>
/// Asks Jira, as the site's service account, which of a set of Jira documents an Atlassian account
/// may see. Every document needs browse on its issue; an internal note also needs
/// <c>SERVICEDESK_AGENT</c>; a restricted comment also needs membership of its group (by id) or
/// project role. Both permissions come from one bulk <c>permissions/check</c>. Only an explicit yes
/// admits: an id missing from Jira's answer, and every failure to get an answer, is a denial.
/// </summary>
/// <remarks>
/// Definite answers are cached for <see cref="AllowTtl"/>, failures for <see cref="FailureTtl"/>.
/// Cancellation is never cached and always propagates.
/// </remarks>
public sealed class JiraPermissionChecker(IMemoryCache cache, ILogger<JiraPermissionChecker> logger)
{
    public static readonly TimeSpan AllowTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    private const string Browse = "BROWSE_PROJECTS";
    private const string Agent = "SERVICEDESK_AGENT";
    private const int BatchSize = 1000;

    // What a failed lookup caches: null members deny every membership they would have decided.
    private sealed record GroupSet(HashSet<string>? Ids);
    private sealed record RoleList(List<(string Name, string Url)>? Roles);
    private sealed record RoleActors(HashSet<string>? Accounts, HashSet<string>? Groups);

    /// <summary>For each resource, whether accountId may see it. Same order as <paramref name="resources"/>. Never throws except on caller cancellation.</summary>
    public async Task<bool[]> CanSeeAsync(
        string cloudId, string accountId, IReadOnlyList<JiraResource> resources, AtlassianSiteLookup sites, CancellationToken ct)
    {
        var verdicts = new bool[resources.Count];

        // An empty or anonymous subject would make Jira answer for the service account itself.
        if (resources.Count == 0 || string.IsNullOrWhiteSpace(accountId)
            || accountId.Trim().Equals("anonymous", StringComparison.OrdinalIgnoreCase))
            return verdicts;
        if (!Guid.TryParse(cloudId, out Guid site))
            return verdicts;
        cloudId = site.ToString("D");

        try
        {
            return await DecideAsync(cloudId, accountId, resources, sites, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jira permission checks on site {CloudId} failed; denying {Count} documents", cloudId, resources.Count);
            return verdicts;
        }
    }

    private async Task<bool[]> DecideAsync(
        string cloudId, string accountId, IReadOnlyList<JiraResource> resources, AtlassianSiteLookup sites, CancellationToken ct)
    {
        var verdicts = new bool[resources.Count];
        AtlassianApiClient? client = null;
        bool siteLooked = false;
        async Task<AtlassianApiClient?> ClientAsync()
        {
            if (!siteLooked)
            {
                siteLooked = true;
                if (await sites.SiteForAsync(cloudId, ct) is { } found)
                    client = sites.ClientFor(found.Site, found.Secret, AtlassianProduct.Jira);
            }
            return client;
        }

        // Resources from another site, or with an id that is not a plain number, stay denied.
        bool Usable(JiraResource r) =>
            string.Equals(r.CloudId, cloudId, StringComparison.OrdinalIgnoreCase) && IsId(r.IssueId);

        var browseIds = resources.Where(Usable).Select(r => r.IssueId).Distinct().ToList();
        var agentIds = resources.Where(r => r.Internal && Usable(r)).Select(r => r.IssueId).Distinct().ToList();
        Dictionary<(string Permission, string IssueId), bool> granted =
            await PermissionsAsync(cloudId, accountId, browseIds, agentIds, ClientAsync, ct);

        // Only resources that passed browse (and agent) go on to membership, so a denied issue costs no further calls.
        var candidates = new List<int>();
        for (int i = 0; i < resources.Count; i++)
        {
            JiraResource r = resources[i];
            if (Usable(r) && granted.GetValueOrDefault((Browse, r.IssueId))
                && (!r.Internal || granted.GetValueOrDefault((Agent, r.IssueId))))
                candidates.Add(i);
        }

        HashSet<string>? groups = null;
        if (candidates.Any(i => resources[i].Restriction is not null))
            groups = await GroupsAsync(cloudId, accountId, ClientAsync, ct);

        var roles = new Dictionary<(string ProjectId, string Name), bool>();
        foreach (int i in candidates)
        {
            JiraRestriction? restriction = resources[i].Restriction;
            if (restriction is null)
            {
                verdicts[i] = true;
            }
            else if (restriction.Type == JiraRestrictionType.Group)
            {
                verdicts[i] = !IsDenyAll(restriction.Value) && groups is not null
                    && groups.Contains(restriction.Value.ToLowerInvariant());
            }
            else if (restriction is { Type: JiraRestrictionType.Role, ProjectId: { } projectId } && IsId(projectId))
            {
                if (!roles.TryGetValue((projectId, restriction.Value), out bool member))
                    roles[(projectId, restriction.Value)] = member =
                        await IsRoleMemberAsync(cloudId, accountId, projectId, restriction.Value, groups, ClientAsync, ct);
                verdicts[i] = member;
            }
        }
        return verdicts;
    }

    private async Task<Dictionary<(string, string), bool>> PermissionsAsync(
        string cloudId, string accountId, List<string> browseIds, List<string> agentIds,
        Func<Task<AtlassianApiClient?>> clientAsync, CancellationToken ct)
    {
        var granted = new Dictionary<(string, string), bool>();
        var uncached = new List<(string Permission, string IssueId)>();
        foreach (var asked in browseIds.Select(id => (Browse, id)).Concat(agentIds.Select(id => (Agent, id))))
        {
            if (cache.TryGetValue(PermissionKey(cloudId, accountId, asked.Item1, asked.Item2), out bool known))
                granted[asked] = known;
            else
                uncached.Add(asked);
        }

        foreach (var batch in uncached.Chunk(BatchSize))
        {
            (HashSet<(string, string)>? allowed, TimeSpan lifetime) = await CheckBatchAsync(cloudId, accountId, batch, clientAsync, ct);
            foreach (var asked in batch)
            {
                bool yes = allowed is not null && allowed.Contains(asked);
                granted[asked] = yes;
                cache.Set(PermissionKey(cloudId, accountId, asked.Permission, asked.IssueId), yes, lifetime);
            }
        }
        return granted;
    }

    /// <summary>The (permission, issue) pairs Jira granted out of <paramref name="batch"/>, or null when it gave no usable answer.</summary>
    private async Task<(HashSet<(string, string)>? Allowed, TimeSpan Lifetime)> CheckBatchAsync(
        string cloudId, string accountId, (string Permission, string IssueId)[] batch,
        Func<Task<AtlassianApiClient?>> clientAsync, CancellationToken ct)
    {
        try
        {
            if (await clientAsync() is not { } client)
                return (null, FailureTtl);

            object body = new
            {
                accountId,
                projectPermissions = batch.GroupBy(b => b.Permission)
                    .OrderBy(g => g.Key == Browse ? 0 : 1)
                    .Select(g => new { permissions = new[] { g.Key }, issues = g.Select(b => long.Parse(b.IssueId)).ToArray() })
                    .ToArray(),
            };
            using var response = await client.PostAsync("rest/api/3/permissions/check", body, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Jira permissions/check on site {CloudId} answered {Status}; denying {Count} checks",
                    cloudId, (int)response.StatusCode, batch.Length);
                return (null, FailureTtl);
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("projectPermissions", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                throw new JsonException("permissions/check answered without a projectPermissions array");

            var sent = batch.ToHashSet();
            var allowed = new HashSet<(string, string)>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("permission", out var permission) || permission.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
                    throw new JsonException("permissions/check answered with a malformed entry");

                foreach (var issue in issues.EnumerateArray())
                {
                    if (issue.ValueKind != JsonValueKind.Number || !issue.TryGetInt64(out long id))
                        throw new JsonException("permissions/check answered with a non-numeric issue id");
                    var pair = (permission.GetString()!, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    if (sent.Contains(pair)) // an id Jira returns that was never asked about admits nothing
                        allowed.Add(pair);
                }
            }
            return (allowed, AllowTtl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Rate limits, rejected credentials, timeouts, malformed answers.
            logger.LogWarning(ex, "Jira permissions/check on site {CloudId} failed; denying {Count} checks", cloudId, batch.Length);
            return (null, FailureTtl);
        }
    }

    /// <summary>The account's group ids in lowercase, or null when Jira could not say.</summary>
    private async Task<HashSet<string>?> GroupsAsync(
        string cloudId, string accountId, Func<Task<AtlassianApiClient?>> clientAsync, CancellationToken ct)
    {
        string key = $"atl:jira:{cloudId}:{accountId}:groups";
        if (cache.TryGetValue(key, out GroupSet? cached) && cached is not null)
            return cached.Ids;

        HashSet<string>? ids = await ReadAsync(cloudId, "groups", clientAsync,
            $"rest/api/3/user/groups?accountId={Uri.EscapeDataString(accountId)}", root =>
            {
                if (root.ValueKind != JsonValueKind.Array)
                    throw new JsonException("user/groups answered without an array");
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in root.EnumerateArray())
                    if (group.ValueKind == JsonValueKind.Object && group.TryGetProperty("groupId", out var id)
                        && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                        set.Add(id.GetString()!.Trim().ToLowerInvariant());
                return set;
            }, ct);
        cache.Set(key, new GroupSet(ids), ids is null ? FailureTtl : AllowTtl);
        return ids;
    }

    private async Task<bool> IsRoleMemberAsync(
        string cloudId, string accountId, string projectId, string roleName, HashSet<string>? groups,
        Func<Task<AtlassianApiClient?>> clientAsync, CancellationToken ct)
    {
        // The name is compared only, never sent: it may be ".." or contain "/". Only the numeric id goes in a path.
        string rolesKey = $"atl:jira:{cloudId}:roles:{projectId}";
        if (!cache.TryGetValue(rolesKey, out RoleList? list) || list is null)
        {
            list = new RoleList(await ReadAsync(cloudId, "project roles", clientAsync, $"rest/api/3/project/{projectId}/role", root =>
            {
                if (root.ValueKind != JsonValueKind.Object)
                    throw new JsonException("project roles answered without an object");
                return root.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .Select(p => (p.Name, p.Value.GetString()!))
                    .ToList();
            }, ct));
            cache.Set(rolesKey, list, list.Roles is null ? FailureTtl : AllowTtl);
        }
        if (list.Roles is null)
            return false;

        var matches = list.Roles.Where(r => string.Equals(r.Name, roleName, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
            return false; // missing, or ambiguous
        if (RoleIdOf(matches[0].Url) is not { } roleId)
            return false;

        string actorsKey = $"atl:jira:{cloudId}:actors:{projectId}:{roleId}";
        if (!cache.TryGetValue(actorsKey, out RoleActors? actors) || actors is null)
        {
            actors = await ReadAsync(cloudId, "role actors", clientAsync, $"rest/api/3/project/{projectId}/role/{roleId}", root =>
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("actors", out var all)
                    || all.ValueKind != JsonValueKind.Array)
                    throw new JsonException("role answered without an actors array");
                var accounts = new HashSet<string>(StringComparer.Ordinal);
                var groupIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var actor in all.EnumerateArray())
                {
                    if (actor.ValueKind != JsonValueKind.Object || !actor.TryGetProperty("type", out var type))
                        continue;
                    if (type.ValueKind == JsonValueKind.String && type.GetString() == "atlassian-user-role-actor"
                        && StringAt(actor, "actorUser", "accountId") is { } account)
                        accounts.Add(account);
                    else if (type.ValueKind == JsonValueKind.String && type.GetString() == "atlassian-group-role-actor"
                        && StringAt(actor, "actorGroup", "groupId") is { } groupId)
                        groupIds.Add(groupId.Trim().ToLowerInvariant());
                }
                return new RoleActors(accounts, groupIds);
            }, ct) ?? new RoleActors(null, null);
            cache.Set(actorsKey, actors, actors.Accounts is null ? FailureTtl : AllowTtl);
        }
        if (actors.Accounts is null || actors.Groups is null)
            return false;

        return actors.Accounts.Contains(accountId)
            || (groups is not null && groups.Any(g => !IsDenyAll(g) && actors.Groups.Contains(g)));
    }

    /// <summary>GETs <paramref name="relative"/> and parses a 200; null on any other answer or failure.</summary>
    private async Task<T?> ReadAsync<T>(
        string cloudId, string what, Func<Task<AtlassianApiClient?>> clientAsync, string relative,
        Func<JsonElement, T> parse, CancellationToken ct) where T : class
    {
        try
        {
            if (await clientAsync() is not { } client)
                return null;

            using var response = await client.GetAsync(relative, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Jira {What} read on site {CloudId} answered {Status}; denying what it decides",
                    what, cloudId, (int)response.StatusCode);
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return parse(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Jira {What} read on site {CloudId} failed; denying what it decides", what, cloudId);
            return null;
        }
    }

    private static string? StringAt(JsonElement element, string objectName, string property) =>
        element.TryGetProperty(objectName, out var inner) && inner.ValueKind == JsonValueKind.Object
        && inner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    // The all-zero group stands for a restriction Connapse could not read; no one is ever in it.
    private static bool IsDenyAll(string groupId) =>
        Guid.TryParse(groupId, out Guid id) && id.ToString("D") == JiraRestriction.DenyAll.Value;

    private static bool IsId(string value) => JiraIssueStateStore.IsId(value);

    /// <summary>
    /// A project role's numeric id from its address (<c>…/project/{id}/role/{roleId}</c>): the last
    /// path segment, ignoring trailing whitespace and slashes, when it is 1 to 19 ASCII digits that
    /// fit a 64-bit id. Null otherwise. The address itself is never followed.
    /// </summary>
    internal static string? RoleIdOf(string? url)
    {
        string trimmed = (url ?? "").TrimEnd().TrimEnd('/');
        string last = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return IsId(last) && long.TryParse(last, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? last
            : null;
    }

    private static string PermissionKey(string cloudId, string accountId, string permission, string issueId) =>
        $"atl:jira:{cloudId}:{accountId}:{permission}:{issueId}";
}
