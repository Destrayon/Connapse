using System.Text.Json;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;
using Microsoft.Extensions.Options;

namespace Connapse.Web.Services;

/// <summary>
/// Deletes S3 Access Grants Connapse created that no connection needs any more.
/// </summary>
/// <remarks>
/// The dangerous direction, so it is fail-closed throughout: it deletes only grants it can prove are
/// its own (by tag), only when it has a <b>complete</b> view of every connection's allowed-locations,
/// and never more than the circuit-breaker limit in one region. Any gap in the input — a connection
/// that will not parse, one that declares no allowlist (and so could index any bucket, making no
/// grant provably orphaned), an unreadable region — makes it hold back rather than guess. This is the
/// same discipline as <c>AwsSearchScopeResolver</c>: an answer that cannot be trusted is not acted on.
/// <para>
/// Single-instance assumption: it deletes any grant tagged <c>connapse:managed</c> whose scope no
/// connection covers, computed from <b>this</b> deployment's connections. Where two Connapse
/// deployments share one AWS account and grant group, one could delete a grant the other still
/// needs. Per-instance scoping (design §6, a <c>connapse:instance</c> tag matched on delete) is a
/// deferred follow-up — the fingerprint source is unresolved and does not affect the common
/// single-instance case.
/// </para>
/// </remarks>
public sealed class GrantReconciliationService(
    IConnectionStore connections,
    IAwsGrantRegions regions,
    IAccessGrantsReader reader,
    IAccessGrantsWriter writer,
    IOptionsMonitor<SamlSignInSettings> saml,
    IOptionsMonitor<GrantReconciliationSettings> settings,
    ILogger<GrantReconciliationService> logger) : IGrantReconciliationService
{
    /// <inheritdoc />
    public async Task<ReconcileReport> ReconcileAsync(bool enforce, CancellationToken ct = default)
    {
        var cfg = settings.CurrentValue;
        if (!cfg.Enabled)
            return ReconcileReport.Abort("Grant reconciliation is switched off.");

        // 1. The complete union of allowed-locations across every S3 connection. Any gap aborts —
        //    an incomplete union makes still-needed grants look orphaned.
        var union = new List<string>();

        IReadOnlyList<Connection> conns;
        try
        {
            conns = await connections.ListAsync(0, int.MaxValue, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grant reconcile: could not list connections; deleting nothing");
            return ReconcileReport.Abort("Could not read the connections, so nothing was deleted.");
        }

        foreach (var connection in conns.Where(c => c.Provider == ConnectionProvider.S3))
        {
            IReadOnlyList<string>? locations;
            try
            {
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(connection.ConfigJson) ? "{}" : connection.ConfigJson);
                locations = StorageLocationPolicy.ReadAllowedLocations(document.RootElement);
            }
            catch (JsonException)
            {
                return ReconcileReport.Abort(
                    $"Connection '{connection.Name}' has configuration that will not parse, so no "
                    + "grant could be proven orphaned. Nothing was deleted.");
            }

            // Absent allowlist -> the connection may index any bucket, so no grant is provably
            // orphaned. Fail closed rather than delete against an unbounded connection.
            if (locations is null)
                return ReconcileReport.Abort(
                    $"Connection '{connection.Name}' declares no allowed-locations, so it could reach "
                    + "any bucket and no grant can be shown orphaned. Nothing was deleted.");

            // Present-but-malformed (StorageLocationPolicy's blank-entry sentinel) is unreadable.
            if (locations.Any(string.IsNullOrWhiteSpace))
                return ReconcileReport.Abort(
                    $"Connection '{connection.Name}' has a malformed allowed-locations list. "
                    + "Nothing was deleted.");

            union.AddRange(locations);
        }

        // 2. Per region: select orphans, circuit-break, confirm provenance, delete.
        string groupId = saml.CurrentValue.GrantGroupId ?? string.Empty;
        int scanned = 0, orphaned = 0, deleted = 0;
        var aborts = new List<string>();
        var failures = new List<GrantWriteFailure>();

        IReadOnlyList<string> regionList;
        try
        {
            regionList = await regions.ListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grant reconcile: could not resolve regions; deleting nothing");
            return ReconcileReport.Abort("Could not resolve the AWS regions, so nothing was deleted.");
        }

        foreach (string region in regionList)
        {
            IReadOnlyList<AccessGrantDetail> grants;
            try
            {
                grants = await reader.ListAllAsync(region, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Grant reconcile: could not read grants in {Region}; skipping",
                    LogSanitizer.Sanitize(region));
                aborts.Add($"Could not read grants in {region}; skipped it.");
                continue;
            }

            scanned += grants.Count;

            var candidates = GrantReconciler.SelectOrphans(grants, union, groupId).Candidates;
            orphaned += candidates.Count;
            if (candidates.Count == 0)
                continue;

            // Circuit breaker: an implausibly large deletion is the signature of a bad union.
            if (candidates.Count > cfg.MaxDeletePerTick)
            {
                string breaker =
                    $"Refused to delete {candidates.Count} grants in {region} (limit "
                    + $"{cfg.MaxDeletePerTick}) — this looks like an incomplete view, not that many "
                    + "real orphans. Nothing was deleted there.";
                logger.LogWarning("Grant reconcile circuit breaker: {Reason}", breaker);
                aborts.Add(breaker);
                continue;
            }

            // Provenance: keep only grants Connapse tagged as its own.
            var arns = candidates
                .Select(c => c.AccessGrantArn)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            IReadOnlyList<string> managedArns;
            try
            {
                managedArns = await writer.FilterManagedAsync(region, arns, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Grant reconcile: could not confirm provenance in {Region}; skipping",
                    LogSanitizer.Sanitize(region));
                aborts.Add($"Could not confirm which grants are Connapse's in {region}; skipped it.");
                continue;
            }

            var managed = managedArns.ToHashSet(StringComparer.Ordinal);
            var toDelete = candidates.Where(c => managed.Contains(c.AccessGrantArn)).ToList();
            if (toDelete.Count == 0)
                continue;

            if (!enforce)
            {
                foreach (var g in toDelete)
                    logger.LogInformation(
                        "Grant reconcile (report only): would delete orphaned grant {Id} scope {Scope} in {Region}",
                        LogSanitizer.Sanitize(g.AccessGrantId), LogSanitizer.Sanitize(g.GrantScope),
                        LogSanitizer.Sanitize(region));
                continue;
            }

            var result = await writer.RevokeAsync(
                region, toDelete.Select(g => g.AccessGrantId).ToList(), ct);

            deleted += result.Deleted.Count;
            failures.AddRange(result.Failed);

            foreach (string id in result.Deleted)
            {
                var g = toDelete.First(x => x.AccessGrantId == id);
                logger.LogWarning(
                    "Deleted orphaned access grant {Id} (scope {Scope}, group {Group}) in {Region}",
                    LogSanitizer.Sanitize(g.AccessGrantId), LogSanitizer.Sanitize(g.GrantScope),
                    LogSanitizer.Sanitize(g.Grantee.Id), LogSanitizer.Sanitize(region));
            }
        }

        return new ReconcileReport(scanned, orphaned, deleted, aborts, failures);
    }
}
