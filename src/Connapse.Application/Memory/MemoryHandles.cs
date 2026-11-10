using Connapse.Core;
using Connapse.Core.Interfaces;

namespace Connapse.Application.Memory;

/// <summary>
/// The caller's short fact handles (f1, f2, ...). The owner is the signed-in user, or the agent when it acts for no
/// user, as for personal spaces. A caller with neither gets no handles and can't use the shown-only save path.
/// </summary>
public static class MemoryHandles
{
    /// <summary>The handle text for a number: 12 as "f12".</summary>
    public static string Text(int handle) => "f" + handle.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static Guid? Owner(OperationContext context)
    {
        MemoryCaller caller = MemoryRules.Caller(context.User);
        return caller.UserId ?? caller.AgentId;
    }

    /// <summary>Records these facts as shown to the caller and returns their handles; none when there is no store or no owner.</summary>
    public static async Task<IReadOnlyDictionary<Guid, int>> ShowAsync(
        IMemoryHandleStore? store, OperationContext context, IEnumerable<Guid> factIds, CancellationToken ct = default)
    {
        Guid[] ids = [.. factIds];
        if (store is null || ids.Length == 0 || Owner(context) is not { } owner)
            return new Dictionary<Guid, int>();
        return await store.ShowAsync(owner, ids, ct);
    }

    /// <summary>
    /// The fact a handle or full ID names, only when the caller was shown it. Null when it was never shown, or when no
    /// check can be made (no store, no owner): a save tool then refuses rather than act on an unchecked ID.
    /// </summary>
    public static async Task<Guid?> ResolveShownAsync(
        IMemoryHandleStore? store, OperationContext context, string idOrHandle, CancellationToken ct = default)
    {
        if (store is null || string.IsNullOrWhiteSpace(idOrHandle) || Owner(context) is not { } owner)
            return null;
        return await store.ResolveAsync(owner, idOrHandle, ct);
    }
}
