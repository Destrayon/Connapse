using Connapse.Core.Interfaces;

namespace Connapse.Ingestion.Validation;

/// <summary>
/// Validates file extensions against the union of all registered parser capabilities.
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

    public bool IsSupported(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return _supportedExtensions.Contains(extension);
    }

    public IReadOnlySet<string> SupportedExtensions => _supportedExtensions;
}
