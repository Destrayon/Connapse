namespace Connapse.Core.Interfaces;

/// <summary>
/// Validates whether a file's extension is supported for ingestion.
/// </summary>
public interface IFileTypeValidator
{
    bool IsSupported(string fileName);
    IReadOnlySet<string> SupportedExtensions { get; }
}
