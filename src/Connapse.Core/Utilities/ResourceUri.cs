namespace Connapse.Core.Utilities;

/// <summary>
/// Builds the absolute address a connector reports for a file it lists.
/// </summary>
/// <remarks>
/// One place, because two things have to agree about this format and they are written far apart:
/// the connectors that mint these, and the permission filter that will match scopes against them.
/// A format decided independently in each connector is the drift that
/// <see cref="StorageLocationPolicy"/> exists to prevent one layer down.
/// <para>
/// The shape is <c>scheme://authority/path</c>, where the authority is whatever makes the path
/// unambiguous — a bucket for S3, an account and container for Azure. Getting the authority wrong
/// is not cosmetic: two connections whose URIs collide would let a permission rule written for one
/// silently match the other's documents.
/// </para>
/// </remarks>
public static class ResourceUri
{
    /// <summary>An S3 object, as <c>s3://bucket/key</c>.</summary>
    /// <remarks>
    /// The key is carried exactly as S3 reported it, including any leading or doubled slash. S3
    /// treats <c>a</c>, <c>/a</c> and <c>//a</c> as three distinct keys, and normalising here would
    /// merge documents that are not the same object.
    /// </remarks>
    public static string ForS3(string bucket, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        return $"s3://{bucket}/{key}";
    }

    /// <summary>An Azure blob, as <c>azblob://account/container/blob</c>.</summary>
    /// <remarks>
    /// The account is part of the address, not context. Container names are unique only within an
    /// account, so omitting it would give two connections holding a container of the same name one
    /// identical URI — and a rule granting one would reach the other's documents.
    /// </remarks>
    public static string ForAzureBlob(string storageAccount, string container, string blob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageAccount);
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        return $"azblob://{storageAccount}/{container}/{blob}";
    }
}
