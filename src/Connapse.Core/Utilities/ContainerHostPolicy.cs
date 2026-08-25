using Connapse.Core.Interfaces;

namespace Connapse.Core.Utilities;

/// <summary>
/// The rewrite rules themselves, with no opinion about whether Connapse is in a container.
/// </summary>
/// <remarks>
/// Split from <see cref="IContainerHostResolver"/> so the rules can be tested without an
/// environment to fake. Whether we are containerised is a fact about the process; what to do
/// about it is a decision, and only the decision is interesting to test.
/// </remarks>
public static class ContainerHostPolicy
{
    /// <summary>
    /// The name Docker Desktop resolves to the host machine from inside a container. On Linux
    /// hosts it exists only when the container was started with
    /// <c>--add-host=host.docker.internal:host-gateway</c>, which is why the rewrite is
    /// surfaced to the operator rather than applied quietly — if it is not going to resolve,
    /// they need to know that is what they are now depending on.
    /// </summary>
    public const string DockerHostAlias = "host.docker.internal";

    /// <summary>
    /// True when <paramref name="host"/> names the machine asking, rather than a machine.
    /// </summary>
    /// <remarks>
    /// Covers the IPv4 loopback <i>range</i>, not just <c>127.0.0.1</c>: every address in
    /// 127.0.0.0/8 is loopback, and <c>127.0.1.1</c> in particular is what Debian puts in
    /// /etc/hosts for the machine's own name.
    /// </remarks>
    public static bool IsLoopback(string? host)
    {
        string trimmed = (host ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;

        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed is "::1" or "[::1]") return true;

        // 127.x.y.z, checked by parsing rather than by prefix: "127.0.0.1.example.com" is a
        // perfectly ordinary hostname that starts with "127.".
        string[] octets = trimmed.Split('.');
        if (octets.Length != 4) return false;

        if (!byte.TryParse(octets[0], out byte first) || first != 127) return false;

        return octets.Skip(1).All(o => byte.TryParse(o, out _));
    }

    /// <summary>
    /// The address to dial, given whether Connapse is containerised.
    /// </summary>
    public static HostResolution Resolve(string? host, bool containerised)
    {
        string trimmed = (host ?? string.Empty).Trim();

        if (!containerised || !IsLoopback(trimmed))
            return new HostResolution(trimmed, Rewritten: false, Reason: null);

        return new HostResolution(
            DockerHostAlias,
            Rewritten: true,
            Reason: $"Connapse runs in a container, where '{trimmed}' is the container itself "
                    + $"rather than your machine. Using {DockerHostAlias} instead, which is how a "
                    + "container reaches the host it runs on.");
    }
}
