using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// The code questions (CON-35) over four pinned repositories. They are not a ranking dataset: the code command asks
/// them of the code tools (CON-37) and scores them against the grep baseline. The manifest entry pins their files, so
/// <c>datasets verify --suite code-v1</c> checks them.
/// </summary>
public sealed class CodeQuestionsAdapter : IDatasetAdapter
{
    public string Name => "code-questions";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"{datasetName} is scored by the code command, not run: dotnet run --project tests/Connapse.Eval -- code --split tuning");
}
