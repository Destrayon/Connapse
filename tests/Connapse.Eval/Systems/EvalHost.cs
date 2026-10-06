using System.Runtime.ExceptionServices;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Connapse.Eval.Systems;

/// <summary>
/// Connapse.Web running in-process against throwaway PostgreSQL (pgvector) and MinIO containers,
/// the same way the integration tests' SharedWebAppFixture does it.
/// </summary>
public sealed class EvalHost : IAsyncDisposable
{
    private const string AdminEmail = "admin@eval.connapse.local";
    private const string AdminPassword = "EvalHarnessAdmin1!";
    private const string JwtSecret = "eval-harness-jwt-secret-used-only-inside-throwaway-containers-64";

    /// <summary>Where a snapshot volume is mounted in the PostgreSQL container (#672).</summary>
    private const string SnapshotMount = "/snapshot";

    private readonly PostgreSqlContainer _postgres;
    private readonly MinioContainer _minio;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string? _snapshotVolume;

    private EvalHost(PostgreSqlContainer postgres, MinioContainer minio, WebApplicationFactory<Program> factory,
        string? snapshotVolume, bool restored)
    {
        _postgres = postgres;
        _minio = minio;
        _factory = factory;
        _snapshotVolume = snapshotVolume;
        Restored = restored;
    }

    public IServiceProvider Services => _factory.Services;

    /// <summary>True when the database was restored from <c>snapshotVolume</c> before Connapse started.</summary>
    public bool Restored { get; }

    /// <param name="snapshotVolume">
    /// A Docker volume mounted into PostgreSQL for index snapshots (#672). With <paramref name="restore"/>,
    /// a complete snapshot in it is restored before Connapse starts, so its migrations find the schema
    /// already current; an incomplete or missing one leaves the database empty.
    /// </param>
    public static async Task<EvalHost> StartAsync(
        SystemConfig config, string webContentRoot, EmbeddingDiskCache cache,
        IEmbeddingProvider? embeddingOverride, CancellationToken ct,
        string? snapshotVolume = null, bool restore = false)
    {
        PostgreSqlBuilder postgresBuilder = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg17")
            .WithDatabase("connapse_eval")
            .WithUsername("eval")
            .WithPassword("eval");
        if (snapshotVolume is not null)
            postgresBuilder = postgresBuilder.WithVolumeMount(snapshotVolume, SnapshotMount);
        PostgreSqlContainer postgres = postgresBuilder.Build();
        // Chainguard's MinIO runs as a non-root user that cannot write /data, so run it as root.
        MinioContainer minio = new MinioBuilder()
            .WithImage("cgr.dev/chainguard/minio")
            .WithCreateParameterModifier(parameters => parameters.User = "0")
            .Build();
        try
        {
            await Task.WhenAll(postgres.StartAsync(ct), minio.StartAsync(ct));
            bool restored = false;
            if (restore && snapshotVolume is not null
                && (await postgres.ExecAsync(["test", "-f", $"{SnapshotMount}/complete"], ct)).ExitCode == 0)
            {
                await ShellAsync(postgres,
                    $"PGPASSWORD=eval pg_restore -h localhost -U eval -d connapse_eval -j 8 --no-owner --exit-on-error {SnapshotMount}/db",
                    ct);
                restored = true;
            }

            string minioHost = $"{minio.Hostname}:{minio.GetMappedPublicPort(9000)}";
            WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(webContentRoot);
                // Applied before the harness's own infrastructure settings below, so that even if
                // SystemConfig validation were ever bypassed, the harness's throwaway endpoints
                // still win (UseSetting: last write wins).
                foreach ((string key, string value) in config.Settings)
                    builder.UseSetting(key, value);

                builder.UseSetting("ConnectionStrings:DefaultConnection", postgres.GetConnectionString());
                builder.UseSetting("Knowledge:Storage:MinIO:Endpoint", minioHost);
                builder.UseSetting("Knowledge:Storage:MinIO:AccessKey", MinioBuilder.DefaultUsername);
                builder.UseSetting("Knowledge:Storage:MinIO:SecretKey", MinioBuilder.DefaultPassword);
                builder.UseSetting("Knowledge:Storage:MinIO:UseSSL", "false");
                builder.UseSetting("Knowledge:Summary:Enabled", "false");
                builder.UseSetting("CONNAPSE_ADMIN_EMAIL", AdminEmail);
                builder.UseSetting("CONNAPSE_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Identity:Jwt:Secret", JwtSecret);
                builder.UseSetting("RateLimiting:ApiPermitLimit", "100000");
                builder.UseSetting("RateLimiting:McpPermitLimit", "100000");

                builder.ConfigureTestServices(services =>
                {
                    // The harness measures ranking, not permissions: every eval document is visible.
                    services.RemoveAll<ISearchScopeResolver>();
                    services.AddScoped<ISearchScopeResolver, UnrestrictedScopeResolver>();

                    ServiceDescriptor original = services.Last(d => d.ServiceType == typeof(IEmbeddingProvider));
                    services.RemoveAll<IEmbeddingProvider>();
                    services.AddScoped<IEmbeddingProvider>(sp => new CachingEmbeddingProvider(
                        embeddingOverride ?? (IEmbeddingProvider)original.ImplementationFactory!(sp), cache,
                        sp.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue));
                });
            });

            try
            {
                using HttpClient client = factory.CreateClient();
                await WaitForHealthAsync(client, ct);
                return new EvalHost(postgres, minio, factory, snapshotVolume, restored);
            }
            catch
            {
                // Cleanup failures here are secondary noise; the exception that triggered this
                // catch is the one that must propagate.
                await DisposeAllAsync(factory);
                throw;
            }
        }
        catch
        {
            await DisposeAllAsync(postgres, minio);
            throw;
        }
    }

    /// <summary>
    /// Dumps the database into the snapshot volume, replacing what was there (#672). The "complete" marker
    /// is written last, so an interrupted dump is never restored.
    /// </summary>
    public Task SaveSnapshotAsync(CancellationToken ct)
    {
        if (_snapshotVolume is null)
            throw new InvalidOperationException("This host was started without a snapshot volume.");
        return ShellAsync(_postgres,
            $"rm -rf {SnapshotMount}/db {SnapshotMount}/complete"
            + $" && PGPASSWORD=eval pg_dump -h localhost -U eval -d connapse_eval -Fd -j 8 -Z 0 -f {SnapshotMount}/db"
            + $" && touch {SnapshotMount}/complete",
            ct);
    }

    private static async Task ShellAsync(PostgreSqlContainer postgres, string command, CancellationToken ct)
    {
        var result = await postgres.ExecAsync(["sh", "-c", command], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"'{command}' failed in the PostgreSQL container: {result.Stderr.Trim()}");
    }

    public async ValueTask DisposeAsync() =>
        (await DisposeAllAsync(_factory, _postgres, _minio))?.Throw();

    /// <summary>
    /// Disposes each item independently — one throwing must not skip the rest, or a container
    /// leaks. Returns the first failure instead of throwing, so callers can decide whether to
    /// surface it or let an already-in-flight exception take priority.
    /// </summary>
    private static async Task<ExceptionDispatchInfo?> DisposeAllAsync(params IAsyncDisposable[] disposables)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (IAsyncDisposable disposable in disposables)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }
        return failure;
    }

    private static async Task WaitForHealthAsync(HttpClient client, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync("/health", ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // not ready yet
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Connapse did not become healthy within 60 seconds.");
    }
}
