using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// The memory-learned tasks (CON-244, realistic arm): a corpus, documents that arrive later, and two sessions of related
/// questions. They are not a ranking dataset: the memory-learned command scores them. The manifest entry pins their
/// files, so <c>datasets verify --suite memory-learned-v1</c> checks them.
/// </summary>
public sealed class MemoryLearnedAdapter : IDatasetAdapter
{
    public string Name => "memory-learned";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"{datasetName} is scored by the memory-learned command, not run: dotnet run --project tests/Connapse.Eval -- memory-learned --config hybrid");
}
