using System.Security.Cryptography;
using System.Text.Json;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Connapse.Web.Endpoints;

/// <summary>
/// Optimistic concurrency for the settings categories. GET hands out an entity tag for the
/// settings it returns; a PUT that sends it back in <c>If-Match</c> is applied only while the
/// stored settings still carry that tag, so an administrator saving from an outdated copy is
/// refused instead of silently wiping out a change made in between.
/// </summary>
/// <remarks>
/// The tag is a strong validator (RFC 9110 §8.8.1, §8.8.3) derived from the settings' content,
/// so it needs no version column and stays the same across processes. <c>If-Match</c> is
/// evaluated per RFC 9110 §13.1.1 — <c>*</c> or any listed tag that matches under strong
/// comparison — using ASP.NET Core's own <see cref="EntityTagHeaderValue"/> parser.
/// </remarks>
public static class SettingsConcurrency
{
    private static readonly JsonSerializerOptions TagOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The quoted strong entity tag for <paramref name="settings"/>.</summary>
    public static string ETagFor<T>(T settings) where T : class
    {
        byte[] hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings, TagOptions));
        return $"\"{Convert.ToHexStringLower(hash)}\"";
    }

    /// <summary>
    /// Whether an <c>If-Match</c> header lets a write go ahead against settings whose tag is
    /// <paramref name="currentETag"/>. A header that does not parse never matches.
    /// </summary>
    public static bool IfMatchAllows(StringValues ifMatch, string currentETag)
    {
        if (!EntityTagHeaderValue.TryParseList(ifMatch, out IList<EntityTagHeaderValue>? tags))
            return false;

        var current = EntityTagHeaderValue.Parse(currentETag);
        return tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: true));
    }

    /// <summary>
    /// Saves <paramref name="settings"/>. Without an <c>If-Match</c> header this is the plain
    /// replace it always was. With one, the comparison and the write happen together under the
    /// store's row lock (<see cref="ISettingsStore.UpdateAsync{T}"/>), so no other save can land
    /// between them.
    /// </summary>
    /// <param name="fallback">What GET shows when nothing is stored — the tag the caller saw then.</param>
    /// <returns>The tag of the settings now stored.</returns>
    /// <exception cref="SettingsChangedException">The stored settings no longer match <paramref name="ifMatch"/>.</exception>
    public static async Task<string> SaveAsync<T>(
        ISettingsStore store,
        string category,
        T settings,
        StringValues ifMatch,
        Func<T> fallback,
        CancellationToken ct) where T : class
    {
        if (ifMatch.Count == 0)
        {
            await store.SaveAsync(category, settings, ct);
        }
        else
        {
            await store.UpdateAsync<T>(category, current =>
                IfMatchAllows(ifMatch, ETagFor(current ?? fallback()))
                    ? settings
                    : throw new SettingsChangedException(category), ct);
        }

        return ETagFor(settings);
    }
}

/// <summary>A conditional settings save found the settings changed since the caller read them.</summary>
public sealed class SettingsChangedException(string category)
    : Exception($"The '{category}' settings were changed after you loaded them. Reload them and make your change again.");
