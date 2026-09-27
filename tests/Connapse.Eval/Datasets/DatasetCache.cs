using System.Security.Cryptography;

namespace Connapse.Eval.Datasets;

public sealed class ChecksumMismatchException(string path, string expected, string actual)
    : Exception($"Checksum mismatch for {path}: manifest says {expected}, file is {actual}. "
        + "Delete the file to download it again, or correct the manifest.");

/// <param name="locksRoot">Where file-list lock files live (eval/datasets); needed only for file-list datasets.</param>
public sealed class DatasetCache(string cacheRoot, HttpClient http, string? locksRoot = null)
{
    public const string LockFileName = "files.sha256";
    private const int ParallelDownloads = 8;

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

        if (entry.FileList is { } fileList)
            hashes[LockFileName] = await EnsureFileListAsync(dataset, entry, fileList, directory, allowUnpinned, ct);
        return hashes;
    }

    /// <summary>
    /// Downloads and verifies every file in the dataset's lock file. With <paramref name="pin"/>, the
    /// file list comes from the adapter instead and the lock file is (re)written. Returns the lock
    /// file's own SHA-256, which the run manifest records.
    /// </summary>
    private async Task<string> EnsureFileListAsync(
        string dataset, DatasetEntry entry, DatasetFileList fileList, string directory, bool pin, CancellationToken ct)
    {
        string lockPath = Path.Combine(locksRoot ?? throw new InvalidOperationException("DatasetCache needs locksRoot for file-list datasets."),
            dataset, LockFileName);
        List<(string RelativePath, string? Sha256)> files;
        if (pin)
        {
            if (DatasetAdapters.Get(entry.Adapter) is not IFileListAdapter lister)
                throw new InvalidOperationException($"Adapter '{entry.Adapter}' cannot list files for '{dataset}'.");
            files = lister.ListFiles(directory).Select(f => (f, (string?)null)).ToList();
        }
        else
        {
            if (!File.Exists(lockPath))
                throw new InvalidOperationException($"{dataset} has no lock file at {lockPath}. Run 'datasets pin --suite <suite>' first.");
            files = ReadLock(lockPath);
        }

        string[] actual = new string[files.Count];
        using SemaphoreSlim gate = new(ParallelDownloads);
        await Task.WhenAll(files.Select(async (file, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                string path = Path.Combine(directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await DownloadAsync(FileUrl(fileList.BaseUrl, file.RelativePath), path, ct);
                }
                actual[i] = await Sha256Async(path, ct);
                if (file.Sha256 is not null && !string.Equals(file.Sha256, actual[i], StringComparison.OrdinalIgnoreCase))
                    throw new ChecksumMismatchException(path, file.Sha256, actual[i]);
            }
            finally
            {
                gate.Release();
            }
        }));

        if (pin)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            File.WriteAllLines(lockPath, files
                .Select((f, i) => (f.RelativePath, Line: $"{actual[i]}  {f.RelativePath}"))
                .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
                .Select(f => f.Line));
        }
        return await Sha256Async(lockPath, ct);
    }

    /// <summary>Lines of "sha256  relative/path", the format sha256sum writes.</summary>
    public static List<(string RelativePath, string? Sha256)> ReadLock(string lockPath) =>
        File.ReadLines(lockPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                int split = line.IndexOf("  ", StringComparison.Ordinal);
                if (split != 64)
                    throw new InvalidDataException($"Malformed line in {lockPath}: '{line}'.");
                return (line[(split + 2)..], (string?)line[..split]);
            })
            .ToList();

    private static string FileUrl(string baseUrl, string relativePath) =>
        baseUrl.TrimEnd('/') + "/" + string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString));

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
