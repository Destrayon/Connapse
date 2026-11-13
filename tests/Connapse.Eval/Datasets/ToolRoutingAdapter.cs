using Connapse.Eval.Model;

namespace Connapse.Eval.Datasets;

/// <summary>
/// The tool-routing tasks (CON-346): fixtures, and tasks an agent finishes only by running one named Connapse tool.
/// They are not a ranking dataset: the tool-routing command scores them. The manifest entry pins their files, so
/// <c>datasets verify --suite tool-routing-v1</c> checks them.
/// </summary>
public sealed class ToolRoutingAdapter : IDatasetAdapter
{
    public string Name => "tool-routing";

    public Task<EvalDataset> LoadAsync(string datasetName, DatasetEntry entry, string directory, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"{datasetName} is scored by the tool-routing command, not run: dotnet run --project tests/Connapse.Eval -- tool-routing --tool-list compact --tool-search off");
}
