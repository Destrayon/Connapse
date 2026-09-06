using Connapse.Core;
using Connapse.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Connapse.Storage.CloudScope;
using Connapse.Storage.ConnectionTesters;
using Connapse.Storage.Connectors;
using Connapse.Storage.Data;
using Connapse.Storage.Connections;
using Connapse.Storage.Containers;
using Connapse.Storage.Documents;
using Connapse.Storage.Sources;
using Connapse.Storage.FileSystem;
using Connapse.Storage.Folders;
using Connapse.Storage.Settings;
using Connapse.Storage.Llm;
using Connapse.Storage.Vectors;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Connapse.Storage.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers storage-related services into the DI container, including PostgreSQL/pgvector DbContexts, file systems (MinIO/local), embedding and LLM providers, stores, connectors, managed storage, vector utilities, and connection testers.
    /// </summary>
    /// <param name="configuration">Application configuration used to obtain the default database connection string and options for file system, MinIO, embedding, and LLM settings.</param>
    /// <summary>
    /// Registers storage-related services (database, file systems, embedding/LLM providers, stores, utilities, and connection testers) into the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to register storage services into.</param>
    /// <param name="configuration">Configuration used to obtain connection strings and bind storage-related options (for example `DefaultConnection`, `KnowledgeFileSystemOptions`, and `MinioOptions`).</param>
    /// <returns>The same <see cref="IServiceCollection"/> instance with the storage services registered.</returns>
    public static IServiceCollection AddConnapseStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // PostgreSQL + pgvector with dynamic JSON support
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson(); // Required for Dictionary<string, string> serialization
        dataSourceBuilder.UseVector();
        // Cap the application's connection pool. Postgres enforces a server-wide max_connections
        // ceiling; the app pool and the background job runner's pool draw from it concurrently, so
        // an uncapped pool here (Npgsql defaults Maximum Pool Size to 100) can starve the other or
        // exhaust the server under load. Sized to leave headroom for the background pool and admin
        // tooling beneath a typical ceiling; overridable per deployment via Database:MaxPoolSize.
        int appMaxPoolSize = configuration.GetValue<int?>("Database:MaxPoolSize") ?? 40;
        if (appMaxPoolSize <= 0)
            throw new InvalidOperationException("Database:MaxPoolSize must be a positive integer.");
        dataSourceBuilder.ConnectionStringBuilder.MaxPoolSize = appMaxPoolSize;
        var dataSource = dataSourceBuilder.Build();

        services.AddDbContext<KnowledgeDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()));

        // Factory for short-lived per-operation contexts (required for Blazor Server and background services
        // to avoid concurrent DbContext access on the same scoped instance).
        services.AddDbContextFactory<KnowledgeDbContext>(options =>
            options.UseNpgsql(dataSource, npgsql => npgsql.UseVector()), ServiceLifetime.Scoped);

        // Settings store
        services.AddScoped<ISettingsStore, PostgresSettingsStore>();

        // Per-container settings resolver
        services.AddScoped<IContainerSettingsResolver, ContainerSettingsResolver>();

        // Summary LLM resolver — picks the right ILlmProvider per-container based on SummarySettings.LlmProvider
        services.AddScoped<SummaryLlmResolver>();

        // Settings reload service - requires IConfigurationRoot to trigger reload
        services.AddSingleton<ISettingsReloader, SettingsReloader>();

        // Local file system (kept for non-Docker dev)
        services.Configure<KnowledgeFileSystemOptions>(
            configuration.GetSection(KnowledgeFileSystemOptions.SectionName));

        // MinIO (S3-compatible object storage)
        services.Configure<MinioOptions>(
            configuration.GetSection(MinioOptions.SectionName));

        var minioConfig = configuration
            .GetSection(MinioOptions.SectionName)
            .Get<MinioOptions>() ?? new MinioOptions();

        var scheme = minioConfig.UseSSL ? "https" : "http";

        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            new BasicAWSCredentials(minioConfig.AccessKey, minioConfig.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = $"{scheme}://{minioConfig.Endpoint}",
                ForcePathStyle = true
            }));

        services.AddSingleton<MinioFileSystem>();
        services.AddSingleton<LocalKnowledgeFileSystem>();

        // Default IKnowledgeFileSystem: use MinIO when configured, otherwise local
        if (!string.IsNullOrEmpty(minioConfig.AccessKey))
            services.AddSingleton<IKnowledgeFileSystem>(sp => sp.GetRequiredService<MinioFileSystem>());
        else
            services.AddSingleton<IKnowledgeFileSystem>(sp => sp.GetRequiredService<LocalKnowledgeFileSystem>());

        // Embedding providers — resolved at runtime based on EmbeddingSettings.Provider
        services.AddHttpClient<OllamaEmbeddingProvider>();

        // Roles Anywhere CreateSession calls; named so tests can substitute the transport.
        services.AddHttpClient(CloudScope.ConnapseAwsCredentials.RolesAnywhereHttpClientName);
        services.AddScoped<OpenAiEmbeddingProvider>();
        services.AddScoped<AzureOpenAiEmbeddingProvider>();
        services.AddScoped<IEmbeddingProvider>(sp =>
        {
            var settings = sp.GetRequiredService<IOptionsMonitor<EmbeddingSettings>>().CurrentValue;
            return settings.Provider switch
            {
                "OpenAI" => sp.GetRequiredService<OpenAiEmbeddingProvider>(),
                "AzureOpenAI" => sp.GetRequiredService<AzureOpenAiEmbeddingProvider>(),
                _ => sp.GetRequiredService<OllamaEmbeddingProvider>()
            };
        });

        // LLM providers — resolved at runtime based on LlmSettings.Provider.
        // Singleton gate shared across all (transient) Ollama provider instances so local
        // inference is throttled process-wide (see LlmConcurrencyGate).
        services.AddSingleton<LlmConcurrencyGate>();
        services.AddHttpClient<OllamaLlmProvider>();
        services.AddScoped<OpenAiLlmProvider>();
        services.AddScoped<AzureOpenAiLlmProvider>();
        services.AddScoped<AnthropicLlmProvider>();
        services.AddScoped<ILlmProvider>(sp =>
        {
            var settings = sp.GetRequiredService<IOptionsMonitor<LlmSettings>>().CurrentValue;
            return settings.Provider switch
            {
                "OpenAI" => sp.GetRequiredService<OpenAiLlmProvider>(),
                "AzureOpenAI" => sp.GetRequiredService<AzureOpenAiLlmProvider>(),
                "Anthropic" => sp.GetRequiredService<AnthropicLlmProvider>(),
                _ => sp.GetRequiredService<OllamaLlmProvider>()
            };
        });

        // Container store
        services.AddScoped<IContainerStore, PostgresContainerStore>();
        services.AddScoped<IConnectionStore, PostgresConnectionStore>();
        services.AddScoped<ISourceStore, PostgresSourceStore>();

        // Folder store
        services.AddScoped<IFolderStore, PostgresFolderStore>();

        // Document store
        services.AddScoped<IDocumentStore, PostgresDocumentStore>();
        services.AddScoped<DocumentCoordinateReport>();

        // Vector store
        services.AddScoped<IVectorStore, PgVectorStore>();

        // Vector index management (partial IVFFlat indexes per embedding model)
        services.AddScoped<VectorColumnManager>();

        // Vector model discovery (cross-model search support)
        services.AddScoped<VectorModelDiscovery>();

        // Bounds which filesystem roots a source may be pointed at. Bound from configuration
        // only — never the settings table — because the authority a root confers is the same
        // class of thing as a cloud credential, which this project refuses to accept over an
        // API. See SourceSecuritySettings.
        services.Configure<SourceSecuritySettings>(
            configuration.GetSection(SourceSecuritySettings.SectionName));

        // Pins an SFTP connection's host key on first use. Singleton to match the factory
        // that reaches it, and it opens its own scope because IConnectionStore is scoped.
        services.AddSingleton<ISshHostKeyStore, ConnectionSshHostKeyStore>();

        // Connector factory (singleton — shared S3 client and config must outlive requests)
        services.AddSingleton<ConnectorFactory>();
        services.AddSingleton<IConnectorFactory>(sp => sp.GetRequiredService<ConnectorFactory>());

        // Managed storage provider (default: MinIO — Cloud overrides with Azure Blob)
        services.AddSingleton<IManagedStorageProvider, MinioManagedStorageProvider>();

        // Connection testers
        services.AddScoped<OllamaConnectionTester>();
        services.AddScoped<MinioConnectionTester>();
        services.AddScoped<S3ConnectionTester>();
        services.AddScoped<AzureBlobConnectionTester>();

        // Singleton: it holds no per-request state, and the SDK caches and refreshes the resolved
        // credential itself, so a new instance per scope would discard that cache each time.
        // Scoped, not singleton: it now reads the stored credential through a DbContext
        // factory, and a singleton holding a scoped dependency is the classic captive.
        // All singletons, and they have to be: ConnectorFactory is a singleton and takes the
        // credential provider, so anything scoped here is a captive dependency. The integration
        // tests catch that through WebApplicationFactory's scope validation; the container does
        // not, because validation is only on in Development — so this passed locally and failed
        // in CI.
        //
        // Singleton is also the better shape. The store reaches the database through
        // IDbContextFactory and creates a short-lived context per call, which is what makes it
        // safe to hold; and RefreshingAWSCredentials caches on its own window, so one instance
        // means one cache for the whole application rather than one per scope.
        // The store stays scoped: it reaches the database through IDbContextFactory, which this
        // application registers as scoped, so nothing consuming it directly can be a singleton.
        services.AddScoped<IProviderCredentialStore, Connections.PostgresProviderCredentialStore>();
        services.AddScoped<CloudScope.RolesAnywhere.IRolesAnywhereSetupValidator,
            CloudScope.RolesAnywhere.RolesAnywhereSetupValidator>();

        // These two must be singletons, because ConnectorFactory is one and consumes them. The
        // credential provider therefore takes IServiceScopeFactory and opens a scope per refresh
        // rather than holding the store.
        services.AddSingleton<CloudScope.ConnapseAwsCredentials>();

        // Connapse's own Azure app identity — bound from Providers:Azure, consumed by
        // ConnectorFactory and AzureBlobConnectionTester. Singleton for the same reason as
        // ConnapseAwsCredentials: it caches/rebuilds its own credential chain on settings
        // reload, so one instance per process is the correct shape, not one per scope.
        services.Configure<AzureProviderSettings>(
            configuration.GetSection(AzureProviderSettings.SectionName));
        services.AddSingleton<CloudScope.ConnapseAzureCredentials>();

        // Expose Connapse's Azure identity as the ambient TokenCredential for Azure control-plane
        // readers (Graph, and ARM in 4b). Nothing else resolves a bare TokenCredential today —
        // connectors take ConnapseAzureCredentials directly — so this mapping is unambiguous.
        services.TryAddSingleton<Azure.Core.TokenCredential>(
            sp => sp.GetRequiredService<CloudScope.ConnapseAzureCredentials>());

        // Reads the Entra directory (deprovisioning gate + transitive groups) over Graph $batch.
        // Typed HttpClient; the 5-minute decision cache is the shared IMemoryCache singleton, so a
        // transient reader instance per resolve still shares one cache across the process.
        services.AddHttpClient<Connapse.Core.Interfaces.IAzureDirectoryReader, CloudScope.GraphDirectoryReader>();

        // Reads the searcher's effective RBAC-readable azblob scopes from ARM (role assignments
        // minus deny assignments). Typed HttpClient; the 5-minute decision cache is the shared
        // IMemoryCache singleton. TokenCredential is already mapped to ConnapseAzureCredentials (4a).
        services.AddHttpClient<Connapse.Core.Interfaces.IAzureRbacReader, CloudScope.ArmRbacReader>();

        services.AddSingleton<IS3Discovery, CloudScope.S3Discovery>();
        services.AddSingleton<IDirectoryUserLookup, CloudScope.IdentityStoreUserLookup>();
        services.AddSingleton<IAccessGrantsReader, CloudScope.S3AccessGrantsReader>();

        // Registered here rather than in Connapse.Search, whose own registration is a TryAdd for
        // the unrestricted default. This one resolves real grants, and it must win.
        services.AddMemoryCache();
        // Starts undetermined, so a host that never runs the startup migration refuses to answer
        // rather than assuming nothing was being enforced. Connapse.Web completes it from
        // SamlEnforcementLatch; nothing else resolves this today.
        services.TryAddSingleton(new EnforcementMigration());

        services.AddScoped<ISearchScopeResolver, CloudScope.AwsSearchScopeResolver>();

        // Reads the connections, so scoped alongside the store it uses.
        services.AddScoped<IAwsGrantRegions, CloudScope.ConnectionGrantRegions>();
        services.AddScoped<SftpConnectionTester>();
        services.AddScoped<OpenAiConnectionTester>();
        services.AddScoped<AzureOpenAiConnectionTester>();
        services.AddScoped<OpenAiLlmConnectionTester>();
        services.AddScoped<AzureOpenAiLlmConnectionTester>();
        services.AddScoped<AnthropicConnectionTester>();
        services.AddScoped<TeiConnectionTester>();
        services.AddScoped<CohereConnectionTester>();
        services.AddScoped<JinaConnectionTester>();
        services.AddScoped<AzureAIFoundryConnectionTester>();
        services.AddScoped<VoyageConnectionTester>();

        // Cloud scope discovery
        services.AddSingleton<IConnectorScopeCache, ConnectorScopeCache>();

        return services;
    }
}
