using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// The memory questions (CON-38) and the writer agents' memory operations (CON-111). They are not a ranking
/// dataset: the memory command scores them, against the same notes stored as documents. The manifest entry pins
/// their files, so <c>datasets verify --suite memory-v1</c> checks them.
/// </summary>
public sealed class MemoryQuestionsAdapter : IDatasetAdapter
{
    public string Name => "memory-questions";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"{datasetName} is scored by the memory command, not run: dotnet run --project tests/Connapse.Eval -- memory --config hybrid");
}
