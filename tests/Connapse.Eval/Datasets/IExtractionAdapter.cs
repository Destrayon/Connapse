using Connapse.Eval.Checks;

namespace Connapse.Eval.Datasets;

/// <summary>An adapter whose dataset is scored by extraction checks rather than relevance judgments.</summary>
public interface IExtractionAdapter : IDatasetAdapter
{
    Task<ExtractionSpec> LoadChecksAsync(string directory, CancellationToken ct);
}

/// <summary>An adapter whose files are listed by the dataset itself (see <see cref="DatasetFileList"/>).</summary>
public interface IFileListAdapter : IDatasetAdapter
{
    /// <summary>Relative paths of every file to download, read from the manifest files already in <paramref name="directory"/>.</summary>
    IReadOnlyList<string> ListFiles(string directory);
}
