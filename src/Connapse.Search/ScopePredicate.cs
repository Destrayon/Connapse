using Connapse.Core;

namespace Connapse.Search;

/// <summary>
/// The SQL rule for which documents (alias <c>d</c>) a search may reach, shared by every query that admits
/// documents in Connapse.Search, so the keyword search and the link walk cannot drift apart.
/// </summary>
internal static class ScopePredicate
{
    /// <summary>A source whose remote revoked access (a public repository gone private) is left out, whatever else the caller asked for.</summary>
    public const string NotRevoked =
        "NOT EXISTS (SELECT 1 FROM sources s WHERE s.id = d.source_id AND s.access_revoked_at IS NOT NULL)";

    /// <summary>Adds the scope rule to <paramref name="whereClauses"/>, its values to <paramref name="parameters"/>.</summary>
    public static void Append(List<string> whereClauses, List<object> parameters, SearchScopes scopes)
    {
        if (scopes is not { IsUnrestricted: false })
            return;

        if (scopes.IsEmpty)
        {
            // No grants (no principal, no access, or the resolver failed) says nothing about
            // documents the cloud has no opinion on. Only cloud-governed documents — those
            // with a resource_uri — are excluded; everything else (uploads, SFTP, ...) still
            // shows. This also means a resolver outage degrades to "you see the non-cloud
            // documents", not "you see nothing".
            whereClauses.Add("d.resource_uri IS NULL");
            return;
        }

        // A document with no resource URI has no cloud coordinate to check permissions
        // against — uploads and connectors that never report one (SFTP, filesystem,
        // MinIO) are like this by design, not by accident. Such a document falls back to
        // Connapse's own access control (container/source reachability) instead of cloud
        // grants, so it is admitted unconditionally here. Cloud scope filtering only
        // narrows the subset of documents that do carry a cloud address.
        var ors = new List<string>();
        foreach (GrantMatch match in scopes.Matches)
        {
            int scopeIdx = parameters.Count;

            // A grant scoped to one object is exact-matched rather than treated as a
            // prefix: as a prefix it would also let through a sibling like
            // "report.pdf.bak", a different object nobody granted.
            string predicate = match.IsExact
                ? $"d.resource_uri = {{{scopeIdx}}}"
                : $"d.resource_uri LIKE {{{scopeIdx}}} ESCAPE '{SearchScopes.LikeEscape}'";

            parameters.Add(match.IsExact
                ? match.Value
                : SearchScopes.ToLikePattern(match.Value));

            // A custom source's grant also needs a key the user holds; the keys are a parameter,
            // never spliced into the SQL.
            if (match.AccessKeys is { } keys)
            {
                int keysIdx = parameters.Count;
                predicate = $"({predicate} AND d.access_keys && {{{keysIdx}}})";
                parameters.Add(keys.ToArray());
            }

            ors.Add(predicate);
        }

        whereClauses.Add($"(d.resource_uri IS NULL OR ({string.Join(" OR ", ors)}))");
    }
}
