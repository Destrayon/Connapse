namespace Connapse.Core.Interfaces;

/// <summary>
/// What an address the operator typed turns into, and whether it changed on the way.
/// </summary>
/// <param name="Host">The address Connapse should actually dial.</param>
/// <param name="Rewritten">
/// True when <paramref name="Host"/> is not what was asked for. Callers surface this rather than
/// swallowing it: an address that silently becomes a different address is the kind of thing that
/// is impossible to debug six months later.
/// </param>
/// <param name="Reason">Why it changed, phrased for the operator. Null when nothing changed.</param>
public record HostResolution(string Host, bool Rewritten, string? Reason);

/// <summary>
/// Translates an address as the operator understands it into one Connapse can actually reach
/// from wherever it happens to be running.
/// </summary>
/// <remarks>
/// This exists for exactly one problem, and it is worth being precise about which. A LAN address
/// needs no translation — a container reaches <c>192.168.1.50</c> perfectly well, because Docker
/// routes outbound traffic through the host. A hostname cannot be translated at all: it either
/// resolves from the container's DNS or it does not, and nothing here can change that.
/// <para>
/// Loopback is the sole address whose meaning depends on who is asking. To the operator it means
/// the machine in front of them; inside a container it means the container. That one is
/// deterministic, so it is worth fixing — and it is worth fixing <i>only</i> when Connapse is
/// containerised, since running directly on the host makes loopback correct and the rewrite
/// wrong.
/// </para>
/// </remarks>
public interface IContainerHostResolver
{
    /// <summary>
    /// True when Connapse is running inside a container, and loopback therefore does not mean
    /// what the operator means by it.
    /// </summary>
    bool IsContainerised { get; }

    /// <summary>
    /// Returns the address to dial. Unchanged unless it is loopback and Connapse is containerised.
    /// </summary>
    HostResolution Resolve(string? host);
}
