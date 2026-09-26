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
        byte[] bytes = File.ReadAllBytes(path);
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
        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(string cacheNamespace, string text)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return Path.Combine(root, Sanitize(cacheNamespace), key[..2], key + ".bin");
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
}
