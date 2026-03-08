using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Testcontainers.LocalStack;

namespace Connapse.Integration.Tests;

/// <summary>
/// Shared fixture that starts a LocalStack container for S3-only testing.
/// Sets environment variables so S3Connector's DefaultAWSCredentials chain
/// redirects to LocalStack automatically.
/// </summary>
public sealed class LocalStackFixture : IAsyncLifetime
{
    public const string AccessKey = "test";
    public const string SecretKey = "test";
    public const string Region = "us-east-1";

    private readonly LocalStackContainer _container = new LocalStackBuilder()
        .WithImage("localstack/localstack:3")
        .WithEnvironment("SERVICES", "s3")
        .Build();

    public IAmazonS3 S3Client { get; private set; } = null!;
    public string ServiceUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        ServiceUrl = _container.GetConnectionString();

        // Set env vars so S3Connector's DefaultAWSCredentials chain resolves to LocalStack
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", AccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", SecretKey);
        Environment.SetEnvironmentVariable("AWS_REGION", Region);
        Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_S3", ServiceUrl);

        S3Client = new AmazonS3Client(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = Region
            });
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", null);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", null);
        Environment.SetEnvironmentVariable("AWS_REGION", null);
        Environment.SetEnvironmentVariable("AWS_ENDPOINT_URL_S3", null);

        S3Client.Dispose();
        await _container.DisposeAsync();
    }

    public async Task<string> CreateBucketAsync(string name, CancellationToken ct = default)
    {
        await S3Client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = name,
            UseClientRegion = true
        }, ct);
        return name;
    }
}
