using Connapse.Core.Interfaces;

namespace Connapse.Ingestion;

/// <summary>
/// Validates file types by aggregating supported extensions from all registered document parsers.
/// </summary>
public class FileTypeValidator : IFileTypeValidator
{
    private readonly HashSet<string> _supportedExtensions;

    public FileTypeValidator(IEnumerable<IDocumentParser> parsers)
    {
        _supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parser in parsers)
        {
            foreach (var ext in parser.SupportedExtensions)
                _supportedExtensions.Add(ext);
        }
    }

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    public bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && _supportedExtensions.Contains(ext);
    }
}
