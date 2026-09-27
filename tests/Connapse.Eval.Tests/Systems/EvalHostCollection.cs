namespace Connapse.Eval.Tests.Systems;

/// <summary>
/// Tests that start an in-process Connapse host run one at a time: Hangfire keeps its job storage
/// in a process-wide static, so a second host started in parallel receives the first host's jobs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EvalHostCollection
{
    public const string Name = "Eval host";
}
