using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// The memory-search tasks (CON-244): a corpus, and questions an agent answers with search alone. They are not a
/// ranking dataset: the memory-search command scores them. The manifest entry pins their files, so
/// <c>datasets verify --suite memory-search-v1</c> checks them.
/// </summary>
public sealed class MemorySearchAdapter : IDatasetAdapter
{
    public string Name => "memory-search";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"{datasetName} is scored by the memory-search command, not run: dotnet run --project tests/Connapse.Eval -- memory-search --config hybrid");
}
