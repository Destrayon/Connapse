using System.Security.Cryptography;

namespace Connapse.Eval.Datasets;

public sealed class ChecksumMismatchException(string path, string expected, string actual)
    : Exception($"Checksum mismatch for {path}: manifest says {expected}, file is {actual}. "
        + "Delete the file to download it again, or correct the manifest.");

public sealed class DatasetCache(string cacheRoot, HttpClient http)
{
    public string DirectoryFor(string dataset, DatasetEntry entry) =>
        Path.Combine(cacheRoot, "datasets", dataset, entry.Version);

    /// <summary>
    /// Downloads missing files and verifies every file against the manifest. With
    /// <paramref name="allowUnpinned"/>, files that have no SHA-256 in the manifest are accepted and
    /// their hash returned so the caller can pin it.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> EnsureAsync(
        string dataset, DatasetEntry entry, bool allowUnpinned, CancellationToken ct)
    {
        string directory = DirectoryFor(dataset, entry);
        Directory.CreateDirectory(directory);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);

        foreach (DatasetFile file in entry.Files)
        {
            string path = Path.Combine(directory, file.Name);
            if (!File.Exists(path))
                await DownloadAsync(file.Url, path, ct);

            string actual = await Sha256Async(path, ct);
            if (file.Sha256 is null)
            {
                if (!allowUnpinned)
                    throw new InvalidOperationException(
                        $"{dataset}/{file.Name} has no pinned SHA-256 in the manifest. Run 'datasets pin --suite <suite>' first.");
            }
            else if (!string.Equals(file.Sha256, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChecksumMismatchException(path, file.Sha256, actual);
            }
            hashes[file.Name] = actual;
        }
        return hashes;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private async Task DownloadAsync(string url, string path, CancellationToken ct)
    {
        string partial = path + ".part";
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using (FileStream output = File.Create(partial))
            await response.Content.CopyToAsync(output, ct);
        File.Move(partial, path, overwrite: true);
    }
}
