using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// The memory-agent facts and tasks (CON-313): made-up project-manager facts seeded into a memory space, and find and
/// save tasks an agent does with the memory tools. They are not a ranking dataset: the memory-agent command scores them.
/// The manifest entry pins their files, so <c>datasets verify --suite memory-agent-v1</c> checks them.
/// </summary>
public sealed class MemoryAgentAdapter : IDatasetAdapter
{
    public string Name => "memory-agent";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"{datasetName} is scored by the memory-agent command, not run: dotnet run --project tests/Connapse.Eval -- memory-agent");
}
