using System.Security.Cryptography;
using System.Text;

namespace Connapse.Eval.Systems;

/// <summary>One little-endian float32 file per (namespace, text), under eval/.cache/embeddings.</summary>
public sealed class EmbeddingDiskCache(string root)
{
    public float[]? TryGet(string cacheNamespace, string text)
    {
        string path = PathFor(cacheNamespace, text);
        if (!File.Exists(path))
            return null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            // Being replaced by a concurrent Put of the same key; recomputing is harmless.
            return null;
        }
        float[] vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    public void Put(string cacheNamespace, string text, float[] vector)
    {
        string path = PathFor(cacheNamespace, text);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, bytes);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Two documents embedding the same text race to write the same key; on Windows the losing
                // rename is denied while the winner's file is open. Only that case is absorbed: the file
                // already there must hold exactly this vector. The winner's handle closes within
                // moments, so both the rename and the read-back get a few short retries; anything else
                // propagates.
                if (SameContent(path, bytes))
                {
                    File.Delete(temporary);
                    return;
                }
                if (attempt == 5)
                {
                    File.Delete(temporary);
                    throw;
                }
                Thread.Sleep(25 * attempt);
            }
        }
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private string PathFor(string cacheNamespace, string text)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return Path.Combine(root, Sanitize(cacheNamespace), key[..2], key + ".bin");
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
}
