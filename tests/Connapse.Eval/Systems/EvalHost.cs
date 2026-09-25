using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    private readonly PostgreSqlContainer _postgres;
    private readonly MinioContainer _minio;
    private readonly WebApplicationFactory<Program> _factory;

    private EvalHost(PostgreSqlContainer postgres, MinioContainer minio, WebApplicationFactory<Program> factory)
    {
        _postgres = postgres;
        _minio = minio;
        _factory = factory;
    }

    public IServiceProvider Services => _factory.Services;

    public static async Task<EvalHost> StartAsync(
        SystemConfig config, string webContentRoot, EmbeddingDiskCache cache,
        IEmbeddingProvider? embeddingOverride, CancellationToken ct)
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg17")
            .WithDatabase("connapse_eval")
            .WithUsername("eval")
            .WithPassword("eval")
            .Build();
        // Chainguard's MinIO runs as a non-root user that cannot write /data, so run it as root.
        MinioContainer minio = new MinioBuilder()
            .WithImage("cgr.dev/chainguard/minio")
            .WithCreateParameterModifier(parameters => parameters.User = "0")
            .Build();
        await Task.WhenAll(postgres.StartAsync(ct), minio.StartAsync(ct));

        string minioHost = $"{minio.Hostname}:{minio.GetMappedPublicPort(9000)}";
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(webContentRoot);
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
            foreach ((string key, string value) in config.Settings)
                builder.UseSetting(key, value);

            builder.ConfigureTestServices(services =>
            {
                // The harness measures ranking, not permissions: every eval document is visible.
                services.RemoveAll<ISearchScopeResolver>();
                services.AddScoped<ISearchScopeResolver, UnrestrictedScopeResolver>();

                ServiceDescriptor original = services.Last(d => d.ServiceType == typeof(IEmbeddingProvider));
                services.RemoveAll<IEmbeddingProvider>();
                services.AddScoped<IEmbeddingProvider>(sp => new CachingEmbeddingProvider(
                    embeddingOverride ?? (IEmbeddingProvider)original.ImplementationFactory!(sp), cache));
            });
        });

        using HttpClient client = factory.CreateClient();
        await WaitForHealthAsync(client, ct);
        return new EvalHost(postgres, minio, factory);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
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
