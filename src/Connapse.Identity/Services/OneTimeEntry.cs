using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;

namespace Connapse.Identity.Services;

/// <summary>
/// A cached value plus the flag that makes taking it single-use. The flag, not the cache removal, is
/// the boundary: <see cref="IMemoryCache"/> exposes no atomic take-and-remove, so two concurrent
/// callers could both read the entry before either removed it.
/// </summary>
internal sealed class OneTimeEntry<T>(T value)
{
    private int _claimed;

    public T Value { get; } = value;

    /// <summary>True for exactly one caller, however many arrive at once.</summary>
    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
}

internal static class OneTimeEntryCacheExtensions
{
    /// <summary>
    /// Takes the <see cref="OneTimeEntry{T}"/> at <paramref name="key"/>. Exactly one caller ever
    /// receives a given entry; the cache removal that follows the claim is only cleanup.
    /// </summary>
    public static bool TryTakeOnce<T>(this IMemoryCache cache, string key, [MaybeNullWhen(false)] out T value)
    {
        if (cache.TryGetValue(key, out OneTimeEntry<T>? entry) && entry is not null && entry.TryClaim())
        {
            cache.Remove(key);
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }
}
