namespace Connapse.Integration.Tests.CrossSurface;

/// <summary>
/// What one action did, in a form two surfaces can be compared on: the surface's own result, the
/// resulting state, the audit rows it wrote, and the Hangfire jobs it queued. Ids and other
/// per-run values are replaced with placeholders, so the same action run through REST, MCP and
/// the UI yields equal outcomes exactly when it behaved the same.
/// </summary>
public sealed record Outcome(string Result, IReadOnlyList<string> State, IReadOnlyList<string> Audit, IReadOnlyList<string> Jobs)
{
    public bool Equals(Outcome? other) =>
        other is not null && Result == other.Result
        && State.SequenceEqual(other.State) && Audit.SequenceEqual(other.Audit) && Jobs.SequenceEqual(other.Jobs);

    public override int GetHashCode() => HashCode.Combine(Result, State.Count, Audit.Count, Jobs.Count);

    public override string ToString() =>
        $"Result: {Result}\nState:\n{Lines(State)}\nAudit:\n{Lines(Audit)}\nJobs:\n{Lines(Jobs)}";

    private static string Lines(IReadOnlyList<string> items) =>
        items.Count == 0 ? "  (none)" : string.Join("\n", items.Select(i => $"  {i}"));
}

/// <summary>
/// The state an action left behind, as display lines, plus the ids of the resources it touched.
/// Audit rows and jobs are found by those ids, never by time or count, because every integration
/// test shares one host and one database.
/// </summary>
public sealed record StateProjection(IReadOnlyList<string> State, IReadOnlyCollection<string> ResourceIds);
