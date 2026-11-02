using Docker.DotNet;
using DotNet.Testcontainers.Containers;

namespace Connapse.Integration.Tests;

/// <summary>
/// Starts a Testcontainers container, pulling its image again if the first pull silently failed.
/// <para>
/// Docker can fail a pull when another pull is committing the same layer at the same time —
/// "failed commit on ref layer-sha256:…: lease does not exist" — and CI pulls in parallel: the
/// test projects run as separate processes, and pgvector/pgvector:pg17 and localstack/localstack:3
/// share a Debian base layer. Testcontainers logs that error, reports the image as created, and
/// then fails to create the container with "No such image" (CON-173). Starting again pulls again,
/// and the second pull finds the shared layer already committed, so no wait is needed between them.
/// </para>
/// </summary>
public static class ContainerStart
{
    public const int MaxAttempts = 3;

    public static async Task StartAsync(IContainer container, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await container.StartAsync(ct);
                return;
            }
            catch (DockerImageNotFoundException) when (attempt < MaxAttempts)
            {
                // The container was never created, so StartAsync checks for the image and pulls it again.
            }
        }
    }
}
