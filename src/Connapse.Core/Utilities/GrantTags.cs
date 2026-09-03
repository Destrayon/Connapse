namespace Connapse.Core.Utilities;

/// <summary>
/// The tags Connapse stamps on the S3 Access Grants it creates, so a cleanup pass can prove a
/// grant is its own before deleting it.
/// </summary>
/// <remarks>
/// One source shared by the writer (which applies them) and the reconciler (which requires them
/// before deleting). AWS forbids tag keys beginning <c>aws:</c>. Tags are write-only through the
/// grant read path — <c>ListAccessGrants</c> does not return them — so the reconciler reads them
/// back per-candidate via <c>ListTagsForResource</c>.
/// </remarks>
public static class GrantTags
{
    /// <summary>Present with <see cref="ManagedValue"/> exactly on grants Connapse created.</summary>
    public const string ManagedKey = "connapse:managed";

    /// <summary>The value <see cref="ManagedKey"/> carries.</summary>
    public const string ManagedValue = "true";
}
