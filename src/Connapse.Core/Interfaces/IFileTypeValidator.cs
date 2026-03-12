namespace Connapse.Core.Interfaces;

/// <summary>
/// Validates file extensions against the set of types supported by registered parsers.
/// </summary>
public interface IFileTypeValidator
{
    /// <summary>
    /// Returns true if the file's extension (last segment) is supported by a registered parser.
    /// </summary>
    bool IsSupported(string fileName);

    /// <summary>
    /// The set of supported extensions (e.g., ".pdf", ".txt") for error messages.
    /// </summary>
    IReadOnlySet<string> SupportedExtensions { get; }
}
