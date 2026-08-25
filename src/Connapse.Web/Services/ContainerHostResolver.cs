using Connapse.Core.Interfaces;
using Connapse.Core.Utilities;

namespace Connapse.Web.Services;

/// <summary>
/// Decides whether Connapse is containerised, then applies <see cref="ContainerHostPolicy"/>.
/// </summary>
/// <remarks>
/// Two signals, because neither is guaranteed on its own. <c>DOTNET_RUNNING_IN_CONTAINER</c> is
/// set by the official .NET base images but not by a hand-rolled Dockerfile on another base;
/// <c>/.dockerenv</c> is created by Docker but not by every other runtime. Either one is enough,
/// and the cost of being wrong is asymmetric: believing we are containerised when we are not
/// rewrites loopback into a name that will not resolve, while believing the reverse merely
/// leaves the operator where they already were.
/// <para>
/// Registered as a singleton and read once. Neither signal can change while the process runs,
/// and touching the filesystem on every keystroke of a host field would be silly.
/// </para>
/// </remarks>
public sealed class ContainerHostResolver : IContainerHostResolver
{
    public ContainerHostResolver()
    {
        IsContainerised = DetectContainer();
    }

    /// <summary>Test seam: lets a test state the environment rather than fake one.</summary>
    internal ContainerHostResolver(bool isContainerised)
    {
        IsContainerised = isContainerised;
    }

    public bool IsContainerised { get; }

    public HostResolution Resolve(string? host) =>
        ContainerHostPolicy.Resolve(host, IsContainerised);

    private static bool DetectContainer()
    {
        string? flag = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        if (bool.TryParse(flag, out bool inContainer) && inContainer)
            return true;

        try
        {
            return File.Exists("/.dockerenv");
        }
        catch
        {
            // A probe that cannot answer must not take the app down over a hint. Not
            // containerised is the safe answer: it changes nothing the operator typed.
            return false;
        }
    }
}
